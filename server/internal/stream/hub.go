package stream

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"sync"
	"time"
)

// FeatureID is the terms_consent feature key for live preview.
// Must match the seeded featured term in terms_content_seeder.go ("live_view").
const FeatureID = "live_view"

// Errs returned to handlers (mapped to HTTP status).
var (
	ErrDisabled          = errors.New("live stream disabled")
	ErrTooManyStreams    = errors.New("too many concurrent streams")
	ErrTooManyWatchers   = errors.New("too many watchers for employee")
	ErrFrameTooLarge     = errors.New("frame exceeds max bytes")
	ErrNotWanted         = errors.New("no watchers for employee")
	ErrInvalidTicket     = errors.New("invalid or expired watch ticket")
)

const watchTicketTTL = 60 * time.Second

type watchTicket struct {
	EmployeeID string
	UserID     string
	ExpiresAt  time.Time
}

// Config holds runtime caps for the in-memory hub.
type Config struct {
	Enabled                bool
	MaxFPS                 int
	FrameMaxBytes          int
	MaxStreams             int
	MaxWatchersPerEmployee int
	IdleSec                int
	TestFrame              bool
}

// DefaultConfig returns Phase-1 defaults from the plan.
func DefaultConfig() Config {
	return Config{
		Enabled:                true,
		MaxFPS:                 10,
		FrameMaxBytes:          524288,
		MaxStreams:             25,
		MaxWatchersPerEmployee: 10,
		IdleSec:                90,
		TestFrame:              false,
	}
}

// Capability is advertised by the desktop client in its hello message.
type Capability struct {
	Platform         string
	StreamAvailable  bool
	Version          string
	Monitors         []MonitorInfo
	SelectedMonitor  int
}

// MonitorInfo is one display the client can capture.
type MonitorInfo struct {
	Index     int    `json:"index"`
	Name      string `json:"name"`
	Width     int    `json:"width"`
	Height    int    `json:"height"`
	IsPrimary bool   `json:"isPrimary"`
}

// Frame is one JPEG preview (latest-wins).
type Frame struct {
	JPEG []byte
	Seq  uint64
	At   time.Time
}

// ControlEvent is sent to the push-socket handler (start/stop/select_monitor).
type ControlEvent struct {
	Type         string // "start" | "stop" | "select_monitor"
	MonitorIndex int    // for select_monitor
}

// EmployeeSnapshot is hub-side state for the employees REST list / watch status.
type EmployeeSnapshot struct {
	Wanted           bool
	Streaming        bool
	StreamAvailable  bool
	WatcherCount     int
	ClientConnected  bool
	Seq              uint64
	LastFrameAt      time.Time
	Monitors         []MonitorInfo
	SelectedMonitor  int
}

type mailbox struct {
	frame           []byte
	seq             uint64
	lastFrameAt     time.Time
	lastActivity    time.Time
	lastPushAt      time.Time
	wanted          bool
	clientConnected bool
	cap             Capability
	watchers        map[uint64]chan Frame
	nextWatcherID   uint64
	ctrl            chan ControlEvent
	countsAsStream  bool
}

// Hub is an in-memory latest-frame mailbox + watcher fan-out. Nothing is persisted.
type Hub struct {
	cfg Config

	mu             sync.RWMutex
	boxes          map[string]*mailbox
	streamingCount int
	tickets        map[string]watchTicket

	stopIdle chan struct{}
	wg       sync.WaitGroup
}

// NewHub creates a hub and starts the idle reaper.
func NewHub(cfg Config) *Hub {
	if cfg.MaxFPS <= 0 {
		cfg.MaxFPS = 10
	}
	if cfg.FrameMaxBytes <= 0 {
		cfg.FrameMaxBytes = 524288
	}
	if cfg.MaxStreams <= 0 {
		cfg.MaxStreams = 25
	}
	if cfg.MaxWatchersPerEmployee <= 0 {
		cfg.MaxWatchersPerEmployee = 10
	}
	if cfg.IdleSec <= 0 {
		cfg.IdleSec = 90
	}

	h := &Hub{
		cfg:      cfg,
		boxes:    make(map[string]*mailbox),
		tickets:  make(map[string]watchTicket),
		stopIdle: make(chan struct{}),
	}
	h.wg.Add(1)
	go h.idleLoop()
	return h
}

// Config returns a copy of the hub config.
func (h *Hub) Config() Config {
	return h.cfg
}

// Close stops the idle loop. Call on server shutdown.
func (h *Hub) Close() {
	select {
	case <-h.stopIdle:
	default:
		close(h.stopIdle)
	}
	h.wg.Wait()

	h.mu.Lock()
	defer h.mu.Unlock()
	for _, m := range h.boxes {
		h.clearMailboxLocked(m)
	}
	h.boxes = make(map[string]*mailbox)
	h.tickets = make(map[string]watchTicket)
	h.streamingCount = 0
}

func (h *Hub) idleLoop() {
	defer h.wg.Done()
	ticker := time.NewTicker(10 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-h.stopIdle:
			return
		case <-ticker.C:
			h.reapIdle()
		}
	}
}

func (h *Hub) reapIdle() {
	idleFor := time.Duration(h.cfg.IdleSec) * time.Second
	now := time.Now()

	h.mu.Lock()
	defer h.mu.Unlock()

	for empID, m := range h.boxes {
		if len(m.watchers) > 0 {
			continue
		}
		if m.wanted {
			// Watchers gone but wanted stuck — force stop.
			m.wanted = false
			h.sendCtrlLocked(m, "stop")
			h.unmarkStreamLocked(m)
			m.frame = nil
			continue
		}
		if !m.clientConnected && len(m.frame) == 0 && now.Sub(m.lastActivity) > idleFor {
			delete(h.boxes, empID)
			continue
		}
		if len(m.frame) > 0 && now.Sub(m.lastActivity) > idleFor && !m.clientConnected {
			m.frame = nil
			m.seq = 0
		}
	}

	for id, t := range h.tickets {
		if now.After(t.ExpiresAt) {
			delete(h.tickets, id)
		}
	}
}

func (h *Hub) getOrCreateLocked(empID string) *mailbox {
	m, ok := h.boxes[empID]
	if ok {
		return m
	}
	m = &mailbox{
		watchers:     make(map[uint64]chan Frame),
		ctrl:         make(chan ControlEvent, 4),
		lastActivity: time.Now(),
		cap: Capability{
			StreamAvailable: false,
		},
	}
	h.boxes[empID] = m
	return m
}

func (h *Hub) sendCtrlLocked(m *mailbox, typ string) {
	h.sendCtrlEventLocked(m, ControlEvent{Type: typ})
}

func (h *Hub) sendCtrlEventLocked(m *mailbox, ev ControlEvent) {
	if m.ctrl == nil {
		return
	}
	select {
	case m.ctrl <- ev:
	default:
		select {
		case <-m.ctrl:
		default:
		}
		select {
		case m.ctrl <- ev:
		default:
		}
	}
}

// SelectMonitor asks the push client to switch capture target (Phase 2 multi-monitor).
func (h *Hub) SelectMonitor(empID string, index int) {
	h.mu.Lock()
	defer h.mu.Unlock()
	m, ok := h.boxes[empID]
	if !ok || !m.clientConnected {
		return
	}
	if index < 0 {
		index = 0
	}
	if n := len(m.cap.Monitors); n > 0 && index >= n {
		index = n - 1
	}
	m.cap.SelectedMonitor = index
	m.lastActivity = time.Now()
	h.sendCtrlEventLocked(m, ControlEvent{Type: "select_monitor", MonitorIndex: index})
}

func (h *Hub) markStreamLocked(m *mailbox) error {
	if m.countsAsStream {
		return nil
	}
	if h.streamingCount >= h.cfg.MaxStreams {
		return ErrTooManyStreams
	}
	m.countsAsStream = true
	h.streamingCount++
	return nil
}

func (h *Hub) unmarkStreamLocked(m *mailbox) {
	if !m.countsAsStream {
		return
	}
	m.countsAsStream = false
	if h.streamingCount > 0 {
		h.streamingCount--
	}
}

func (h *Hub) clearMailboxLocked(m *mailbox) {
	for id, ch := range m.watchers {
		close(ch)
		delete(m.watchers, id)
	}
	m.frame = nil
	m.wanted = false
	h.unmarkStreamLocked(m)
}

// RegisterClient attaches the push-socket control channel for an employee.
// Returns a receive-only control channel; call UnregisterClient on disconnect.
func (h *Hub) RegisterClient(empID string) (<-chan ControlEvent, bool) {
	if !h.cfg.Enabled {
		return nil, false
	}
	h.mu.Lock()
	defer h.mu.Unlock()

	m := h.getOrCreateLocked(empID)
	m.clientConnected = true
	m.lastActivity = time.Now()

	if m.wanted {
		h.sendCtrlLocked(m, "start")
	}
	return m.ctrl, true
}

// UnregisterClient marks the push client gone and stops streaming accounting.
func (h *Hub) UnregisterClient(empID string) {
	h.mu.Lock()
	defer h.mu.Unlock()

	m, ok := h.boxes[empID]
	if !ok {
		return
	}
	m.clientConnected = false
	m.lastActivity = time.Now()
	if m.wanted {
		// Keep wanted + stream slot — next reconnect gets start. Clear frame so UI reconnects.
		m.frame = nil
		return
	}
	h.unmarkStreamLocked(m)
}

// SetCapability stores the client's hello advertisement.
func (h *Hub) SetCapability(empID string, cap Capability) {
	h.mu.Lock()
	defer h.mu.Unlock()
	m := h.getOrCreateLocked(empID)
	m.cap = cap
	m.lastActivity = time.Now()
}

// PushFrame stores the latest JPEG and fans out to watchers (latest-wins per watcher).
func (h *Hub) PushFrame(empID string, jpeg []byte) error {
	if !h.cfg.Enabled {
		return ErrDisabled
	}
	if len(jpeg) == 0 {
		return nil
	}
	if len(jpeg) > h.cfg.FrameMaxBytes {
		return ErrFrameTooLarge
	}

	h.mu.Lock()
	defer h.mu.Unlock()

	m, ok := h.boxes[empID]
	if !ok || !m.wanted {
		return ErrNotWanted
	}

	minInterval := time.Second / time.Duration(h.cfg.MaxFPS)
	if !m.lastPushAt.IsZero() && time.Since(m.lastPushAt) < minInterval {
		return nil // drop — over FPS budget
	}

	// Copy so callers can reuse their buffer.
	buf := make([]byte, len(jpeg))
	copy(buf, jpeg)
	m.seq++
	m.frame = buf
	m.lastFrameAt = time.Now()
	m.lastPushAt = m.lastFrameAt
	m.lastActivity = m.lastFrameAt
	_ = h.markStreamLocked(m)

	fr := Frame{JPEG: buf, Seq: m.seq, At: m.lastFrameAt}
	for _, ch := range m.watchers {
		select {
		case ch <- fr:
		default:
			// Drop stale frame in channel, then send latest.
			select {
			case <-ch:
			default:
			}
			select {
			case ch <- fr:
			default:
			}
		}
	}
	return nil
}

// Subscribe attaches a watcher. First watcher marks the stream wanted and may send start.
func (h *Hub) Subscribe(empID string) (watcherID uint64, frames <-chan Frame, err error) {
	if !h.cfg.Enabled {
		return 0, nil, ErrDisabled
	}

	h.mu.Lock()
	defer h.mu.Unlock()

	m := h.getOrCreateLocked(empID)
	if len(m.watchers) >= h.cfg.MaxWatchersPerEmployee {
		return 0, nil, ErrTooManyWatchers
	}

	wasWanted := m.wanted
	if !wasWanted {
		if err := h.markStreamLocked(m); err != nil {
			return 0, nil, err
		}
		m.wanted = true
		if m.clientConnected {
			h.sendCtrlLocked(m, "start")
		}
	}

	m.nextWatcherID++
	id := m.nextWatcherID
	ch := make(chan Frame, 1)
	m.watchers[id] = ch
	m.lastActivity = time.Now()

	// Seed with latest frame if present.
	if len(m.frame) > 0 {
		fr := Frame{JPEG: m.frame, Seq: m.seq, At: m.lastFrameAt}
		select {
		case ch <- fr:
		default:
		}
	}

	return id, ch, nil
}

// Unsubscribe detaches a watcher. Last watcher sends stop and clears the mailbox frame.
func (h *Hub) Unsubscribe(empID string, watcherID uint64) {
	h.mu.Lock()
	defer h.mu.Unlock()

	m, ok := h.boxes[empID]
	if !ok {
		return
	}
	if ch, ok := m.watchers[watcherID]; ok {
		delete(m.watchers, watcherID)
		close(ch)
	}
	m.lastActivity = time.Now()

	if len(m.watchers) == 0 && m.wanted {
		m.wanted = false
		h.sendCtrlLocked(m, "stop")
		h.unmarkStreamLocked(m)
		m.frame = nil
	}
}

// Snapshot returns hub state for one employee (zero value if unknown).
func (h *Hub) Snapshot(empID string) EmployeeSnapshot {
	h.mu.RLock()
	defer h.mu.RUnlock()
	m, ok := h.boxes[empID]
	if !ok {
		return EmployeeSnapshot{}
	}
	return EmployeeSnapshot{
		Wanted:          m.wanted,
		Streaming:       m.wanted && len(m.frame) > 0,
		StreamAvailable: m.cap.StreamAvailable,
		WatcherCount:    len(m.watchers),
		ClientConnected: m.clientConnected,
		Seq:             m.seq,
		LastFrameAt:     m.lastFrameAt,
		Monitors:        append([]MonitorInfo(nil), m.cap.Monitors...),
		SelectedMonitor: m.cap.SelectedMonitor,
	}
}

// CapabilityOf returns the last advertised capability (ok=false if never set).
func (h *Hub) CapabilityOf(empID string) (Capability, bool) {
	h.mu.RLock()
	defer h.mu.RUnlock()
	m, ok := h.boxes[empID]
	if !ok {
		return Capability{}, false
	}
	return m.cap, m.clientConnected || m.cap.Platform != ""
}

// IsWanted reports whether any admin is watching.
func (h *Hub) IsWanted(empID string) bool {
	h.mu.RLock()
	defer h.mu.RUnlock()
	m, ok := h.boxes[empID]
	return ok && m.wanted
}

// InjectTestFrame pushes a synthetic JPEG when LIVE_STREAM_TEST_FRAME is on (dev only).
func (h *Hub) InjectTestFrame(empID string, jpeg []byte) error {
	if !h.cfg.TestFrame {
		return ErrDisabled
	}
	h.mu.Lock()
	m := h.getOrCreateLocked(empID)
	if !m.wanted {
		h.mu.Unlock()
		return ErrNotWanted
	}
	h.mu.Unlock()
	return h.PushFrame(empID, jpeg)
}

// IssueWatchTicket mints a one-time ticket so the browser can open the watch
// WebSocket without relying on cross-port cookies (httpOnly JWT stays on REST).
func (h *Hub) IssueWatchTicket(userID, employeeID string) (ticket string, expiresInSec int, err error) {
	if !h.cfg.Enabled {
		return "", 0, ErrDisabled
	}
	var raw [16]byte
	if _, err := rand.Read(raw[:]); err != nil {
		return "", 0, err
	}
	ticket = hex.EncodeToString(raw[:])

	h.mu.Lock()
	defer h.mu.Unlock()
	h.tickets[ticket] = watchTicket{
		EmployeeID: employeeID,
		UserID:     userID,
		ExpiresAt:  time.Now().Add(watchTicketTTL),
	}
	return ticket, int(watchTicketTTL.Seconds()), nil
}

// ConsumeWatchTicket validates and burns a ticket. employeeID must match.
func (h *Hub) ConsumeWatchTicket(ticket, employeeID string) (userID string, err error) {
	h.mu.Lock()
	defer h.mu.Unlock()

	t, ok := h.tickets[ticket]
	if !ok || time.Now().After(t.ExpiresAt) {
		delete(h.tickets, ticket)
		return "", ErrInvalidTicket
	}
	if t.EmployeeID != employeeID {
		return "", ErrInvalidTicket
	}
	delete(h.tickets, ticket)
	return t.UserID, nil
}
