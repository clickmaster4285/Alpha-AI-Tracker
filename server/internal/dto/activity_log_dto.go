package dto

import "time"

// ────────────────────────────────
// Request DTOs
// ────────────────────────────────

// SyncActivityLogsRequest is the payload for syncing activity logs from the desktop client.
type SyncActivityLogsRequest struct {
	EmployeeID string               `json:"employeeId"`
	Token      string               `json:"token"`
	Logs       []ActivityLogEntry   `json:"logs"`
}

// ActivityLogEntry represents a single log entry from the client.
type ActivityLogEntry struct {
	ID           string  `json:"id"`
	MachineID    string  `json:"machineId"`
	Timestamp    string  `json:"timestamp"`
	ProcessName  string  `json:"processName"`
	WindowTitle  *string `json:"windowTitle,omitempty"`
	ProcessID    int     `json:"processId"`
	CPUPercent   float64 `json:"cpuPercent"`
	MemoryBytes  int64   `json:"memoryBytes"`
	IsForeground bool    `json:"isForeground"`
	UserName     string  `json:"userName"`
	Platform     string  `json:"platform"`
	SessionID    *string `json:"sessionId,omitempty"`
	EmployeeID   string  `json:"employeeId"`
	EmployeeName *string `json:"employeeName,omitempty"`
}

// ────────────────────────────────
// Response DTOs
// ────────────────────────────────

// SyncActivityLogsResponse is returned after successfully syncing logs.
type SyncActivityLogsResponse struct {
	Synced  int      `json:"synced"`
	Message string   `json:"message"`
}

// ActivityLogResponse is the public API response for a single activity log.
type ActivityLogResponse struct {
	ID           string    `json:"id"`
	EmployeeID   string    `json:"employeeId"`
	EmployeeName *string   `json:"employeeName,omitempty"`
	MachineID    string    `json:"machineId"`
	Timestamp    time.Time `json:"timestamp"`
	ProcessName  string    `json:"processName"`
	WindowTitle  *string   `json:"windowTitle,omitempty"`
	ProcessID    int       `json:"processId"`
	CPUPercent   float64   `json:"cpuPercent"`
	MemoryBytes  int64     `json:"memoryBytes"`
	IsForeground bool      `json:"isForeground"`
	UserName     string    `json:"userName"`
	Platform     string    `json:"platform"`
	SessionID    *string   `json:"sessionId,omitempty"`
	SyncedAt     time.Time `json:"syncedAt"`
}

// ActivityLogListResponse is a paginated list of activity logs.
type ActivityLogListResponse struct {
	Data       []ActivityLogResponse `json:"data"`
	Total      int                   `json:"total"`
	Page       int                   `json:"page"`
	PerPage    int                   `json:"perPage"`
	TotalPages int                   `json:"totalPages"`
}
