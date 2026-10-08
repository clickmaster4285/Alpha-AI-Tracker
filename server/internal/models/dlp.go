package models

import "time"

// DlpRule is an admin-defined DLP policy row.
type DlpRule struct {
	ID           string     `json:"id" db:"id"`
	Name         string     `json:"name" db:"name"`
	Trigger      string     `json:"trigger" db:"trigger"`
	Pattern      string     `json:"pattern" db:"pattern"`
	Action       string     `json:"action" db:"action"`
	Severity     string     `json:"severity" db:"severity"`
	Enabled      bool       `json:"enabled" db:"enabled"`
	ApplyToAll   bool       `json:"applyToAll" db:"apply_to_all"`
	DepartmentIDs []int     `json:"departmentIds,omitempty"`
	CreatedAt    time.Time  `json:"createdAt" db:"created_at"`
	UpdatedAt    time.Time  `json:"updatedAt" db:"updated_at"`
	DeletedAt    *time.Time `json:"-" db:"deleted_at"`
}

// DlpAlert is a client-minted DLP alert (upserted by DeviceAuth sync).
type DlpAlert struct {
	ID         string     `json:"id" db:"id"`
	EmployeeID string     `json:"employeeId" db:"employee_id"`
	DeviceID   *string    `json:"deviceId,omitempty" db:"device_id"`
	RuleID     *string    `json:"ruleId,omitempty" db:"rule_id"`
	Trigger    string     `json:"trigger" db:"trigger"`
	Severity   string     `json:"severity" db:"severity"`
	Status     string     `json:"status" db:"status"`
	FileOrURL  string     `json:"fileOrUrl" db:"file_or_url"`
	DetailJSON *string    `json:"detailJson,omitempty" db:"detail_json"`
	AssignedTo *string    `json:"assignedTo,omitempty" db:"assigned_to"`
	Notes      string     `json:"notes" db:"notes"`
	EventAt    time.Time  `json:"eventAt" db:"event_at"`
	SyncedAt   time.Time  `json:"syncedAt" db:"synced_at"`
	CreatedAt  time.Time  `json:"createdAt" db:"created_at"`
	UpdatedAt  time.Time  `json:"updatedAt" db:"updated_at"`
	DeletedAt  *time.Time `json:"-" db:"deleted_at"`
	// Joined display fields for admin list
	EmployeeName string `json:"employeeName,omitempty"`
}
