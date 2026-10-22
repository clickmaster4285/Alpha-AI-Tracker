package models

import (
	"encoding/json"
	"time"

	"github.com/jackc/pgx/v5/pgtype"
)

// DlpRule is an admin-editable DLP policy row.
type DlpRule struct {
	ID        string          `json:"id"`
	Name      string          `json:"name"`
	Enabled   bool            `json:"enabled"`
	Trigger   string          `json:"trigger"`
	MatchJSON json.RawMessage `json:"match"`
	Action    string          `json:"action"`
	Severity  string          `json:"severity"`
	ScopeJSON json.RawMessage `json:"scope"`
	CreatedAt time.Time       `json:"createdAt"`
	UpdatedAt time.Time       `json:"updatedAt"`
}

// DlpPolicyVersion is an immutable published bundle.
type DlpPolicyVersion struct {
	Version     int64           `json:"policyVersion"`
	BundleJSON  json.RawMessage `json:"bundle"`
	BundleHash  string          `json:"hash"`
	PublishedAt time.Time       `json:"publishedAt"`
	PublishedBy pgtype.UUID     `json:"-"`
}

// DlpAlert is a device-synced incident.
type DlpAlert struct {
	ID             string          `json:"id"`
	RuleID         *string         `json:"ruleId,omitempty"`
	PolicyVersion  int64           `json:"policyVersion"`
	EmployeeID     string          `json:"employeeId"`
	MachineID      string          `json:"machineId"`
	Type           string          `json:"type"`
	Severity       string          `json:"severity"`
	ActionTaken    string          `json:"actionTaken"`
	OccurredAt     time.Time       `json:"occurredAt"`
	EvidenceJSON   json.RawMessage `json:"evidence"`
	Status         string          `json:"status"`
	AssignedTo     string          `json:"assignedTo"`
	Notes          string          `json:"notes"`
	SyncedAt       time.Time       `json:"syncedAt"`
	CreatedAt      time.Time       `json:"createdAt"`
	UpdatedAt      time.Time       `json:"updatedAt"`
	EmployeeName   string          `json:"employeeName,omitempty"`
}

// DlpAlertEvent is one field change in the triage audit trail.
type DlpAlertEvent struct {
	ID             int64     `json:"id"`
	AlertID        string    `json:"alertId"`
	ChangedBy      *string   `json:"changedBy,omitempty"`
	ChangedByName  string    `json:"changedByName"`
	Field          string    `json:"field"` // status | assigned_to | notes
	OldValue       string    `json:"oldValue"`
	NewValue       string    `json:"newValue"`
	CreatedAt      time.Time `json:"createdAt"`
}

// ShadowItApp is a Shadow IT catalog row.
type ShadowItApp struct {
	ID              int64     `json:"id"`
	Name            string    `json:"name"`
	NameNormalized  string    `json:"nameNormalized"`
	Kind            string    `json:"kind"`
	AuthStatus      string    `json:"authStatus"`
	Risk            string    `json:"risk"`
	FirstDetectedAt time.Time `json:"firstDetectedAt"`
	UserCount       int       `json:"userCount"`
	LastSeenAt      time.Time `json:"lastSeenAt"`
	CreatedAt       time.Time `json:"createdAt"`
	UpdatedAt       time.Time `json:"updatedAt"`
}
