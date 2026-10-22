package services

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

var allowedTriggers = map[string]bool{
	"usb_storage": true, "usb_file": true, "web_navigate": true,
	"software_install": true, "software_uninstall": true, "cloud_sync_folder": true,
	"file_transfer": true, "email": true,
}
var allowedActions = map[string]bool{"alert": true, "block": true, "alert_block": true}
var allowedSeverities = map[string]bool{"critical": true, "high": true, "medium": true, "low": true}
var allowedAlertStatuses = map[string]bool{
	"open": true, "investigating": true, "resolved": true, "false_positive": true,
}

type DlpService struct {
	repo               *repository.DlpRepo
	removalCodeTTLMin  int
}

func NewDlpService(repo *repository.DlpRepo, removalCodeTTLMin int) *DlpService {
	if removalCodeTTLMin <= 0 {
		removalCodeTTLMin = 60
	}
	return &DlpService{repo: repo, removalCodeTTLMin: removalCodeTTLMin}
}

// ── Rules ───────────────────────────────────────────────────────────────────

func (s *DlpService) ListRules(ctx context.Context) ([]models.DlpRule, error) {
	return s.repo.ListRules(ctx)
}

func (s *DlpService) CreateRule(ctx context.Context, req dto.CreateDlpRuleRequest) (*models.DlpRule, error) {
	name := strings.TrimSpace(req.Name)
	if name == "" {
		return nil, fmt.Errorf("name is required")
	}
	trigger := strings.TrimSpace(req.Trigger)
	if !allowedTriggers[trigger] {
		return nil, fmt.Errorf("invalid trigger")
	}
	action := strings.TrimSpace(req.Action)
	if action == "" {
		action = "alert"
	}
	if !allowedActions[action] {
		return nil, fmt.Errorf("invalid action")
	}
	severity := strings.TrimSpace(req.Severity)
	if severity == "" {
		severity = "medium"
	}
	if !allowedSeverities[severity] {
		return nil, fmt.Errorf("invalid severity")
	}
	enabled := true
	if req.Enabled != nil {
		enabled = *req.Enabled
	}
	return s.repo.CreateRule(ctx, name, trigger, req.Match, req.Scope, action, severity, enabled)
}

func (s *DlpService) UpdateRule(ctx context.Context, id string, req dto.UpdateDlpRuleRequest) (*models.DlpRule, error) {
	if req.Trigger != nil && !allowedTriggers[*req.Trigger] {
		return nil, fmt.Errorf("invalid trigger")
	}
	if req.Action != nil && !allowedActions[*req.Action] {
		return nil, fmt.Errorf("invalid action")
	}
	if req.Severity != nil && !allowedSeverities[*req.Severity] {
		return nil, fmt.Errorf("invalid severity")
	}
	rule, err := s.repo.UpdateRule(ctx, id, req.Name, req.Enabled, req.Trigger, req.Match, req.Action, req.Severity, req.Scope)
	if err != nil {
		return nil, err
	}
	if rule == nil {
		return nil, fmt.Errorf("dlp rule not found")
	}
	return rule, nil
}

func (s *DlpService) DeleteRule(ctx context.Context, id string) error {
	return s.repo.SoftDeleteRule(ctx, id)
}

// Publish compiles enabled rules + shadow deny into an immutable policy version.
func (s *DlpService) Publish(ctx context.Context, publishedBy *string) (*dto.PublishDlpPolicyResponse, error) {
	rules, err := s.repo.ListEnabledRules(ctx)
	if err != nil {
		return nil, err
	}
	domains, appNames, err := s.repo.ListUnauthorizedShadowDeny(ctx)
	if err != nil {
		return nil, err
	}
	if domains == nil {
		domains = []string{}
	}
	if appNames == nil {
		appNames = []string{}
	}

	type bundleRule struct {
		ID       string          `json:"id"`
		Name     string          `json:"name"`
		Enabled  bool            `json:"enabled"`
		Trigger  string          `json:"trigger"`
		Match    json.RawMessage `json:"match"`
		Action   string          `json:"action"`
		Severity string          `json:"severity"`
		Scope    json.RawMessage `json:"scope"`
		Alert    bool            `json:"alert"`
	}
	br := make([]bundleRule, 0, len(rules))
	for _, rule := range rules {
		alert := rule.Action == "alert" || rule.Action == "alert_block"
		br = append(br, bundleRule{
			ID: rule.ID, Name: rule.Name, Enabled: rule.Enabled,
			Trigger: rule.Trigger, Match: rule.MatchJSON, Action: rule.Action,
			Severity: rule.Severity, Scope: rule.ScopeJSON, Alert: alert,
		})
	}

	placeholder, err := json.Marshal(map[string]any{
		"policyVersion": 0,
		"rules":         br,
		"shadowDeny": map[string]any{
			"domains":    domains,
			"publishers": []string{},
			"appNames":   appNames,
		},
	})
	if err != nil {
		return nil, err
	}
	ver, err := s.repo.InsertPolicyVersion(ctx, placeholder, "pending", publishedBy)
	if err != nil {
		return nil, err
	}

	// Hash is stored on dlp_policy_versions.bundle_hash and returned beside the
	// bundle on pull — keep it out of the JSON body so the digest is stable.
	finalObj := map[string]any{
		"policyVersion": ver.Version,
		"publishedAt":   ver.PublishedAt.UTC().Format(time.RFC3339),
		"rules":         br,
		"shadowDeny": map[string]any{
			"domains":    domains,
			"publishers": []string{},
			"appNames":   appNames,
		},
	}
	final, err := json.Marshal(finalObj)
	if err != nil {
		return nil, err
	}
	sum := sha256.Sum256(final)
	finalHash := "sha256:" + hex.EncodeToString(sum[:])
	if err := s.repo.UpdatePolicyBundle(ctx, ver.Version, final, finalHash); err != nil {
		return nil, err
	}

	return &dto.PublishDlpPolicyResponse{
		PolicyVersion: ver.Version,
		Hash:          finalHash,
		PublishedAt:   ver.PublishedAt,
		RuleCount:     len(br),
	}, nil
}

// ── Device policy ───────────────────────────────────────────────────────────

func (s *DlpService) PullPolicy(ctx context.Context, sinceVersion int64) (*dto.DlpPolicyPullResponse, error) {
	latest, err := s.repo.LatestPolicyVersion(ctx)
	if err != nil {
		return nil, err
	}
	if latest == nil {
		empty, _ := json.Marshal(map[string]any{
			"policyVersion": 0,
			"rules":         []any{},
			"shadowDeny":    map[string]any{"domains": []string{}, "publishers": []string{}, "appNames": []string{}},
		})
		return &dto.DlpPolicyPullResponse{
			PolicyVersion: 0,
			Hash:          "",
			Bundle:        empty,
			Unchanged:     sinceVersion == 0,
		}, nil
	}
	pub := latest.PublishedAt
	if sinceVersion > 0 && sinceVersion >= latest.Version {
		return &dto.DlpPolicyPullResponse{
			PolicyVersion: latest.Version,
			Hash:          latest.BundleHash,
			PublishedAt:   &pub,
			Bundle:        latest.BundleJSON,
			Unchanged:     true,
		}, nil
	}
	return &dto.DlpPolicyPullResponse{
		PolicyVersion: latest.Version,
		Hash:          latest.BundleHash,
		PublishedAt:   &pub,
		Bundle:        latest.BundleJSON,
	}, nil
}

func (s *DlpService) Health(ctx context.Context, _ dto.DlpHealthRequest) (*dto.DlpHealthResponse, error) {
	latest, err := s.repo.LatestPolicyVersion(ctx)
	if err != nil {
		return nil, err
	}
	ver := int64(0)
	if latest != nil {
		ver = latest.Version
	}
	return &dto.DlpHealthResponse{OK: true, LatestVersion: ver}, nil
}

// ── Alerts ──────────────────────────────────────────────────────────────────

func (s *DlpService) SyncAlerts(ctx context.Context, employeeID, machineID string, req dto.SyncDlpAlertsRequest) (*dto.SyncDlpAlertsResponse, error) {
	items := make([]struct {
		ID            string
		RuleID        *string
		PolicyVersion int64
		Type          string
		Severity      string
		ActionTaken   string
		OccurredAt    time.Time
		Evidence      json.RawMessage
	}, 0, len(req.Alerts))
	for _, a := range req.Alerts {
		items = append(items, struct {
			ID            string
			RuleID        *string
			PolicyVersion int64
			Type          string
			Severity      string
			ActionTaken   string
			OccurredAt    time.Time
			Evidence      json.RawMessage
		}{
			ID: a.ID, RuleID: a.RuleID, PolicyVersion: a.PolicyVersion,
			Type: a.Type, Severity: a.Severity, ActionTaken: a.ActionTaken,
			OccurredAt: a.OccurredAt, Evidence: a.Evidence,
		})
	}
	accepted, rejected, err := s.repo.UpsertAlerts(ctx, employeeID, machineID, items)
	if err != nil {
		return nil, err
	}
	return &dto.SyncDlpAlertsResponse{AcceptedIDs: accepted, RejectedIDs: rejected}, nil
}

func (s *DlpService) ListAlerts(ctx context.Context, f repository.DlpAlertListFilter) (*dto.DlpAlertListResponse, error) {
	alerts, total, err := s.repo.ListAlerts(ctx, f)
	if err != nil {
		return nil, err
	}
	// Counts follow the same filters (minus the status filter) so the status
	// chips on the web page stay meaningful while a status is selected.
	open, inv, res, fp, err := s.repo.AlertStatusCounts(ctx, f)
	if err != nil {
		return nil, err
	}
	types, err := s.repo.AlertTypeCounts(ctx, f)
	if err != nil {
		return nil, err
	}
	out := make([]any, len(alerts))
	for i := range alerts {
		out[i] = alerts[i]
	}
	return &dto.DlpAlertListResponse{
		Alerts: out, Total: total, Page: f.Page, PerPage: f.PerPage,
		Open: open, Investigating: inv, Resolved: res,
		FalsePositive: fp, Types: types,
	}, nil
}

func (s *DlpService) UpdateAlert(ctx context.Context, id string, req dto.UpdateDlpAlertRequest, changedByUserID string) (*models.DlpAlert, error) {
	if req.Status != nil && !allowedAlertStatuses[*req.Status] {
		return nil, fmt.Errorf("invalid status")
	}
	patch := repository.UpdateAlertPatch{
		Status:        req.Status,
		AssignedTo:    req.AssignedTo,
		Notes:         req.Notes,
		ChangedBy:     changedByUserID,
		ChangedByName: s.repo.UserDisplayName(ctx, changedByUserID),
	}
	if patch.ChangedByName == "" && changedByUserID != "" {
		patch.ChangedByName = "Unknown user"
	}
	a, err := s.repo.UpdateAlert(ctx, id, patch)
	if err != nil {
		return nil, err
	}
	if a == nil {
		return nil, fmt.Errorf("dlp alert not found")
	}
	return a, nil
}

func (s *DlpService) ListAlertHistory(ctx context.Context, alertID string) (*dto.DlpAlertHistoryResponse, error) {
	events, err := s.repo.ListAlertEvents(ctx, alertID)
	if err != nil {
		return nil, err
	}
	out := make([]dto.DlpAlertHistoryEvent, len(events))
	for i, e := range events {
		out[i] = dto.DlpAlertHistoryEvent{
			ID: e.ID, AlertID: e.AlertID, ChangedBy: e.ChangedBy,
			ChangedByName: e.ChangedByName, Field: e.Field,
			OldValue: e.OldValue, NewValue: e.NewValue, CreatedAt: e.CreatedAt,
		}
	}
	return &dto.DlpAlertHistoryResponse{Events: out}, nil
}

// ── Shadow IT ───────────────────────────────────────────────────────────────

func (s *DlpService) ListShadowIt(ctx context.Context, f repository.ShadowItListFilter) (*dto.ShadowItListResponse, error) {
	apps, total, err := s.repo.ListShadowItApps(ctx, f)
	if err != nil {
		return nil, err
	}
	ap, un, ur, err := s.repo.ShadowItStatusCounts(ctx)
	if err != nil {
		return nil, err
	}
	out := make([]any, len(apps))
	for i := range apps {
		out[i] = apps[i]
	}
	return &dto.ShadowItListResponse{
		Apps: out, Total: total, Page: f.Page, PerPage: f.PerPage,
		Approved: ap, Unauthorized: un, UnderReview: ur,
	}, nil
}

func (s *DlpService) UpdateShadowIt(ctx context.Context, id int64, req dto.UpdateShadowItAppRequest) (*models.ShadowItApp, error) {
	auth := req.AuthStatus
	if req.Action != nil {
		switch strings.ToLower(*req.Action) {
		case "approve":
			v := "approved"
			auth = &v
		case "block":
			v := "unauthorized"
			auth = &v
		case "flag":
			v := "under_review"
			auth = &v
		}
	}
	if auth != nil {
		switch *auth {
		case "approved", "unauthorized", "under_review":
		default:
			return nil, fmt.Errorf("invalid authStatus")
		}
	}
	if req.Risk != nil {
		switch *req.Risk {
		case "high", "medium", "low":
		default:
			return nil, fmt.Errorf("invalid risk")
		}
	}
	a, err := s.repo.UpdateShadowItApp(ctx, id, auth, req.Risk)
	if err != nil {
		return nil, err
	}
	if a == nil {
		return nil, fmt.Errorf("shadow it app not found")
	}
	return a, nil
}

// ── Removal codes ───────────────────────────────────────────────────────────

func (s *DlpService) IssueRemovalCode(ctx context.Context, req dto.IssueRemovalCodeRequest, issuedBy *string) (*dto.IssueRemovalCodeResponse, error) {
	machineID := strings.TrimSpace(req.MachineID)
	employeeID := strings.TrimSpace(req.EmployeeID)
	if machineID == "" || employeeID == "" {
		return nil, fmt.Errorf("machineId and employeeId are required")
	}
	purpose := strings.TrimSpace(req.Purpose)
	if purpose == "" {
		purpose = "both"
	}
	if purpose != "stop" && purpose != "uninstall" && purpose != "both" {
		return nil, fmt.Errorf("invalid purpose")
	}
	ttl := req.TTLMinutes
	if ttl <= 0 {
		ttl = s.removalCodeTTLMin
	}
	raw, err := randomRemovalCode()
	if err != nil {
		return nil, err
	}
	hash := repository.HashRemovalToken(raw)
	expires := time.Now().UTC().Add(time.Duration(ttl) * time.Minute)
	if _, err := s.repo.InsertRemovalChallenge(ctx, machineID, employeeID, hash, purpose, expires, issuedBy); err != nil {
		return nil, err
	}
	return &dto.IssueRemovalCodeResponse{
		Code: raw, ExpiresAt: expires, Purpose: purpose, MachineID: machineID,
	}, nil
}

func (s *DlpService) VerifyRemovalCode(ctx context.Context, machineID string, req dto.VerifyRemovalCodeRequest) (*dto.VerifyRemovalCodeResponse, error) {
	code := strings.TrimSpace(req.Code)
	if code == "" || machineID == "" {
		return &dto.VerifyRemovalCodeResponse{Valid: false}, nil
	}
	hash := repository.HashRemovalToken(code)
	ok, purpose, err := s.repo.ConsumeRemovalChallenge(ctx, machineID, hash, strings.TrimSpace(req.Purpose))
	if err != nil {
		return nil, err
	}
	return &dto.VerifyRemovalCodeResponse{Valid: ok, Purpose: purpose, MachineID: machineID}, nil
}

func randomRemovalCode() (string, error) {
	b := make([]byte, 18)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	// URL-safe, no padding — easy to type/paste.
	return base64.RawURLEncoding.EncodeToString(b), nil
}
