package handlers

import (
	"context"
	"encoding/json"
	"log"
	"net/http"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dlpws"
	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/gorilla/websocket"
	"github.com/labstack/echo/v4"
)

const (
	dlpWsWriteWait  = 10 * time.Second
	dlpWsPongWait   = 60 * time.Second
	dlpWsPingPeriod = 30 * time.Second
	dlpWsMaxLife    = 4 * time.Hour
	dlpWsSendBuf    = 8
)

// ConnectRulesWS — DeviceAuth GET /api/v1/dlp/ws (owned by client --dlp agent).
func (h *DlpHandler) ConnectRulesWS(c echo.Context) error {
	if h.hub == nil {
		return c.JSON(http.StatusServiceUnavailable, dto.APIError{
			Code: http.StatusServiceUnavailable, Message: "DLP WebSocket unavailable",
		})
	}

	empID, ok := c.Get("employee_id").(string)
	if !ok || empID == "" {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code: http.StatusUnauthorized, Message: "Unauthorized employee context",
		})
	}

	conn, err := h.upgrader.Upgrade(c.Response(), c.Request(), nil)
	if err != nil {
		log.Printf("[dlp-ws] upgrade failed employee=%s: %v", empID, err)
		return nil
	}

	client := &dlpws.Conn{
		EmployeeID: empID,
		Send:       make(chan []byte, dlpWsSendBuf),
	}
	h.hub.Register(empID, client)
	defer func() {
		h.hub.Unregister(client)
		_ = conn.Close()
	}()

	// Initial snapshot so the agent does not wait for the next HTTP poll.
	h.pushRulesToEmployee(c.Request().Context(), empID)

	done := make(chan struct{})
	go h.dlpWsWritePump(conn, client, done)
	h.dlpWsReadPump(conn, empID, done)
	return nil
}

func (h *DlpHandler) dlpWsReadPump(conn *websocket.Conn, empID string, done chan struct{}) {
	defer close(done)
	_ = conn.SetReadDeadline(time.Now().Add(dlpWsPongWait))
	conn.SetPongHandler(func(string) error {
		_ = conn.SetReadDeadline(time.Now().Add(dlpWsPongWait))
		return nil
	})
	deadline := time.Now().Add(dlpWsMaxLife)
	for {
		if time.Now().After(deadline) {
			log.Printf("[dlp-ws] max lifetime reached employee=%s", empID)
			return
		}
		_, msg, err := conn.ReadMessage()
		if err != nil {
			return
		}
		_ = conn.SetReadDeadline(time.Now().Add(dlpWsPongWait))
		var envelope struct {
			Type string `json:"type"`
		}
		if err := json.Unmarshal(msg, &envelope); err == nil && strings.EqualFold(envelope.Type, "ping") {
			// Write pump already sends protocol pings; app-level ping is acknowledged implicitly.
			continue
		}
	}
}

func (h *DlpHandler) dlpWsWritePump(conn *websocket.Conn, client *dlpws.Conn, done <-chan struct{}) {
	ticker := time.NewTicker(dlpWsPingPeriod)
	defer ticker.Stop()
	for {
		select {
		case <-done:
			return
		case payload, ok := <-client.Send:
			if !ok {
				return
			}
			_ = conn.SetWriteDeadline(time.Now().Add(dlpWsWriteWait))
			if err := conn.WriteMessage(websocket.TextMessage, payload); err != nil {
				return
			}
		case <-ticker.C:
			_ = conn.SetWriteDeadline(time.Now().Add(dlpWsWriteWait))
			if err := conn.WriteMessage(websocket.PingMessage, nil); err != nil {
				return
			}
		}
	}
}

type dlpRulesMessage struct {
	Type  string           `json:"type"`
	Rules []models.DlpRule `json:"rules"`
}

func (h *DlpHandler) pushRulesToEmployee(ctx context.Context, empID string) {
	if h.hub == nil || empID == "" {
		return
	}
	rules, err := h.svc.ListActiveRules(ctx, empID)
	if err != nil {
		log.Printf("[dlp-ws] ListActiveRules employee=%s: %v", empID, err)
		return
	}
	if rules == nil {
		rules = []models.DlpRule{}
	}
	payload, err := json.Marshal(dlpRulesMessage{Type: "dlp_rules", Rules: rules})
	if err != nil {
		return
	}
	h.hub.SendTo(empID, payload)
}

// notifyRulesChanged pushes a per-employee active-rules snapshot to every connected DLP agent.
func (h *DlpHandler) notifyRulesChanged(ctx context.Context) {
	if h.hub == nil {
		return
	}
	for _, empID := range h.hub.EmployeeIDs() {
		h.pushRulesToEmployee(ctx, empID)
	}
}
