package handlers

import (
	"encoding/json"
	"log"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/stream"
	"github.com/gorilla/websocket"
	"github.com/labstack/echo/v4"
)

const (
	// Heartbeat is written every collect cycle (~30s) but only reaches the server on
	// SyncService cadence (~60s). A 60s online window therefore flaps Offline between
	// syncs. Use 3 minutes so a healthy syncing client stays Online.
	liveStreamOnlineWindow = 3 * time.Minute
	wsWriteWait            = 10 * time.Second
	wsPongWait             = 60 * time.Second
	wsPingPeriod           = 30 * time.Second
)

// StreamHandler serves live-stream WebSocket + REST endpoints.
type StreamHandler struct {
	hub              *stream.Hub
	employeeRepo     *repository.EmployeeRepo
	termsConsentRepo *repository.TermsConsentRepo
	taRepo           *repository.TimeAttendanceRepo
	allowedOrigins   map[string]bool
	upgrader         websocket.Upgrader
}

// NewStreamHandler constructs the handler. allowedOrigins should match CORS_ALLOWED_ORIGINS.
func NewStreamHandler(
	hub *stream.Hub,
	employeeRepo *repository.EmployeeRepo,
	termsConsentRepo *repository.TermsConsentRepo,
	taRepo *repository.TimeAttendanceRepo,
	allowedOrigins []string,
) *StreamHandler {
	originSet := make(map[string]bool, len(allowedOrigins))
	for _, o := range allowedOrigins {
		originSet[strings.TrimSpace(o)] = true
	}
	h := &StreamHandler{
		hub:              hub,
		employeeRepo:     employeeRepo,
		termsConsentRepo: termsConsentRepo,
		taRepo:           taRepo,
		allowedOrigins:   originSet,
	}
	h.upgrader = websocket.Upgrader{
		ReadBufferSize:  1024,
		WriteBufferSize: 64 * 1024,
		CheckOrigin: func(r *http.Request) bool {
			origin := r.Header.Get("Origin")
			if origin == "" {
				// Non-browser clients (desktop push socket) omit Origin.
				return true
			}
			if len(h.allowedOrigins) == 0 {
				return true
			}
			if h.allowedOrigins[origin] {
				return true
			}
			log.Printf("[live-stream] CheckOrigin rejected origin=%q allowed=%v", origin, allowedOrigins)
			return false
		},
	}
	return h
}

// LiveStreamEmployee is the rail list DTO.
type LiveStreamEmployee struct {
	EmployeeID      string `json:"employeeId"`
	Name            string `json:"name"`
	Department      string `json:"department"`
	Online          bool   `json:"online"`
	Streaming       bool   `json:"streaming"`
	StreamAvailable bool   `json:"streamAvailable"`
	ClientConnected bool   `json:"clientConnected"`
	ConsentMissing  bool   `json:"consentMissing"`
}

// ListEmployees handles GET /api/v1/live-stream/employees (JWTAuth).
func (h *StreamHandler) ListEmployees(c echo.Context) error {
	if !h.hub.Config().Enabled {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: "Live stream is disabled",
		})
	}

	employees, err := h.employeeRepo.ListAll(c.Request().Context())
	if err != nil {
		log.Printf("[live-stream] ListEmployees employees: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to list employees", Detail: err.Error(),
		})
	}

	heartbeats, err := h.taRepo.ListLastHeartbeats(c.Request().Context())
	if err != nil {
		log.Printf("[live-stream] ListEmployees heartbeats: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to load heartbeats", Detail: err.Error(),
		})
	}

	accepted, err := h.termsConsentRepo.ListAcceptedEmployeeIDs(c.Request().Context(), stream.FeatureID)
	if err != nil {
		log.Printf("[live-stream] ListEmployees consent: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to load consent", Detail: err.Error(),
		})
	}

	now := time.Now().UTC()
	out := make([]LiveStreamEmployee, 0, len(employees))
	for _, e := range employees {
		snap := h.hub.Snapshot(e.EmployeeID)
		hb, hasHB := heartbeats[e.EmployeeID]
		online := hasHB && now.Sub(hb.UTC()) <= liveStreamOnlineWindow
		hasConsent := accepted[e.EmployeeID]
		out = append(out, LiveStreamEmployee{
			EmployeeID:      e.EmployeeID,
			Name:            e.Name,
			Department:      e.Department,
			Online:          online,
			Streaming:       snap.Streaming,
			StreamAvailable: snap.StreamAvailable,
			ClientConnected: snap.ClientConnected,
			ConsentMissing:  !hasConsent,
		})
	}

	return c.JSON(http.StatusOK, map[string]interface{}{
		"data":  out,
		"total": len(out),
	})
}

// IssueWatchTicket handles GET /api/v1/live-stream/watch-ticket?employeeId= (JWTAuth).
// Browsers cannot reliably send httpOnly cookies on cross-port WebSockets, so the
// page fetches a short-lived ticket over REST (cookies work) then opens WS with it.
func (h *StreamHandler) IssueWatchTicket(c echo.Context) error {
	if !h.hub.Config().Enabled {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: "Live stream is disabled",
		})
	}

	empID := strings.TrimSpace(c.QueryParam("employeeId"))
	if empID == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code: http.StatusBadRequest, Message: "employeeId is required",
		})
	}

	userID, _ := c.Get("user_id").(string)
	if userID == "" {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code: http.StatusUnauthorized, Message: "Authentication required",
		})
	}

	if existing, err := h.employeeRepo.GetByEmployeeID(c.Request().Context(), empID); err != nil || existing == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{
			Code: http.StatusNotFound, Message: "Employee not found",
		})
	}

	accepted, err := h.termsConsentRepo.HasAccepted(c.Request().Context(), empID, stream.FeatureID, "")
	if err != nil {
		log.Printf("[live-stream] watch-ticket consent: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Consent check failed",
		})
	}
	if !accepted {
		return c.JSON(http.StatusForbidden, dto.APIError{
			Code: http.StatusForbidden, Message: "consent_required",
			Detail: "Employee has not accepted live_view terms",
		})
	}

	ticket, expiresIn, err := h.hub.IssueWatchTicket(userID, empID)
	if err != nil {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: err.Error(),
		})
	}

	return c.JSON(http.StatusOK, map[string]interface{}{
		"ticket":    ticket,
		"expiresIn": expiresIn,
	})
}

// Push handles GET /api/v1/live-stream/push (DeviceAuth) — client control + JPEG frames.
func (h *StreamHandler) Push(c echo.Context) error {
	if !h.hub.Config().Enabled {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: "Live stream is disabled",
		})
	}

	empID, ok := c.Get("employee_id").(string)
	if !ok || empID == "" {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code: http.StatusUnauthorized, Message: "Unauthorized employee context",
		})
	}

	accepted, err := h.termsConsentRepo.HasAccepted(c.Request().Context(), empID, stream.FeatureID, "")
	if err != nil {
		log.Printf("[live-stream] Push consent check: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Consent check failed",
		})
	}
	if !accepted {
		return c.JSON(http.StatusForbidden, dto.APIError{
			Code: http.StatusForbidden, Message: "consent_required",
			Detail: "Employee must accept live_view terms before streaming",
		})
	}

	conn, err := h.upgrader.Upgrade(c.Response(), c.Request(), nil)
	if err != nil {
		log.Printf("[live-stream] Push upgrade: %v", err)
		return nil
	}
	defer conn.Close()

	ctrl, ok := h.hub.RegisterClient(empID)
	if !ok {
		_ = writeJSON(conn, map[string]string{"type": "error", "code": "disabled"})
		return nil
	}
	defer h.hub.UnregisterClient(empID)

	log.Printf("[live-stream] push connected employee=%s", empID)

	var writeMu sync.Mutex
	done := make(chan struct{})
	var once sync.Once
	closeDone := func() { once.Do(func() { close(done) }) }

	// Control → client
	go func() {
		defer closeDone()
		ticker := time.NewTicker(wsPingPeriod)
		defer ticker.Stop()
		for {
			select {
			case ev, ok := <-ctrl:
				if !ok {
					return
				}
				writeMu.Lock()
				_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
				err := conn.WriteJSON(map[string]string{"type": ev.Type})
				writeMu.Unlock()
				if err != nil {
					return
				}
			case <-ticker.C:
				writeMu.Lock()
				_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
				err := conn.WriteMessage(websocket.PingMessage, nil)
				writeMu.Unlock()
				if err != nil {
					return
				}
			case <-done:
				return
			}
		}
	}()

	_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))
	conn.SetPongHandler(func(string) error {
		_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))
		return nil
	})

	for {
		msgType, data, err := conn.ReadMessage()
		if err != nil {
			closeDone()
			log.Printf("[live-stream] push disconnected employee=%s: %v", empID, err)
			return nil
		}
		_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))

		switch msgType {
		case websocket.BinaryMessage:
			if err := h.hub.PushFrame(empID, data); err != nil && err != stream.ErrNotWanted {
				if err == stream.ErrFrameTooLarge {
					log.Printf("[live-stream] drop oversized frame employee=%s size=%d", empID, len(data))
					continue
				}
				log.Printf("[live-stream] PushFrame employee=%s: %v", empID, err)
			}
		case websocket.TextMessage:
			h.handlePushText(empID, data)
		}
	}
}

func (h *StreamHandler) handlePushText(empID string, data []byte) {
	var msg struct {
		Type            string `json:"type"`
		Platform        string `json:"platform"`
		StreamAvailable bool   `json:"streamAvailable"`
		Version         string `json:"version"`
	}
	if err := json.Unmarshal(data, &msg); err != nil {
		return
	}
	if msg.Type == "hello" {
		h.hub.SetCapability(empID, stream.Capability{
			Platform:        msg.Platform,
			StreamAvailable: msg.StreamAvailable,
			Version:         msg.Version,
		})
		log.Printf("[live-stream] hello employee=%s platform=%s available=%v", empID, msg.Platform, msg.StreamAvailable)
	}
}

// Watch handles GET /api/v1/live-stream/watch?employeeId=&ticket= (ticket auth).
// Auth is via a one-time ticket from IssueWatchTicket — not JWT middleware — because
// browsers often omit httpOnly cookies on cross-port WebSocket handshakes.
func (h *StreamHandler) Watch(c echo.Context) error {
	if !h.hub.Config().Enabled {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: "Live stream is disabled",
		})
	}

	empID := strings.TrimSpace(c.QueryParam("employeeId"))
	ticket := strings.TrimSpace(c.QueryParam("ticket"))
	if empID == "" || ticket == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code: http.StatusBadRequest, Message: "employeeId and ticket are required",
		})
	}

	if _, err := h.hub.ConsumeWatchTicket(ticket, empID); err != nil {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code: http.StatusUnauthorized, Message: "invalid_or_expired_ticket",
		})
	}

	if existing, err := h.employeeRepo.GetByEmployeeID(c.Request().Context(), empID); err != nil || existing == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{
			Code: http.StatusNotFound, Message: "Employee not found",
		})
	}

	// Consent already checked when minting the ticket; re-check so a revoke mid-flight blocks.
	accepted, err := h.termsConsentRepo.HasAccepted(c.Request().Context(), empID, stream.FeatureID, "")
	if err != nil {
		log.Printf("[live-stream] Watch consent check: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Consent check failed",
		})
	}
	if !accepted {
		return c.JSON(http.StatusForbidden, dto.APIError{
			Code: http.StatusForbidden, Message: "consent_required",
			Detail: "Employee has not accepted live_view terms",
		})
	}

	conn, err := h.upgrader.Upgrade(c.Response(), c.Request(), nil)
	if err != nil {
		log.Printf("[live-stream] Watch upgrade: %v", err)
		return nil
	}
	defer conn.Close()

	watcherID, frames, err := h.hub.Subscribe(empID)
	if err != nil {
		code := http.StatusConflict
		msg := err.Error()
		if err == stream.ErrTooManyStreams || err == stream.ErrTooManyWatchers {
			code = http.StatusTooManyRequests
		}
		// Connection already upgraded — send error JSON then close.
		_ = writeJSON(conn, map[string]interface{}{"type": "error", "code": msg, "httpStatus": code})
		return nil
	}
	defer h.hub.Unsubscribe(empID, watcherID)

	log.Printf("[live-stream] watch connected employee=%s watcher=%d", empID, watcherID)

	snap := h.hub.Snapshot(empID)
	_ = writeJSON(conn, map[string]interface{}{
		"type":            "status",
		"streaming":       snap.Streaming,
		"streamAvailable": snap.StreamAvailable,
		"clientConnected": snap.ClientConnected,
		"consentMissing":  false,
	})

	// Dev-only synthetic frames when no client is pushing.
	var testStop chan struct{}
	if h.hub.Config().TestFrame {
		testStop = make(chan struct{})
		go h.testFrameLoop(empID, testStop)
		defer close(testStop)
	}

	var writeMu sync.Mutex
	done := make(chan struct{})
	var once sync.Once
	closeDone := func() { once.Do(func() { close(done) }) }

	go func() {
		defer closeDone()
		ticker := time.NewTicker(wsPingPeriod)
		defer ticker.Stop()
		statusTicker := time.NewTicker(2 * time.Second)
		defer statusTicker.Stop()
		for {
			select {
			case fr, ok := <-frames:
				if !ok {
					return
				}
				writeMu.Lock()
				_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
				err := conn.WriteMessage(websocket.BinaryMessage, fr.JPEG)
				writeMu.Unlock()
				if err != nil {
					return
				}
			case <-statusTicker.C:
				s := h.hub.Snapshot(empID)
				writeMu.Lock()
				_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
				err := conn.WriteJSON(map[string]interface{}{
					"type":            "status",
					"streaming":       s.Streaming,
					"streamAvailable": s.StreamAvailable,
					"clientConnected": s.ClientConnected,
					"consentMissing":  false,
				})
				writeMu.Unlock()
				if err != nil {
					return
				}
			case <-ticker.C:
				writeMu.Lock()
				_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
				err := conn.WriteMessage(websocket.PingMessage, nil)
				writeMu.Unlock()
				if err != nil {
					return
				}
			case <-done:
				return
			}
		}
	}()

	_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))
	conn.SetPongHandler(func(string) error {
		_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))
		return nil
	})

	for {
		if _, _, err := conn.ReadMessage(); err != nil {
			closeDone()
			log.Printf("[live-stream] watch disconnected employee=%s watcher=%d", empID, watcherID)
			return nil
		}
		_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))
	}
}

func (h *StreamHandler) testFrameLoop(empID string, stop <-chan struct{}) {
	// Minimal valid 1x1 JPEG
	jpeg := []byte{
		0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46, 0x00, 0x01,
		0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xff, 0xdb, 0x00, 0x43,
		0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08, 0x07, 0x07, 0x07, 0x09,
		0x09, 0x08, 0x0a, 0x0c, 0x14, 0x0d, 0x0c, 0x0b, 0x0b, 0x0c, 0x19, 0x12,
		0x13, 0x0f, 0x14, 0x1d, 0x1a, 0x1f, 0x1e, 0x1d, 0x1a, 0x1c, 0x1c, 0x20,
		0x24, 0x2e, 0x27, 0x20, 0x22, 0x2c, 0x23, 0x1c, 0x1c, 0x28, 0x37, 0x29,
		0x2c, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1f, 0x27, 0x39, 0x3d, 0x38, 0x32,
		0x3c, 0x2e, 0x33, 0x34, 0x32, 0xff, 0xc0, 0x00, 0x0b, 0x08, 0x00, 0x01,
		0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xff, 0xc4, 0x00, 0x1f, 0x00, 0x00,
		0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
		0x09, 0x0a, 0x0b, 0xff, 0xc4, 0x00, 0xb5, 0x10, 0x00, 0x02, 0x01, 0x03,
		0x03, 0x02, 0x04, 0x03, 0x05, 0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7d,
		0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06,
		0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
		0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72,
		0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
		0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45,
		0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
		0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75,
		0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
		0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3,
		0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
		0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9,
		0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
		0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4,
		0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa, 0xff, 0xda, 0x00, 0x08, 0x01, 0x01,
		0x00, 0x00, 0x3f, 0x00, 0x7b, 0xdf, 0xff, 0xd9,
	}
	ticker := time.NewTicker(500 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-stop:
			return
		case <-ticker.C:
			snap := h.hub.Snapshot(empID)
			if snap.ClientConnected {
				continue // real client is pushing
			}
			_ = h.hub.InjectTestFrame(empID, jpeg)
		}
	}
}

func writeJSON(conn *websocket.Conn, v interface{}) error {
	_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
	return conn.WriteJSON(v)
}
