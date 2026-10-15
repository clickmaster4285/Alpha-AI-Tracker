package dlpws

import (
	"sync"
)

// Conn is one DeviceAuth DLP control WebSocket (owned by the --dlp agent).
type Conn struct {
	EmployeeID string
	Send       chan []byte
}

// Hub fans rule snapshots to connected DLP agents (keyed by employee_id).
// Independent of presence / live-stream hubs.
type Hub struct {
	mu    sync.RWMutex
	byEmp map[string]map[*Conn]struct{}
}

// NewHub constructs an empty DLP rules hub.
func NewHub() *Hub {
	return &Hub{byEmp: make(map[string]map[*Conn]struct{})}
}

// Register attaches a connection for empID.
func (h *Hub) Register(empID string, c *Conn) {
	if h == nil || empID == "" || c == nil {
		return
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	set, ok := h.byEmp[empID]
	if !ok {
		set = make(map[*Conn]struct{})
		h.byEmp[empID] = set
	}
	set[c] = struct{}{}
}

// Unregister removes a connection.
func (h *Hub) Unregister(c *Conn) {
	if h == nil || c == nil || c.EmployeeID == "" {
		return
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	set, ok := h.byEmp[c.EmployeeID]
	if !ok {
		return
	}
	delete(set, c)
	if len(set) == 0 {
		delete(h.byEmp, c.EmployeeID)
	}
}

// EmployeeIDs returns employees that currently hold at least one DLP WS.
func (h *Hub) EmployeeIDs() []string {
	if h == nil {
		return nil
	}
	h.mu.RLock()
	defer h.mu.RUnlock()
	out := make([]string, 0, len(h.byEmp))
	for empID := range h.byEmp {
		out = append(out, empID)
	}
	return out
}

// SendTo pushes a payload to every DLP socket for empID (non-blocking; drops if full).
func (h *Hub) SendTo(empID string, payload []byte) {
	if h == nil || empID == "" || len(payload) == 0 {
		return
	}
	h.mu.RLock()
	set := h.byEmp[empID]
	conns := make([]*Conn, 0, len(set))
	for c := range set {
		conns = append(conns, c)
	}
	h.mu.RUnlock()

	for _, c := range conns {
		select {
		case c.Send <- payload:
		default:
			// Slow client — drop this snapshot; next CRUD or reconnect refreshes.
		}
	}
}
