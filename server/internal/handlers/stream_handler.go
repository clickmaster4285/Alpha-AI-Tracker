package handlers

import (
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/stream"
	"github.com/alpha-ai-tracker/server/internal/ws"
	"github.com/gorilla/websocket"
	"github.com/labstack/echo/v4"
	"github.com/pion/webrtc/v4"
)

const (
	// Heartbeat fallback only — primary online is the presence WS (/api/v1/ws).
	liveStreamOnlineWindow = 3 * time.Minute
	wsWriteWait            = 10 * time.Second
	wsPongWait             = 90 * time.Second
	wsPingPeriod           = 30 * time.Second
)

// StreamHandler serves live-stream WebRTC signaling + REST endpoints.
type StreamHandler struct {
	hub              *stream.Hub
	presence         *ws.Hub // optional — instant online via GET /api/v1/ws
	employeeRepo     *repository.EmployeeRepo
	termsConsentRepo *repository.TermsConsentRepo
	taRepo           *repository.TimeAttendanceRepo
	allowedOrigins   map[string]bool
	upgrader         websocket.Upgrader
}

// NewStreamHandler constructs the handler. presence may be nil (falls back to heartbeat online).
func NewStreamHandler(
	hub *stream.Hub,
	presence *ws.Hub,
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
		presence:         presence,
		employeeRepo:     employeeRepo,
		termsConsentRepo: termsConsentRepo,
		taRepo:           taRepo,
		allowedOrigins:   originSet,
	}
	h.upgrader = websocket.Upgrader{
		ReadBufferSize:  4096,
		WriteBufferSize: 4096,
		CheckOrigin: func(r *http.Request) bool {
			origin := r.Header.Get("Origin")
			if origin == "" {
				return true
			}
			if len(h.allowedOrigins) == 0 {
				return true
			}
			if h.allowedOrigins[origin] {
				return true
			}
			log.Printf("[live-stream] CheckOrigin rejected origin=%q", origin)
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
	WsConnected     bool   `json:"wsConnected"` // presence socket GET /api/v1/ws
	Streaming       bool   `json:"streaming"`
	StreamAvailable bool   `json:"streamAvailable"`
	ClientConnected bool   `json:"clientConnected"` // live-stream push signaling socket
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
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to list employees", Detail: err.Error(),
		})
	}
	heartbeats, err := h.taRepo.ListLastHeartbeats(c.Request().Context())
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to load heartbeats", Detail: err.Error(),
		})
	}
	accepted, err := h.termsConsentRepo.ListAcceptedEmployeeIDs(c.Request().Context(), stream.FeatureID)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Failed to load consent", Detail: err.Error(),
		})
	}

	now := time.Now().UTC()
	presenceOn := h.presence != nil && h.presence.Config().Enabled
	out := make([]LiveStreamEmployee, 0, len(employees))
	for _, e := range employees {
		snap := h.hub.Snapshot(e.EmployeeID)
		wsConnected := presenceOn && h.presence.IsConnected(e.EmployeeID)
		hb, hasHB := heartbeats[e.EmployeeID]
		hbOnline := hasHB && now.Sub(hb.UTC()) <= liveStreamOnlineWindow
		online := wsConnected
		if !presenceOn {
			online = hbOnline
		}
		out = append(out, LiveStreamEmployee{
			EmployeeID:      e.EmployeeID,
			Name:            e.Name,
			Department:      e.Department,
			Online:          online,
			WsConnected:     wsConnected,
			Streaming:       snap.Streaming,
			StreamAvailable: snap.StreamAvailable,
			ClientConnected: snap.ClientConnected,
			ConsentMissing:  !accepted[e.EmployeeID],
		})
	}
	return c.JSON(http.StatusOK, map[string]interface{}{"data": out, "total": len(out)})
}

// IssueWatchTicket handles GET /api/v1/live-stream/watch-ticket?employeeId=.
// Response includes ICE servers for the browser RTCPeerConnection.
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
		"ticket":     ticket,
		"expiresIn":  expiresIn,
		"iceServers": h.hub.ICEServers(),
	})
}

type signalMsg struct {
	Type             string               `json:"type"`
	SDP              string               `json:"sdp,omitempty"`
	Candidate        string               `json:"candidate,omitempty"`
	SDPMLineIndex    *uint16              `json:"sdpMLineIndex,omitempty"`
	SDPMid           *string              `json:"sdpMid,omitempty"`
	Index            int                  `json:"index,omitempty"`
	Platform         string               `json:"platform,omitempty"`
	StreamAvailable  bool                 `json:"streamAvailable,omitempty"`
	Version          string               `json:"version,omitempty"`
	SelectedMonitor  int                  `json:"selectedMonitor,omitempty"`
	Monitors         []stream.MonitorInfo `json:"monitors,omitempty"`
}

// Push handles GET /api/v1/live-stream/push (DeviceAuth) — WebRTC publisher signaling.
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
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Consent check failed",
		})
	}
	if !accepted {
		log.Printf("[live-stream] push refused employee=%s reason=consent_required", empID)
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

	ctrl, gen, ok := h.hub.RegisterClient(empID)
	if !ok {
		_ = writeJSON(conn, map[string]string{"type": "error", "code": "disabled"})
		return nil
	}
	defer h.hub.UnregisterClient(empID, gen)

	log.Printf("[live-stream] push connected employee=%s (webrtc)", empID)

	var writeMu sync.Mutex
	write := func(v interface{}) error {
		writeMu.Lock()
		defer writeMu.Unlock()
		_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
		return conn.WriteJSON(v)
	}

	// Advertise ICE servers to the publisher.
	_ = write(map[string]interface{}{
		"type":       "ice_servers",
		"iceServers": h.hub.ICEServers(),
	})

	done := make(chan struct{})
	var once sync.Once
	closeDone := func() { once.Do(func() { close(done) }) }

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
				payload := map[string]interface{}{"type": ev.Type}
				if ev.Type == "select_monitor" {
					payload["index"] = ev.MonitorIndex
				}
				if err := write(payload); err != nil {
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
		_, data, err := conn.ReadMessage()
		if err != nil {
			closeDone()
			log.Printf("[live-stream] push disconnected employee=%s: %v", empID, err)
			return nil
		}
		_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))

		var msg signalMsg
		if json.Unmarshal(data, &msg) != nil {
			continue
		}
		switch msg.Type {
		case "hello":
			h.hub.SetCapability(empID, stream.Capability{
				Platform:        msg.Platform,
				StreamAvailable: msg.StreamAvailable,
				Version:         msg.Version,
				Monitors:        msg.Monitors,
				SelectedMonitor: msg.SelectedMonitor,
			})
			log.Printf("[live-stream] hello employee=%s platform=%s available=%v monitors=%d",
				empID, msg.Platform, msg.StreamAvailable, len(msg.Monitors))
		case "offer":
			answer, err := h.hub.SFU().AcceptPublisherOffer(empID, msg.SDP, func(c webrtc.ICECandidateInit) {
				payload := map[string]interface{}{
					"type":      "ice",
					"candidate": c.Candidate,
				}
				if c.SDPMLineIndex != nil {
					payload["sdpMLineIndex"] = *c.SDPMLineIndex
				}
				if c.SDPMid != nil {
					payload["sdpMid"] = *c.SDPMid
				}
				_ = write(payload)
			})
			if err != nil {
				log.Printf("[live-stream] publisher offer employee=%s: %v", empID, err)
				_ = write(map[string]string{"type": "error", "code": "offer_failed"})
				continue
			}
			log.Printf("[live-stream] publisher offer accepted employee=%s", empID)
			_ = write(map[string]string{"type": "answer", "sdp": answer})
		case "ice":
			cand := webrtc.ICECandidateInit{Candidate: msg.Candidate}
			if msg.SDPMLineIndex != nil {
				cand.SDPMLineIndex = msg.SDPMLineIndex
			}
			if msg.SDPMid != nil {
				cand.SDPMid = msg.SDPMid
			}
			if err := h.hub.SFU().AddPublisherICE(empID, cand); err != nil {
				log.Printf("[live-stream] publisher ice employee=%s: %v", empID, err)
			}
		}
	}
}

func statusPayload(snap stream.EmployeeSnapshot) map[string]interface{} {
	return map[string]interface{}{
		"type":            "status",
		"streaming":       snap.Streaming,
		"streamAvailable": snap.StreamAvailable,
		"clientConnected": snap.ClientConnected,
		"consentMissing":  false,
		"monitors":        snap.Monitors,
		"selectedMonitor": snap.SelectedMonitor,
	}
}

// Watch handles GET /api/v1/live-stream/watch?employeeId=&ticket= — WebRTC subscriber signaling.
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
	accepted, err := h.termsConsentRepo.HasAccepted(c.Request().Context(), empID, stream.FeatureID, "")
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code: http.StatusInternalServerError, Message: "Consent check failed",
		})
	}
	if !accepted {
		return c.JSON(http.StatusForbidden, dto.APIError{
			Code: http.StatusForbidden, Message: "consent_required",
		})
	}

	conn, err := h.upgrader.Upgrade(c.Response(), c.Request(), nil)
	if err != nil {
		log.Printf("[live-stream] Watch upgrade: %v", err)
		return nil
	}
	defer conn.Close()

	watcherID, err := h.hub.Subscribe(empID)
	if err != nil {
		_ = writeJSON(conn, map[string]interface{}{"type": "error", "code": err.Error()})
		return nil
	}
	defer h.hub.Unsubscribe(empID, watcherID)

	log.Printf("[live-stream] watch connected employee=%s watcher=%d (webrtc)", empID, watcherID)

	var writeMu sync.Mutex
	write := func(v interface{}) error {
		writeMu.Lock()
		defer writeMu.Unlock()
		_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
		return conn.WriteJSON(v)
	}

	_ = write(map[string]interface{}{
		"type":       "ice_servers",
		"iceServers": h.hub.ICEServers(),
	})
	_ = write(statusPayload(h.hub.Snapshot(empID)))

	done := make(chan struct{})
	var once sync.Once
	closeDone := func() { once.Do(func() { close(done) }) }

	// Late-track renegotiation: registered only when an answer went out without media.
	trackNotify := make(chan struct{}, 1)
	go func() {
		defer closeDone()
		ticker := time.NewTicker(wsPingPeriod)
		defer ticker.Stop()
		statusTicker := time.NewTicker(2 * time.Second)
		defer statusTicker.Stop()
		for {
			select {
			case <-trackNotify:
				if err := write(map[string]string{"type": "track_ready"}); err != nil {
					return
				}
			case <-statusTicker.C:
				if err := write(statusPayload(h.hub.Snapshot(empID))); err != nil {
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
		_, data, err := conn.ReadMessage()
		if err != nil {
			closeDone()
			log.Printf("[live-stream] watch disconnected employee=%s watcher=%d", empID, watcherID)
			return nil
		}
		_ = conn.SetReadDeadline(time.Now().Add(wsPongWait))

		var msg signalMsg
		if json.Unmarshal(data, &msg) != nil {
			continue
		}
		switch msg.Type {
		case "select_monitor":
			h.hub.SelectMonitor(empID, msg.Index)
		case "offer":
			sdp := msg.SDP
			go func() {
				h.hub.SFU().UnwatchTrackReady(empID, watcherID)
				answer, withTrack, err := h.hub.SFU().AcceptSubscriberOffer(empID, watcherID, sdp, func(c webrtc.ICECandidateInit) {
					payload := map[string]interface{}{
						"type":      "ice",
						"candidate": c.Candidate,
					}
					if c.SDPMLineIndex != nil {
						payload["sdpMLineIndex"] = *c.SDPMLineIndex
					}
					if c.SDPMid != nil {
						payload["sdpMid"] = *c.SDPMid
					}
					_ = write(payload)
				})
				if err != nil {
					if errors.Is(err, stream.ErrSuperseded) {
						log.Printf("[live-stream] subscriber offer superseded employee=%s watcher=%d", empID, watcherID)
						return
					}
					log.Printf("[live-stream] subscriber offer employee=%s: %v", empID, err)
					_ = write(map[string]string{"type": "error", "code": "offer_failed"})
					return
				}
				_ = write(map[string]string{"type": "answer", "sdp": answer})
				if withTrack {
					return
				}
				// Answer had no media — wait for publisher track, then one renegotiate.
				ready := h.hub.SFU().WatchTrackReady(empID, watcherID)
				go func() {
					select {
					case <-ready:
						select {
						case trackNotify <- struct{}{}:
						default:
						}
					case <-done:
					}
					h.hub.SFU().UnwatchTrackReady(empID, watcherID)
				}()
			}()
		case "ice":
			cand := webrtc.ICECandidateInit{Candidate: msg.Candidate}
			if msg.SDPMLineIndex != nil {
				cand.SDPMLineIndex = msg.SDPMLineIndex
			}
			if msg.SDPMid != nil {
				cand.SDPMid = msg.SDPMid
			}
			_ = h.hub.SFU().AddSubscriberICE(empID, watcherID, cand)
		}
	}
}

func writeJSON(conn *websocket.Conn, v interface{}) error {
	_ = conn.SetWriteDeadline(time.Now().Add(wsWriteWait))
	return conn.WriteJSON(v)
}
