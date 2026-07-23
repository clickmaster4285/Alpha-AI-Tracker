package services

import (
	"context"
	"fmt"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

// ActivityLogService handles business logic for activity logs.
type ActivityLogService struct {
	repo        *repository.ActivityLogRepo
	employeeRepo *repository.EmployeeRepo
}

// NewActivityLogService creates a new ActivityLogService.
func NewActivityLogService(repo *repository.ActivityLogRepo, employeeRepo *repository.EmployeeRepo) *ActivityLogService {
	return &ActivityLogService{repo: repo, employeeRepo: employeeRepo}
}

// SyncLogs accepts a batch of activity logs from a desktop client.
// Only processes logs for the authenticated employee.
func (s *ActivityLogService) SyncLogs(ctx context.Context, req *dto.SyncActivityLogsRequest) (*dto.SyncActivityLogsResponse, error) {
	// Verify employee exists
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}

	if len(req.Logs) == 0 {
		return &dto.SyncActivityLogsResponse{Synced: 0, Message: "No logs to sync"}, nil
	}

	// Convert DTOs to models
	now := time.Now()
	activityLogs := make([]models.ActivityLog, 0, len(req.Logs))
	for _, entry := range req.Logs {
		ts, err := time.Parse(time.RFC3339, entry.Timestamp)
		if err != nil {
			ts = now
		}

		activityLogs = append(activityLogs, models.ActivityLog{
			ID:           entry.ID,
			EmployeeID:   req.EmployeeID,
			MachineID:    entry.MachineID,
			Timestamp:    ts,
			ProcessName:  entry.ProcessName,
			WindowTitle:  entry.WindowTitle,
			ProcessID:    entry.ProcessID,
			CPUPercent:   entry.CPUPercent,
			MemoryBytes:  entry.MemoryBytes,
			IsForeground: entry.IsForeground,
			UserName:     entry.UserName,
			Platform:     entry.Platform,
			SessionID:    entry.SessionID,
			EmployeeName: entry.EmployeeName,
			SyncedAt:     now,
		})
	}

	inserted, err := s.repo.BulkInsert(ctx, activityLogs)
	if err != nil {
		return nil, fmt.Errorf("bulk insert: %w", err)
	}

	return &dto.SyncActivityLogsResponse{
		Synced:  inserted,
		Message: fmt.Sprintf("Synced %d of %d logs", inserted, len(req.Logs)),
	}, nil
}

// List returns a paginated list of activity logs.
func (s *ActivityLogService) List(ctx context.Context, params repository.ActivityLogListParams) (*dto.ActivityLogListResponse, error) {
	result, err := s.repo.List(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("list activity logs: %w", err)
	}

	logs := make([]dto.ActivityLogResponse, len(result.Logs))
	for i, l := range result.Logs {
		logs[i] = dto.ActivityLogResponse{
			ID:           l.ID,
			EmployeeID:   l.EmployeeID,
			EmployeeName: l.EmployeeName,
			MachineID:    l.MachineID,
			Timestamp:    l.Timestamp,
			ProcessName:  l.ProcessName,
			WindowTitle:  l.WindowTitle,
			ProcessID:    l.ProcessID,
			CPUPercent:   l.CPUPercent,
			MemoryBytes:  l.MemoryBytes,
			IsForeground: l.IsForeground,
			UserName:     l.UserName,
			Platform:     l.Platform,
			SessionID:    l.SessionID,
			SyncedAt:     l.SyncedAt,
		}
	}

	return &dto.ActivityLogListResponse{
		Data:       logs,
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}
