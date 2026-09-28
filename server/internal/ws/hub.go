package ws

import (
	"sync"
	"sync/atomic"
	"time"
)

// Config holds caps for the in-memory presence / control-channel hub.
type Config struct {
	Enabled        bool
	MaxConnections int
}

// ConnMeta is the lightweight per-employee presence record (no DB).
type ConnMeta struct {
	Gen         uint64
	EmployeeID  string
	ConnectedAt time.Time
	Platform    string
	Version     string
}

// Hub tracks long-lived control WebSocket connections (one per employee).
// Independent of the live-stream frame hub — no watchers, no JPEG state.
type Hub struct {
	cfg     Config
	mu      sync.RWMutex
	conns   map[string]*ConnMeta
	nextGen atomic.Uint64
}

// NewHub constructs a presence hub. MaxConnections <= 0 defaults to 10_000.
func NewHub(cfg Config) *Hub {
	if cfg.MaxConnections <= 0 {
		cfg.MaxConnections = 10_000
	}
	return &Hub{
		cfg:   cfg,
		conns: make(map[string]*ConnMeta),
	}
}

// Config returns a copy of the hub config.
func (h *Hub) Config() Config { return h.cfg }

// Register inserts or replaces the connection for empID and returns a generation
// token. Unregister must pass the same token so a stale reconnect cannot wipe a
// newer socket. Returns ok=false when disabled or at capacity (and empID is new).
func (h *Hub) Register(empID string) (gen uint64, ok bool) {
	if !h.cfg.Enabled || empID == "" {
		return 0, false
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	if _, exists := h.conns[empID]; !exists && len(h.conns) >= h.cfg.MaxConnections {
		return 0, false
	}
	gen = h.nextGen.Add(1)
	h.conns[empID] = &ConnMeta{
		Gen:         gen,
		EmployeeID:  empID,
		ConnectedAt: time.Now().UTC(),
	}
	return gen, true
}

// SetHello stores optional hello metadata from the client (generation-scoped).
func (h *Hub) SetHello(empID string, gen uint64, platform, version string) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if meta, ok := h.conns[empID]; ok && meta.Gen == gen {
		meta.Platform = platform
		meta.Version = version
	}
}

// Unregister removes the connection for empID only when gen still matches.
func (h *Hub) Unregister(empID string, gen uint64) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if meta, ok := h.conns[empID]; ok && meta.Gen == gen {
		delete(h.conns, empID)
	}
}

// IsConnected reports whether empID currently holds a control socket.
func (h *Hub) IsConnected(empID string) bool {
	h.mu.RLock()
	defer h.mu.RUnlock()
	_, ok := h.conns[empID]
	return ok
}

// Count returns the number of live control connections.
func (h *Hub) Count() int {
	h.mu.RLock()
	defer h.mu.RUnlock()
	return len(h.conns)
}

// Close clears the registry (server shutdown).
func (h *Hub) Close() {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.conns = make(map[string]*ConnMeta)
}
