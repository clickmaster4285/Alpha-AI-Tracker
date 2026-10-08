package dto

// CreateDlpRuleRequest is the admin body for POST /dlp-rules.
type CreateDlpRuleRequest struct {
	Name          string `json:"name"`
	Trigger       string `json:"trigger"`
	Pattern       string `json:"pattern"`
	Action        string `json:"action"`
	Severity      string `json:"severity"`
	Enabled       *bool  `json:"enabled"`
	ApplyToAll    *bool  `json:"applyToAll"`
	DepartmentIDs []int  `json:"departmentIds"`
}

// UpdateDlpRuleRequest is the admin body for PUT /dlp-rules/:id.
type UpdateDlpRuleRequest struct {
	Name          *string `json:"name"`
	Trigger       *string `json:"trigger"`
	Pattern       *string `json:"pattern"`
	Action        *string `json:"action"`
	Severity      *string `json:"severity"`
	Enabled       *bool   `json:"enabled"`
	ApplyToAll    *bool   `json:"applyToAll"`
	DepartmentIDs *[]int  `json:"departmentIds"`
}

// DlpAlertEntry is one row in POST /dlp-alerts/sync.
type DlpAlertEntry struct {
	ID         string  `json:"id"`
	RuleID     *string `json:"ruleId,omitempty"`
	Trigger    string  `json:"trigger"`
	Severity   string  `json:"severity"`
	FileOrURL  string  `json:"fileOrUrl"`
	DetailJSON *string `json:"detailJson,omitempty"`
	EventAt    string  `json:"eventAt"`
}

// SyncDlpAlertsRequest is the DeviceAuth sync body.
type SyncDlpAlertsRequest struct {
	EmployeeID string          `json:"employeeId"`
	Token      string          `json:"token"`
	Entries    []DlpAlertEntry `json:"entries"`
}

// PatchDlpAlertRequest is the admin triage body.
type PatchDlpAlertRequest struct {
	Status     *string `json:"status"`
	AssignedTo *string `json:"assignedTo"`
	Notes      *string `json:"notes"`
}
