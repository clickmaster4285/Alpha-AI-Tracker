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

// NewSchemaService handles business logic for Phase 1 & Phase 2 tables.
type NewSchemaService struct {
	repo         *repository.NewSchemaRepo
	employeeRepo *repository.EmployeeRepo
}

func NewNewSchemaService(repo *repository.NewSchemaRepo, employeeRepo *repository.EmployeeRepo) *NewSchemaService {
	return &NewSchemaService{repo: repo, employeeRepo: employeeRepo}
}

// ── device_hardware_info ──

func (s *NewSchemaService) SyncDeviceHardware(ctx context.Context, req *dto.SyncDeviceHardwareRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	entries := make([]models.DeviceHardwareInfo, 0, len(req.Entries))
	for _, e := range req.Entries {
		ts, err := time.Parse(time.RFC3339, e.CollectedAt)
		if err != nil {
			ts = now
		}
		entries = append(entries, models.DeviceHardwareInfo{
			ID:             e.ID,
			EmployeeID:     req.EmployeeID,
			MacAddress:     e.MacAddress,
			Hostname:       e.Hostname,
			OsName:         e.OsName,
			OsVersion:      e.OsVersion,
			CpuModel:       e.CpuModel,
			CpuCores:       e.CpuCores,
			RamTotalMB:     e.RamTotalMb,
			StorageDevices: e.StorageDevices,
			GpuModel:       e.GpuModel,
			GpuVramMB:      e.GpuVramMb,
			CollectedAt:    ts,
			SyncedAt:       &now,
		})
	}

	inserted, err := s.repo.BulkInsertDeviceHardware(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk insert device_hardware: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── installed_applications ──

// SyncInstalledApps ingests installed applications as a company-global catalog
// (deduplicated by app_fingerprint = desktop_id|binary_name) plus per-employee link rows
// holding install-specific metadata (version, path, install date).
func (s *NewSchemaService) SyncInstalledApps(ctx context.Context, req *dto.SyncInstalledAppsRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	inserted := 0
	for _, e := range req.Entries {
		dets, _ := time.Parse(time.RFC3339, e.DetectedAt)
		var installDate *time.Time
		if e.InstallDate != nil {
			if t, err := time.Parse(time.RFC3339, *e.InstallDate); err == nil {
				installDate = &t
			}
		}

		cat := models.InstalledApplication{
			ID:              e.ID,
			EmployeeID:      req.EmployeeID,
			AppName:         e.AppName,
			AppVersion:      e.AppVersion,
			Publisher:       e.Publisher,
			InstallPath:     e.InstallPath,
			InstallDate:     installDate,
			UninstallString: e.UninstallString,
			ChangeType:      e.ChangeType,
			DetectedAt:      dets,
			SyncedAt:        &now,
			BinaryName:      e.BinaryName,
			IsBrowser:       e.IsBrowser,
			DesktopID:       e.DesktopID,
			Categories:      e.Categories,
			AppFingerprint:  appFingerprint(e.DesktopID, e.BinaryName),
		}

		tx, err := s.repo.Begin(ctx)
		if err != nil {
			return nil, fmt.Errorf("begin tx: %w", err)
		}

		catalogID, err := s.repo.UpsertApplicationCatalog(ctx, tx, cat)
		if err != nil {
			tx.Rollback(ctx)
			return nil, err
		}

		err = s.repo.UpsertEmployeeAppLink(ctx, tx, models.EmployeeInstalledApplication{
			EmployeeID:             req.EmployeeID,
			InstalledApplicationID: catalogID,
			AppVersion:             e.AppVersion,
			Publisher:              e.Publisher,
			InstallPath:            e.InstallPath,
			InstallDate:            installDate,
		})
		if err != nil {
			tx.Rollback(ctx)
			return nil, err
		}

		if err := tx.Commit(ctx); err != nil {
			return nil, fmt.Errorf("commit tx: %w", err)
		}
		inserted++
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// appFingerprint builds the catalog natural key for applications.
func appFingerprint(desktopID, binaryName string) string {
	desktopID = strings.TrimSpace(desktopID)
	binaryName = strings.TrimSpace(binaryName)
	return desktopID + "|" + binaryName
}

// packageFingerprint builds the catalog natural key for packages.
func packageFingerprint(packageName, sourceManager string) string {
	return strings.TrimSpace(packageName) + "|" + strings.TrimSpace(sourceManager)
}

// ── installed_packages ──

// SyncInstalledPackages ingests installed packages as a company-global catalog
// (deduplicated by package_fingerprint = package_name|source_manager) plus per-employee
// link rows holding install-specific metadata (version, path, publisher).
func (s *NewSchemaService) SyncInstalledPackages(ctx context.Context, req *dto.SyncInstalledPackagesRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	inserted := 0
	for _, e := range req.Entries {
		dets, _ := time.Parse(time.RFC3339, e.DetectedAt)

		cat := models.InstalledPackage{
			ID:                e.ID,
			EmployeeID:        req.EmployeeID,
			PackageName:       e.PackageName,
			Version:           e.Version,
			Category:          e.Category,
			SourceManager:     e.SourceManager,
			InstallPath:       e.InstallPath,
			Publisher:         e.Publisher,
			Description:       e.Description,
			DetectedAt:        dets,
			SyncedAt:          &now,
			PackageFingerprint: packageFingerprint(e.PackageName, e.SourceManager),
		}

		tx, err := s.repo.Begin(ctx)
		if err != nil {
			return nil, fmt.Errorf("begin tx: %w", err)
		}

		catalogID, err := s.repo.UpsertPackageCatalog(ctx, tx, cat)
		if err != nil {
			tx.Rollback(ctx)
			return nil, err
		}

		err = s.repo.UpsertEmployeePackageLink(ctx, tx, models.EmployeeInstalledPackage{
			EmployeeID:         req.EmployeeID,
			InstalledPackageID: catalogID,
			Version:            e.Version,
			Publisher:          e.Publisher,
			InstallPath:        e.InstallPath,
		})
		if err != nil {
			tx.Rollback(ctx)
			return nil, err
		}

		if err := tx.Commit(ctx); err != nil {
			return nil, fmt.Errorf("commit tx: %w", err)
		}
		inserted++
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── network_info ──

func (s *NewSchemaService) SyncNetworkInfo(ctx context.Context, req *dto.SyncNetworkInfoRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	entries := make([]models.NetworkInfo, 0, len(req.Entries))
	for _, e := range req.Entries {
		ts, _ := time.Parse(time.RFC3339, e.CollectedAt)
		entries = append(entries, models.NetworkInfo{
			ID:                   e.ID,
			EmployeeID:           req.EmployeeID,
			PublicIP:             e.PublicIP,
			PrivateIP:            e.PrivateIP,
			MacAddress:           e.MacAddress,
			NetworkInterfaceName: e.NetworkInterfaceName,
			CollectedAt:          ts,
			SyncedAt:             &now,
		})
	}

	inserted, err := s.repo.BulkInsertNetworkInfo(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk insert network_info: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── session_events ──

func (s *NewSchemaService) SyncSessionEvents(ctx context.Context, req *dto.SyncSessionEventsRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	entries := make([]models.SessionEvent, 0, len(req.Entries))
	for _, e := range req.Entries {
		ts, _ := time.Parse(time.RFC3339, e.EventAt)
		entries = append(entries, models.SessionEvent{
			ID:         e.ID,
			EmployeeID: req.EmployeeID,
			EventType:  e.EventType,
			OsUsername: e.OsUsername,
			EventAt:    ts,
			SyncedAt:   &now,
		})
	}

	inserted, err := s.repo.BulkInsertSessionEvents(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk insert session_events: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── app_sessions ──

func (s *NewSchemaService) SyncAppSessions(ctx context.Context, req *dto.SyncAppSessionsRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	entries := make([]models.AppSession, 0, len(req.Entries))
	for _, e := range req.Entries {
		started, _ := time.Parse(time.RFC3339, e.StartedAt)
		var ended *time.Time
		if e.EndedAt != nil {
			if t, err := time.Parse(time.RFC3339, *e.EndedAt); err == nil {
				ended = &t
			}
		}
		entries = append(entries, models.AppSession{
			ID:                 e.ID,
			EmployeeID:         req.EmployeeID,
			ProcessName:        e.ProcessName,
			AppDisplayName:     e.AppDisplayName,
			StartedAt:          started,
			EndedAt:            ended,
			MachineID:          e.MachineID,
			SessionID:          e.SessionID,
			Platform:           e.Platform,
			ProcessID:          e.ProcessID,
			ParentProcessID:    e.ParentProcessID,
			InstalledAppID:     e.InstalledAppID,
			InstalledPackageID: e.InstalledPackageID,
			GroupedBy:          e.GroupedBy,
			CgroupScope:        e.CgroupScope,
			ContextLabel:       e.ContextLabel,
			SyncedAt:           &now,
		})
	}

	inserted, err := s.repo.BulkInsertAppSessions(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk insert app_sessions: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d sessions", inserted, len(req.Entries))}, nil
}

// ── app_items ──

func (s *NewSchemaService) SyncAppItems(ctx context.Context, req *dto.SyncAppItemsRequest) (*dto.SyncBatchResponse, error) {
	emp, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("verify employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}
	if len(req.Entries) == 0 {
		return &dto.SyncBatchResponse{Synced: 0, Message: "No entries to sync"}, nil
	}

	now := time.Now()
	entries := make([]models.AppItem, 0, len(req.Entries))
	for _, e := range req.Entries {
		opened, _ := time.Parse(time.RFC3339, e.OpenedAt)
		var closed *time.Time
		if e.ClosedAt != nil {
			if t, err := time.Parse(time.RFC3339, *e.ClosedAt); err == nil {
				closed = &t
			}
		}
		entries = append(entries, models.AppItem{
			ID:           e.ID,
			EmployeeID:   req.EmployeeID,
			AppSessionID: e.AppSessionID,
			ParentItemID: e.ParentItemID,
			ItemType:     e.ItemType,
			Title:        e.Title,
			Identifier:   e.Identifier,
			Url:          e.Url,
			Domain:       e.Domain,
			OpenedAt:     opened,
			ClosedAt:     closed,
			ProcessID:    e.ProcessID,
			ObjectType:   e.ObjectType,
			Action:       e.Action,
			JourneyID:    e.JourneyID,
			Sequence:     e.Sequence,
			PreviousPath: e.PreviousPath,
			CurrentPath:  e.CurrentPath,
			WindowID:     e.WindowID,
			TabID:        e.TabID,
			MetadataJSON: e.MetadataJSON,
			SyncedAt:     &now,
		})
	}

	inserted, err := s.repo.BulkInsertAppItems(ctx, entries)
	if err != nil {
		return nil, fmt.Errorf("bulk insert app_items: %w", err)
	}
	return &dto.SyncBatchResponse{Synced: inserted, Message: fmt.Sprintf("Synced %d of %d entries", inserted, len(req.Entries))}, nil
}

// ── List app_sessions (for web dashboard) ──

func (s *NewSchemaService) ListAppSessions(ctx context.Context, params repository.AppSessionListParams) (*dto.AppSessionListResponse, error) {
	result, err := s.repo.ListAppSessions(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("list app sessions: %w", err)
	}

	sessions := make([]dto.AppSessionResponse, len(result.Sessions))
	for i, s := range result.Sessions {
		sessions[i] = dto.AppSessionResponse{
			ID:                 s.ID,
			EmployeeID:         s.EmployeeID,
			ProcessName:        s.ProcessName,
			AppDisplayName:     s.AppDisplayName,
			StartedAt:          s.StartedAt,
			EndedAt:            s.EndedAt,
			MachineID:          s.MachineID,
			SessionID:          s.SessionID,
			Platform:           s.Platform,
			ProcessID:          s.ProcessID,
			ParentProcessID:    s.ParentProcessID,
			InstalledAppID:     s.InstalledAppID,
			InstalledPackageID: s.InstalledPackageID,
			GroupedBy:          s.GroupedBy,
			CgroupScope:        s.CgroupScope,
			ContextLabel:       s.ContextLabel,
			SyncedAt:           s.SyncedAt,
		}
	}

	return &dto.AppSessionListResponse{
		Data:       sessions,
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}

// ── List app items (for web dashboard — URLs, file paths, etc.) ──

func (s *NewSchemaService) ListAppItems(ctx context.Context, params repository.AppItemListParams) (*dto.AppItemListResponse, error) {
	result, err := s.repo.ListAppItems(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("list app_items: %w", err)
	}

	items := make([]dto.AppItemResponse, len(result.Items))
	for i, item := range result.Items {
		var closedAt *time.Time
		if item.ClosedAt != nil {
			t := *item.ClosedAt
			closedAt = &t
		}
		var syncedAt *time.Time
		if item.SyncedAt != nil {
			t := *item.SyncedAt
			syncedAt = &t
		}
		var parentItemID *string
		if item.ParentItemID != nil {
			t := *item.ParentItemID
			parentItemID = &t
		}
		items[i] = dto.AppItemResponse{
			ID:           item.ID,
			EmployeeID:   item.EmployeeID,
			AppSessionID: item.AppSessionID,
			ParentItemID: parentItemID,
			ItemType:     item.ItemType,
			Title:        item.Title,
			Identifier:   item.Identifier,
			Url:          item.Url,
			Domain:       item.Domain,
			OpenedAt:     item.OpenedAt,
			ClosedAt:     closedAt,
			ProcessID:    item.ProcessID,
			ObjectType:   item.ObjectType,
			Action:       item.Action,
			JourneyID:    item.JourneyID,
			Sequence:     item.Sequence,
			PreviousPath: item.PreviousPath,
			CurrentPath:  item.CurrentPath,
			WindowID:     item.WindowID,
			TabID:        item.TabID,
			MetadataJSON: item.MetadataJSON,
			SyncedAt:     syncedAt,
		}
	}

	return &dto.AppItemListResponse{
		Data:       items,
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}
