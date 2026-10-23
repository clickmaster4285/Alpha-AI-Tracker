package dto

import (
	"encoding/json"
	"time"
)

// ── Admin: DLP Rules (non-web) ───────────────────────────────────────────────

type CreateDlpRuleRequest struct {
	Name       string   `json:"name"`
	Enabled    *bool    `json:"enabled,omitempty"`
	Trigger    string   `json:"trigger"`
	Action     string   `json:"action"`
	Severity   string   `json:"severity"`
	ScopeAll   *bool    `json:"scopeAll,omitempty"`
	Extensions []string `json:"extensions"`
	Patterns   []string `json:"patterns"`
}

type UpdateDlpRuleRequest struct {
	Name       *string  `json:"name,omitempty"`
	Enabled    *bool    `json:"enabled,omitempty"`
	Trigger    *string  `json:"trigger,omitempty"`
	Action     *string  `json:"action,omitempty"`
	Severity   *string  `json:"severity,omitempty"`
	ScopeAll   *bool    `json:"scopeAll,omitempty"`
	Extensions *[]string `json:"extensions,omitempty"`
	Patterns   *[]string `json:"patterns,omitempty"`
}

type DlpRuleListResponse struct {
	Rules []any `json:"rules"`
	Total int   `json:"total"`
}

// ── Admin: Web Blocker ──────────────────────────────────────────────────────

type CreateWebBlockerRuleRequest struct {
	Name              string   `json:"name"`
	Enabled           *bool    `json:"enabled,omitempty"`
	Action            string   `json:"action"`
	Severity          string   `json:"severity"`
	ScopeAll          *bool    `json:"scopeAll,omitempty"`
	Domains           []string `json:"domains"`
	Paths             []string `json:"paths"`
	ScheduleEnabled   bool     `json:"scheduleEnabled"`
	ScheduleStart     string   `json:"scheduleStart"`
	ScheduleEnd       string   `json:"scheduleEnd"`
	ScheduleTimezone  string   `json:"scheduleTimezone"`
	ScheduleDays      []string `json:"scheduleDays"`
}

type UpdateWebBlockerRuleRequest struct {
	Name              *string  `json:"name,omitempty"`
	Enabled           *bool    `json:"enabled,omitempty"`
	Action            *string  `json:"action,omitempty"`
	Severity          *string  `json:"severity,omitempty"`
	ScopeAll          *bool    `json:"scopeAll,omitempty"`
	Domains           *[]string `json:"domains,omitempty"`
	Paths             *[]string `json:"paths,omitempty"`
	ScheduleEnabled   *bool    `json:"scheduleEnabled,omitempty"`
	ScheduleStart     *string  `json:"scheduleStart,omitempty"`
	ScheduleEnd       *string  `json:"scheduleEnd,omitempty"`
	ScheduleTimezone  *string  `json:"scheduleTimezone,omitempty"`
	ScheduleDays      *[]string `json:"scheduleDays,omitempty"`
}

type WebBlockerRuleListResponse struct {
	Rules []any `json:"rules"`
	Total int   `json:"total"`
}

type PublishDlpPolicyResponse struct {
	PolicyVersion int64     `json:"policyVersion"`
	Hash          string    `json:"hash"`
	PublishedAt   time.Time `json:"publishedAt"`
	RuleCount     int       `json:"ruleCount"`
}

// ── Device: Policy pull (wire bundle still JSON — assembled from relational rows) ─

type DlpPolicyPullResponse struct {
	PolicyVersion int64           `json:"policyVersion"`
	Hash          string          `json:"hash"`
	PublishedAt   *time.Time      `json:"publishedAt,omitempty"`
	Bundle        json.RawMessage `json:"bundle"`
	Unchanged     bool            `json:"unchanged,omitempty"`
}

// ── Device: Alert sync ──────────────────────────────────────────────────────

type SyncDlpAlertItem struct {
	ID            string            `json:"id"`
	RuleID        *string           `json:"ruleId,omitempty"`
	PolicyVersion int64             `json:"policyVersion"`
	Type          string            `json:"type"`
	Severity      string            `json:"severity"`
	ActionTaken   string            `json:"actionTaken"`
	OccurredAt    time.Time         `json:"occurredAt"`
	Evidence      map[string]string `json:"evidence"`
}

type SyncDlpAlertsRequest struct {
	Alerts []SyncDlpAlertItem `json:"alerts"`
}

type SyncDlpAlertsResponse struct {
	AcceptedIDs []string `json:"acceptedIds"`
	RejectedIDs []string `json:"rejectedIds,omitempty"`
	Action      string   `json:"action,omitempty"`
}

// ── Device: Health ──────────────────────────────────────────────────────────

type DlpHealthRequest struct {
	PolicyVersion int64  `json:"policyVersion"`
	EnforceOK     bool   `json:"enforceOk"`
	LastError     string `json:"lastError,omitempty"`
	Platform      string `json:"platform,omitempty"`
	ServiceVer    string `json:"serviceVersion,omitempty"`
}

type DlpHealthResponse struct {
	OK            bool  `json:"ok"`
	LatestVersion int64 `json:"latestPolicyVersion"`
}

// ── Admin: Alerts ───────────────────────────────────────────────────────────

type UpdateDlpAlertRequest struct {
	Status     *string `json:"status,omitempty"`
	AssignedTo *string `json:"assignedTo,omitempty"`
	Notes      *string `json:"notes,omitempty"`
}

type DlpAlertHistoryEvent struct {
	ID            int64     `json:"id"`
	AlertID       string    `json:"alertId"`
	ChangedBy     *string   `json:"changedBy,omitempty"`
	ChangedByName string    `json:"changedByName"`
	Field         string    `json:"field"`
	OldValue      string    `json:"oldValue"`
	NewValue      string    `json:"newValue"`
	CreatedAt     time.Time `json:"createdAt"`
}

type DlpAlertHistoryResponse struct {
	Events []DlpAlertHistoryEvent `json:"events"`
}

type DlpAlertListResponse struct {
	Alerts         []any    `json:"alerts"`
	Total          int      `json:"total"`
	Page           int      `json:"page"`
	PerPage        int      `json:"perPage"`
	Open           int      `json:"open"`
	Investigating  int      `json:"investigating"`
	Resolved       int      `json:"resolved"`
	FalsePositive  int      `json:"falsePositive"`
	Types          []string `json:"types"`
}

// ── Shadow IT ───────────────────────────────────────────────────────────────

type UpdateShadowItAppRequest struct {
	AuthStatus *string `json:"authStatus,omitempty"`
	Risk       *string `json:"risk,omitempty"`
	Action     *string `json:"action,omitempty"` // approve | block | flag
}

type ShadowItListResponse struct {
	Apps         []any `json:"apps"`
	Total        int   `json:"total"`
	Page         int   `json:"page"`
	PerPage      int   `json:"perPage"`
	Approved     int   `json:"approved"`
	Unauthorized int   `json:"unauthorized"`
	UnderReview  int   `json:"underReview"`
}

// ── Removal codes ───────────────────────────────────────────────────────────

type IssueRemovalCodeRequest struct {
	MachineID  string `json:"machineId"`
	EmployeeID string `json:"employeeId"`
	Purpose    string `json:"purpose"`
	TTLMinutes int    `json:"ttlMinutes,omitempty"`
}

type IssueRemovalCodeResponse struct {
	Code      string    `json:"code"`
	ExpiresAt time.Time `json:"expiresAt"`
	Purpose   string    `json:"purpose"`
	MachineID string    `json:"machineId"`
}

type VerifyRemovalCodeRequest struct {
	Code    string `json:"code"`
	Purpose string `json:"purpose,omitempty"`
}

type VerifyRemovalCodeResponse struct {
	Valid     bool   `json:"valid"`
	Purpose   string `json:"purpose,omitempty"`
	MachineID string `json:"machineId,omitempty"`
}
