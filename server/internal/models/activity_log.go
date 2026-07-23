package models

import "time"

// ActivityLog represents a single activity log entry synced from a desktop client.
type ActivityLog struct {
	ID            string    `json:"id" db:"id"`
	EmployeeID    string    `json:"employeeId" db:"employee_id"`
	MachineID     string    `json:"machineId" db:"machine_id"`
	Timestamp     time.Time `json:"timestamp" db:"timestamp"`
	ProcessName   string    `json:"processName" db:"process_name"`
	WindowTitle   *string   `json:"windowTitle,omitempty" db:"window_title"`
	ProcessID     int       `json:"processId" db:"process_id"`
	CPUPercent    float64   `json:"cpuPercent" db:"cpu_percent"`
	MemoryBytes   int64     `json:"memoryBytes" db:"memory_bytes"`
	IsForeground  bool      `json:"isForeground" db:"is_foreground"`
	UserName      string    `json:"userName" db:"user_name"`
	Platform      string    `json:"platform" db:"platform"`
	SessionID     *string   `json:"sessionId,omitempty" db:"session_id"`
	EmployeeName  *string   `json:"employeeName,omitempty" db:"employee_name"`
	SyncedAt      time.Time `json:"syncedAt" db:"synced_at"`
	CreatedAt     time.Time `json:"createdAt" db:"created_at"`
}
