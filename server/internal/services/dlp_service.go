package services

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

var dlpTriggers = map[string]bool{"usb": true, "file_transfer": true, "cloud_upload": true}
var dlpSeverities = map[string]bool{"critical": true, "high": true, "medium": true, "low": true}
var dlpActions = map[string]bool{"alert_only": true, "block": true, "alert_and_block": true}
var dlpStatuses = map[string]bool{"open": true, "investigating": true, "resolved": true, "false_positive": true}

type DlpService struct {
	repo *repository.DlpRepo
}

func NewDlpService(repo *repository.DlpRepo) *DlpService {
	return &DlpService{repo: repo}
}

type DlpAlertListResult struct {
	Data       []models.DlpAlert `json:"data"`
	Total      int               `json:"total"`
	Page       int               `json:"page"`
	PerPage    int               `json:"perPage"`
	TotalPages int               `json:"totalPages"`
}

func (s *DlpService) ListRules(ctx context.Context) ([]models.DlpRule, error) {
	return s.repo.ListRules(ctx)
}

func (s *DlpService) ListActiveRules(ctx context.Context, employeeID string) ([]models.DlpRule, error) {
	return s.repo.ListActiveRulesForEmployee(ctx, employeeID)
}

func (s *DlpService) CreateRule(ctx context.Context, req dto.CreateDlpRuleRequest) (*models.DlpRule, error) {
	name := strings.TrimSpace(req.Name)
	if name == "" {
		return nil, fmt.Errorf("name is required")
	}
	trigger := strings.ToLower(strings.TrimSpace(req.Trigger))
	if !dlpTriggers[trigger] {
		return nil, fmt.Errorf("invalid trigger")
	}
	severity := strings.ToLower(strings.TrimSpace(req.Severity))
	if severity == "" {
		severity = "medium"
	}
	if !dlpSeverities[severity] {
		return nil, fmt.Errorf("invalid severity")
	}
	action := strings.ToLower(strings.TrimSpace(req.Action))
	if action == "" {
		action = "alert_only"
	}
	if !dlpActions[action] {
		return nil, fmt.Errorf("invalid action")
	}
	enabled := true
	if req.Enabled != nil {
		enabled = *req.Enabled
	}
	applyToAll := true
	if req.ApplyToAll != nil {
		applyToAll = *req.ApplyToAll
	}
	if !applyToAll && len(req.DepartmentIDs) == 0 {
		return nil, fmt.Errorf("departmentIds required when applyToAll is false")
	}

	rule := models.DlpRule{
		Name:       name,
		Trigger:    trigger,
		Pattern:    strings.TrimSpace(req.Pattern),
		Action:     action,
		Severity:   severity,
		Enabled:    enabled,
		ApplyToAll: applyToAll,
	}
	return s.repo.CreateRule(ctx, rule, req.DepartmentIDs)
}

func (s *DlpService) UpdateRule(ctx context.Context, id string, req dto.UpdateDlpRuleRequest) (*models.DlpRule, error) {
	existing, err := s.repo.GetRule(ctx, id)
	if err != nil {
		return nil, err
	}
	if req.Name != nil {
		name := strings.TrimSpace(*req.Name)
		if name == "" {
			return nil, fmt.Errorf("name is required")
		}
		existing.Name = name
	}
	if req.Trigger != nil {
		trigger := strings.ToLower(strings.TrimSpace(*req.Trigger))
		if !dlpTriggers[trigger] {
			return nil, fmt.Errorf("invalid trigger")
		}
		existing.Trigger = trigger
	}
	if req.Pattern != nil {
		existing.Pattern = strings.TrimSpace(*req.Pattern)
	}
	if req.Action != nil {
		action := strings.ToLower(strings.TrimSpace(*req.Action))
		if !dlpActions[action] {
			return nil, fmt.Errorf("invalid action")
		}
		existing.Action = action
	}
	if req.Severity != nil {
		severity := strings.ToLower(strings.TrimSpace(*req.Severity))
		if !dlpSeverities[severity] {
			return nil, fmt.Errorf("invalid severity")
		}
		existing.Severity = severity
	}
	if req.Enabled != nil {
		existing.Enabled = *req.Enabled
	}
	if req.ApplyToAll != nil {
		existing.ApplyToAll = *req.ApplyToAll
	}
	if !existing.ApplyToAll {
		deps := existing.DepartmentIDs
		if req.DepartmentIDs != nil {
			deps = *req.DepartmentIDs
		}
		if len(deps) == 0 {
			return nil, fmt.Errorf("departmentIds required when applyToAll is false")
		}
	}
	return s.repo.UpdateRule(ctx, id, *existing, req.DepartmentIDs)
}

func (s *DlpService) DeleteRule(ctx context.Context, id string) error {
	return s.repo.SoftDeleteRule(ctx, id)
}

func (s *DlpService) SyncAlerts(ctx context.Context, employeeID string, deviceID *string, entries []dto.DlpAlertEntry) (int, error) {
	parsed := make([]models.DlpAlert, 0, len(entries))
	for _, e := range entries {
		id := strings.TrimSpace(e.ID)
		if id == "" {
			continue
		}
		trigger := strings.ToLower(strings.TrimSpace(e.Trigger))
		if !dlpTriggers[trigger] {
			continue
		}
		severity := strings.ToLower(strings.TrimSpace(e.Severity))
		if !dlpSeverities[severity] {
			severity = "medium"
		}
		eventAt := time.Now().UTC()
		if t, err := time.Parse(time.RFC3339, e.EventAt); err == nil {
			eventAt = t.UTC()
		}
		parsed = append(parsed, models.DlpAlert{
			ID:         id,
			RuleID:     e.RuleID,
			Trigger:    trigger,
			Severity:   severity,
			FileOrURL:  e.FileOrURL,
			DetailJSON: e.DetailJSON,
			EventAt:    eventAt,
		})
	}
	return s.repo.BulkUpsertAlerts(ctx, employeeID, deviceID, parsed)
}

func (s *DlpService) ListAlerts(ctx context.Context, p repository.DlpAlertListParams) (*DlpAlertListResult, error) {
	if p.Status != "" && !dlpStatuses[p.Status] {
		return nil, fmt.Errorf("invalid status")
	}
	if p.Severity != "" && !dlpSeverities[p.Severity] {
		return nil, fmt.Errorf("invalid severity")
	}
	if p.Trigger != "" && !dlpTriggers[p.Trigger] {
		return nil, fmt.Errorf("invalid trigger")
	}
	alerts, total, err := s.repo.ListAlerts(ctx, p)
	if err != nil {
		return nil, err
	}
	if alerts == nil {
		alerts = []models.DlpAlert{}
	}
	perPage := p.PerPage
	if perPage < 1 {
		perPage = 20
	}
	page := p.Page
	if page < 1 {
		page = 1
	}
	totalPages := 0
	if total > 0 {
		totalPages = (total + perPage - 1) / perPage
	}
	return &DlpAlertListResult{
		Data:       alerts,
		Total:      total,
		Page:       page,
		PerPage:    perPage,
		TotalPages: totalPages,
	}, nil
}

func (s *DlpService) PatchAlert(ctx context.Context, id string, req dto.PatchDlpAlertRequest) (*models.DlpAlert, error) {
	if req.Status != nil {
		st := strings.ToLower(strings.TrimSpace(*req.Status))
		if !dlpStatuses[st] {
			return nil, fmt.Errorf("invalid status")
		}
		req.Status = &st
	}
	return s.repo.PatchAlert(ctx, id, req.Status, req.AssignedTo, req.Notes)
}
