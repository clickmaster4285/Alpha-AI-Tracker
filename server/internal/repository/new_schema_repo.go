package repository

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type NewSchemaRepo struct {
	pool *pgxpool.Pool
}

func NewNewSchemaRepo(pool *pgxpool.Pool) *NewSchemaRepo {
	return &NewSchemaRepo{pool: pool}
}

// Begin starts a transaction for multi-statement ingestion (catalog upsert + link upsert).
func (r *NewSchemaRepo) Begin(ctx context.Context) (pgx.Tx, error) {
	return r.pool.Begin(ctx)
}

// ────────────────────────────────
// Device Hardware Info
// ────────────────────────────────

func (r *NewSchemaRepo) BulkInsertDeviceHardware(ctx context.Context, entries []models.DeviceHardwareInfo) (int, error) {
	if len(entries) == 0 {
		return 0, nil
	}
	batchSize := 500
	inserted := 0
	for i := 0; i < len(entries); i += batchSize {
		end := i + batchSize
		if end > len(entries) {
			end = len(entries)
		}
		batch := entries[i:end]
		valueStrings := make([]string, 0, len(batch))
		args := make([]interface{}, 0, len(batch)*12)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4,
				argIdx+5, argIdx+6, argIdx+7, argIdx+8, argIdx+9,
				argIdx+10, argIdx+11, argIdx+12, argIdx+13,
			))
			storageDevices := e.StorageDevices
			if strings.TrimSpace(storageDevices) == "" {
				storageDevices = "[]"
			}
			args = append(args,
				e.ID, e.EmployeeID, e.MacAddress, e.Hostname, e.OsName, e.OsVersion,
				e.CpuModel, e.CpuCores, e.RamTotalMB, storageDevices, e.GpuModel, e.GpuVramMB, e.CollectedAt, time.Now(),
			)
			argIdx += 14
		}

		query := fmt.Sprintf(`
			INSERT INTO device_hardware_info
				(id, employee_id, mac_address, hostname, os_name, os_version,
				 cpu_model, cpu_cores, ram_total_mb, storage_devices, gpu_model, gpu_vram_mb, collected_at, synced_at)
			VALUES %s
			ON CONFLICT (id) DO NOTHING
		`, strings.Join(valueStrings, ", "))

		tag, err := r.pool.Exec(ctx, query, args...)
		if err != nil {
			return inserted, fmt.Errorf("bulk insert device_hardware_info: %w", err)
		}
		inserted += int(tag.RowsAffected())
	}
	return inserted, nil
}

// ────────────────────────────────
// Installed Applications
// ────────────────────────────────

func (r *NewSchemaRepo) BulkInsertInstalledApps(ctx context.Context, entries []models.InstalledApplication) (int, error) {
	if len(entries) == 0 {
		return 0, nil
	}
	batchSize := 500
	inserted := 0
	for i := 0; i < len(entries); i += batchSize {
		end := i + batchSize
		if end > len(entries) {
			end = len(entries)
		}
		batch := entries[i:end]
		valueStrings := make([]string, 0, len(batch))
		args := make([]interface{}, 0, len(batch)*10)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4,
				argIdx+5, argIdx+6, argIdx+7, argIdx+8, argIdx+9,
				argIdx+10, argIdx+11, argIdx+12, argIdx+13, argIdx+14,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.AppName, e.AppVersion, e.Publisher,
				e.InstallPath, e.InstallDate, e.UninstallString, e.ChangeType, e.DetectedAt,
				time.Now(), e.BinaryName, e.IsBrowser, e.DesktopID, e.Categories,
			)
			argIdx += 15
		}

		query := fmt.Sprintf(`
			INSERT INTO installed_applications
				(id, employee_id, app_name, app_version, publisher, install_path,
				 install_date, uninstall_string, change_type, detected_at, synced_at,
				 binary_name, is_browser, desktop_id, categories)
			VALUES %s
			ON CONFLICT (id) DO NOTHING
		`, strings.Join(valueStrings, ", "))

		tag, err := r.pool.Exec(ctx, query, args...)
		if err != nil {
			return inserted, fmt.Errorf("bulk insert installed_applications: %w", err)
		}
		inserted += int(tag.RowsAffected())
	}
	return inserted, nil
}

// ────────────────────────────────
// Installed Packages
// ────────────────────────────────

func (r *NewSchemaRepo) BulkInsertInstalledPackages(ctx context.Context, entries []models.InstalledPackage) (int, error) {
	if len(entries) == 0 {
		return 0, nil
	}
	batchSize := 500
	inserted := 0
	for i := 0; i < len(entries); i += batchSize {
		end := i + batchSize
		if end > len(entries) {
			end = len(entries)
		}
		batch := entries[i:end]
		valueStrings := make([]string, 0, len(batch))
		args := make([]interface{}, 0, len(batch)*10)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4,
				argIdx+5, argIdx+6, argIdx+7, argIdx+8, argIdx+9, argIdx+10,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.PackageName, e.Version, e.Category,
				e.SourceManager, e.InstallPath, e.Publisher, e.Description, e.DetectedAt, time.Now(),
			)
			argIdx += 11
		}

		query := fmt.Sprintf(`
			INSERT INTO installed_packages
				(id, employee_id, package_name, version, category, source_manager,
				 install_path, publisher, description, detected_at, synced_at)
			VALUES %s
			ON CONFLICT (id) DO NOTHING
		`, strings.Join(valueStrings, ", "))

		tag, err := r.pool.Exec(ctx, query, args...)
		if err != nil {
			return inserted, fmt.Errorf("bulk insert installed_packages: %w", err)
		}
		inserted += int(tag.RowsAffected())
	}
	return inserted, nil
}

// ────────────────────────────────
// Network Info
// ────────────────────────────────

func (r *NewSchemaRepo) BulkInsertNetworkInfo(ctx context.Context, entries []models.NetworkInfo) (int, error) {
	if len(entries) == 0 {
		return 0, nil
	}
	batchSize := 500
	inserted := 0
	for i := 0; i < len(entries); i += batchSize {
		end := i + batchSize
		if end > len(entries) {
			end = len(entries)
		}
		batch := entries[i:end]
		valueStrings := make([]string, 0, len(batch))
		args := make([]interface{}, 0, len(batch)*7)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4, argIdx+5, argIdx+6, argIdx+7,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.PublicIP, e.PrivateIP, e.MacAddress, e.NetworkInterfaceName, e.CollectedAt, time.Now(),
			)
			argIdx += 8
		}

		query := fmt.Sprintf(`
			INSERT INTO network_info
				(id, employee_id, public_ip, private_ip, mac_address, network_interface_name, collected_at, synced_at)
			VALUES %s
			ON CONFLICT (id) DO NOTHING
		`, strings.Join(valueStrings, ", "))

		tag, err := r.pool.Exec(ctx, query, args...)
		if err != nil {
			return inserted, fmt.Errorf("bulk insert network_info: %w", err)
		}
		inserted += int(tag.RowsAffected())
	}
	return inserted, nil
}

// ────────────────────────────────
// Session Events
// ────────────────────────────────

func (r *NewSchemaRepo) BulkInsertSessionEvents(ctx context.Context, entries []models.SessionEvent) (int, error) {
	if len(entries) == 0 {
		return 0, nil
	}
	batchSize := 500
	inserted := 0
	for i := 0; i < len(entries); i += batchSize {
		end := i + batchSize
		if end > len(entries) {
			end = len(entries)
		}
		batch := entries[i:end]
		valueStrings := make([]string, 0, len(batch))
		args := make([]interface{}, 0, len(batch)*5)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4, argIdx+5,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.EventType, e.OsUsername, e.EventAt, time.Now(),
			)
			argIdx += 6
		}

		query := fmt.Sprintf(`
			INSERT INTO session_events
				(id, employee_id, event_type, os_username, event_at, synced_at)
			VALUES %s
			ON CONFLICT (id) DO NOTHING
		`, strings.Join(valueStrings, ", "))

		tag, err := r.pool.Exec(ctx, query, args...)
		if err != nil {
			return inserted, fmt.Errorf("bulk insert session_events: %w", err)
		}
		inserted += int(tag.RowsAffected())
	}
	return inserted, nil
}

// ────────────────────────────────
// App Sessions
// ────────────────────────────────

func (r *NewSchemaRepo) BulkInsertAppSessions(ctx context.Context, entries []models.AppSession) (int, error) {
	if len(entries) == 0 {
		return 0, nil
	}
	batchSize := 500
	inserted := 0
	for i := 0; i < len(entries); i += batchSize {
		end := i + batchSize
		if end > len(entries) {
			end = len(entries)
		}
		batch := entries[i:end]
		valueStrings := make([]string, 0, len(batch))
		args := make([]interface{}, 0, len(batch)*19)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4,
				argIdx+5, argIdx+6, argIdx+7, argIdx+8, argIdx+9,
				argIdx+10, argIdx+11, argIdx+12, argIdx+13, argIdx+14,
				argIdx+15, argIdx+16, argIdx+17, argIdx+18,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.ProcessName, e.AppDisplayName, e.StartedAt,
				e.EndedAt, e.MachineID, e.SessionID, e.Platform, e.ProcessID, e.ParentProcessID,
				e.InstalledAppID, e.InstalledPackageID, e.GroupedBy, e.CgroupScope, e.ContextLabel,
				e.ForegroundSeconds, e.BackgroundSeconds,
				time.Now(),
			)
			argIdx += 19
		}

		query := fmt.Sprintf(`
			INSERT INTO app_sessions
				(id, employee_id, process_name, app_display_name, started_at,
				 ended_at, machine_id, session_id, platform, process_id, parent_process_id,
				 installed_app_id, installed_package_id, grouped_by, cgroup_scope, context_label,
				 foreground_seconds, background_seconds,
				 synced_at)
			VALUES %s
			ON CONFLICT (id) DO UPDATE SET
				ended_at = COALESCE(EXCLUDED.ended_at, app_sessions.ended_at),
				parent_process_id = COALESCE(EXCLUDED.parent_process_id, app_sessions.parent_process_id),
				installed_app_id = COALESCE(EXCLUDED.installed_app_id, app_sessions.installed_app_id),
				installed_package_id = COALESCE(EXCLUDED.installed_package_id, app_sessions.installed_package_id),
				grouped_by = COALESCE(EXCLUDED.grouped_by, app_sessions.grouped_by),
				cgroup_scope = COALESCE(EXCLUDED.cgroup_scope, app_sessions.cgroup_scope),
				context_label = COALESCE(EXCLUDED.context_label, app_sessions.context_label),
				foreground_seconds = EXCLUDED.foreground_seconds,
				background_seconds = EXCLUDED.background_seconds,
				synced_at = EXCLUDED.synced_at
		`, strings.Join(valueStrings, ", "))

		tag, err := r.pool.Exec(ctx, query, args...)
		if err != nil {
			return inserted, fmt.Errorf("bulk insert app_sessions: %w", err)
		}
		inserted += int(tag.RowsAffected())
	}
	return inserted, nil
}

// ────────────────────────────────
// App Items (generic child of app_sessions)
// ────────────────────────────────

func (r *NewSchemaRepo) BulkInsertAppItems(ctx context.Context, entries []models.AppItem) (int, error) {
	if len(entries) == 0 {
		return 0, nil
	}
	batchSize := 500
	inserted := 0
	for i := 0; i < len(entries); i += batchSize {
		end := i + batchSize
		if end > len(entries) {
			end = len(entries)
		}
		batch := entries[i:end]
		valueStrings := make([]string, 0, len(batch))
		args := make([]interface{}, 0, len(batch)*21)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4,
				argIdx+5, argIdx+6, argIdx+7, argIdx+8, argIdx+9,
				argIdx+10, argIdx+11, argIdx+12, argIdx+13, argIdx+14,
				argIdx+15, argIdx+16, argIdx+17, argIdx+18, argIdx+19,
				argIdx+20,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.AppSessionID, e.ParentItemID, e.ItemType,
				e.Title, e.Identifier, e.Url, e.Domain, e.OpenedAt, e.ClosedAt,
				e.ProcessID, e.ObjectType, e.Action, e.JourneyID, e.Sequence,
				e.PreviousPath, e.CurrentPath, e.WindowID, e.TabID, e.MetadataJSON,
			)
			argIdx += 21
		}

		query := fmt.Sprintf(`
			INSERT INTO app_items
				(id, employee_id, app_session_id, parent_item_id, item_type,
				 title, identifier, url, domain, opened_at, closed_at,
				 process_id, object_type, action, journey_id, sequence,
				 previous_path, current_path, window_id, tab_id, metadata_json)
			VALUES %s
			ON CONFLICT (id) DO UPDATE SET
				title = EXCLUDED.title,
				identifier = EXCLUDED.identifier,
				url = COALESCE(NULLIF(EXCLUDED.url, ''), app_items.url),
				domain = COALESCE(NULLIF(EXCLUDED.domain, ''), app_items.domain),
				parent_item_id = COALESCE(EXCLUDED.parent_item_id, app_items.parent_item_id),
				closed_at = COALESCE(EXCLUDED.closed_at, app_items.closed_at),
				process_id = COALESCE(EXCLUDED.process_id, app_items.process_id),
				object_type = COALESCE(NULLIF(EXCLUDED.object_type, ''), app_items.object_type),
				action = COALESCE(NULLIF(EXCLUDED.action, ''), app_items.action),
				journey_id = COALESCE(NULLIF(EXCLUDED.journey_id, ''), app_items.journey_id),
				sequence = COALESCE(EXCLUDED.sequence, app_items.sequence),
				previous_path = COALESCE(NULLIF(EXCLUDED.previous_path, ''), app_items.previous_path),
				current_path = COALESCE(NULLIF(EXCLUDED.current_path, ''), app_items.current_path),
				window_id = COALESCE(EXCLUDED.window_id, app_items.window_id),
				tab_id = COALESCE(EXCLUDED.tab_id, app_items.tab_id),
				metadata_json = EXCLUDED.metadata_json,
				synced_at = NOW()
		`, strings.Join(valueStrings, ", "))

		tag, err := r.pool.Exec(ctx, query, args...)
		if err != nil {
			return inserted, fmt.Errorf("bulk insert app_items: %w", err)
		}
		inserted += int(tag.RowsAffected())
	}
	return inserted, nil
}

// ────────────────────────────────
// LIST / QUERY
// ────────────────────────────────

type AppSessionListParams struct {
	EmployeeID string
	Search     string
	Platform   string
	DateFrom   time.Time
	DateTo     time.Time
	Page       int
	PerPage    int
}

type AppSessionListResult struct {
	Sessions   []models.AppSession
	Total      int
	Page       int
	PerPage    int
	TotalPages int
}

func (r *NewSchemaRepo) ListAppSessions(ctx context.Context, params AppSessionListParams) (*AppSessionListResult, error) {
	if params.Page < 1 {
		params.Page = 1
	}
	if params.PerPage < 1 || params.PerPage > 100 {
		params.PerPage = 20
	}

	var conditions []string
	var args []interface{}
	argIdx := 1

	conditions = append(conditions, "deleted_at IS NULL")

	if params.EmployeeID != "" {
		conditions = append(conditions, fmt.Sprintf("employee_id = $%d", argIdx))
		args = append(args, params.EmployeeID)
		argIdx++
	}
	if params.Search != "" {
		conditions = append(conditions, fmt.Sprintf(
			"(LOWER(process_name) LIKE LOWER($%d) OR LOWER(app_display_name) LIKE LOWER($%d))",
			argIdx, argIdx))
		args = append(args, "%"+params.Search+"%")
		argIdx++
	}
	if params.Platform != "" {
		conditions = append(conditions, fmt.Sprintf("platform = $%d", argIdx))
		args = append(args, params.Platform)
		argIdx++
	}
	if !params.DateFrom.IsZero() {
		conditions = append(conditions, fmt.Sprintf("started_at >= $%d", argIdx))
		args = append(args, params.DateFrom)
		argIdx++
	}
	if !params.DateTo.IsZero() {
		conditions = append(conditions, fmt.Sprintf("started_at <= $%d", argIdx))
		args = append(args, params.DateTo)
		argIdx++
	}

	whereClause := ""
	if len(conditions) > 0 {
		whereClause = "WHERE " + strings.Join(conditions, " AND ")
	}

	countQuery := fmt.Sprintf("SELECT COUNT(*) FROM app_sessions %s", whereClause)
	var total int
	if err := r.pool.QueryRow(ctx, countQuery, args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count app_sessions: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	totalPages := (total + params.PerPage - 1) / params.PerPage

	query := fmt.Sprintf(`
		SELECT id, employee_id, process_name, app_display_name, started_at, ended_at,
		       machine_id, session_id, platform, process_id, parent_process_id,
		       installed_app_id, installed_package_id, grouped_by, cgroup_scope, context_label,
		       foreground_seconds, background_seconds, synced_at, created_at
		FROM app_sessions %s
		ORDER BY started_at DESC
		LIMIT $%d OFFSET $%d
	`, whereClause, argIdx, argIdx+1)
	args = append(args, params.PerPage, offset)

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list app_sessions: %w", err)
	}
	defer rows.Close()

	var sessions []models.AppSession
	for rows.Next() {
		var s models.AppSession
		if err := rows.Scan(
			&s.ID, &s.EmployeeID, &s.ProcessName, &s.AppDisplayName, &s.StartedAt, &s.EndedAt,
			&s.MachineID, &s.SessionID, &s.Platform, &s.ProcessID, &s.ParentProcessID,
			&s.InstalledAppID, &s.InstalledPackageID, &s.GroupedBy, &s.CgroupScope, &s.ContextLabel,
			&s.ForegroundSeconds, &s.BackgroundSeconds, &s.SyncedAt, &s.CreatedAt,
		); err != nil {
			return nil, fmt.Errorf("scan app_session row: %w", err)
		}
		sessions = append(sessions, s)
	}

	return &AppSessionListResult{
		Sessions:   sessions,
		Total:      total,
		Page:       params.Page,
		PerPage:    params.PerPage,
		TotalPages: totalPages,
	}, nil
}

// ────────────────────────────────
// App Items (list for web dashboard)
// ────────────────────────────────

type AppItemListParams struct {
	EmployeeID   string
	AppSessionID string
	ItemType     string
	Search       string
	DateFrom     time.Time
	DateTo       time.Time
	Page         int
	PerPage      int
}

type AppItemListResult struct {
	Items      []models.AppItem
	Total      int
	Page       int
	PerPage    int
	TotalPages int
}

func (r *NewSchemaRepo) ListAppItems(ctx context.Context, params AppItemListParams) (*AppItemListResult, error) {
	if params.Page < 1 {
		params.Page = 1
	}
	if params.PerPage < 1 || params.PerPage > 100 {
		params.PerPage = 20
	}

	var conditions []string
	var args []interface{}
	argIdx := 1

	conditions = append(conditions, "deleted_at IS NULL")

	if params.EmployeeID != "" {
		conditions = append(conditions, fmt.Sprintf("employee_id = $%d", argIdx))
		args = append(args, params.EmployeeID)
		argIdx++
	}
	if params.AppSessionID != "" {
		conditions = append(conditions, fmt.Sprintf("app_session_id = $%d", argIdx))
		args = append(args, params.AppSessionID)
		argIdx++
	}
	if params.ItemType != "" {
		conditions = append(conditions, fmt.Sprintf("item_type = $%d", argIdx))
		args = append(args, params.ItemType)
		argIdx++
	}
	if params.Search != "" {
		conditions = append(conditions, fmt.Sprintf(
			"(LOWER(title) LIKE LOWER($%d) OR LOWER(identifier) LIKE LOWER($%d))",
			argIdx, argIdx))
		args = append(args, "%"+params.Search+"%")
		argIdx++
	}
	if !params.DateFrom.IsZero() {
		conditions = append(conditions, fmt.Sprintf("opened_at >= $%d", argIdx))
		args = append(args, params.DateFrom)
		argIdx++
	}
	if !params.DateTo.IsZero() {
		conditions = append(conditions, fmt.Sprintf("opened_at <= $%d", argIdx))
		args = append(args, params.DateTo)
		argIdx++
	}

	whereClause := ""
	if len(conditions) > 0 {
		whereClause = "WHERE " + strings.Join(conditions, " AND ")
	}

	countQuery := fmt.Sprintf("SELECT COUNT(*) FROM app_items %s", whereClause)
	var total int
	if err := r.pool.QueryRow(ctx, countQuery, args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count app_items: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	totalPages := (total + params.PerPage - 1) / params.PerPage

	query := fmt.Sprintf(`
		SELECT id, employee_id, app_session_id, parent_item_id, item_type,
		       title, identifier, url, domain, opened_at, closed_at,
		       process_id, object_type, action, journey_id, sequence,
		       previous_path, current_path, window_id, tab_id, metadata_json,
		       synced_at, created_at
		FROM app_items %s
		ORDER BY opened_at DESC
		LIMIT $%d OFFSET $%d
	`, whereClause, argIdx, argIdx+1)
	args = append(args, params.PerPage, offset)

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list app_items: %w", err)
	}
	defer rows.Close()

	var items []models.AppItem
	for rows.Next() {
		var item models.AppItem
		if err := rows.Scan(
			&item.ID, &item.EmployeeID, &item.AppSessionID, &item.ParentItemID, &item.ItemType,
			&item.Title, &item.Identifier, &item.Url, &item.Domain,
			&item.OpenedAt, &item.ClosedAt,
			&item.ProcessID, &item.ObjectType, &item.Action, &item.JourneyID, &item.Sequence,
			&item.PreviousPath, &item.CurrentPath, &item.WindowID, &item.TabID, &item.MetadataJSON,
			&item.SyncedAt, &item.CreatedAt,
		); err != nil {
			return nil, fmt.Errorf("scan app_item row: %w", err)
		}
		items = append(items, item)
	}

	return &AppItemListResult{
		Items:      items,
		Total:      total,
		Page:       params.Page,
		PerPage:    params.PerPage,
		TotalPages: totalPages,
	}, nil
}

// ────────────────────────────────
// CATALOG + EMPLOYEE LINK UPSERTS
// (employee↔app / employee↔package catalog dedup)
// ────────────────────────────────

// UpsertApplicationCatalog inserts or updates the deduplicated app catalog row keyed by
// app_fingerprint and returns its id.
func (r *NewSchemaRepo) UpsertApplicationCatalog(ctx context.Context, tx pgx.Tx, e models.InstalledApplication) (string, error) {
	var id string
	err := tx.QueryRow(ctx, `
		INSERT INTO installed_applications
			(id, employee_id, app_name, app_version, publisher, install_path,
			 install_date, uninstall_string, change_type, detected_at, synced_at,
			 binary_name, is_browser, desktop_id, categories, app_fingerprint)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16)
		ON CONFLICT (app_fingerprint) DO UPDATE SET
			app_name = EXCLUDED.app_name,
			binary_name = COALESCE(NULLIF(EXCLUDED.binary_name, ''), installed_applications.binary_name),
			is_browser = installed_applications.is_browser OR EXCLUDED.is_browser,
			desktop_id = COALESCE(NULLIF(EXCLUDED.desktop_id, ''), installed_applications.desktop_id),
			categories = COALESCE(NULLIF(EXCLUDED.categories, ''), installed_applications.categories),
			detected_at = EXCLUDED.detected_at,
			synced_at = EXCLUDED.synced_at
		RETURNING id
	`,
		e.ID, e.EmployeeID, e.AppName, e.AppVersion, e.Publisher, e.InstallPath,
		e.InstallDate, e.UninstallString, e.ChangeType, e.DetectedAt, e.SyncedAt,
		e.BinaryName, e.IsBrowser, e.DesktopID, e.Categories, e.AppFingerprint,
	).Scan(&id)
	if err != nil {
		return "", fmt.Errorf("upsert application catalog: %w", err)
	}
	return id, nil
}

// UpsertPackageCatalog inserts or updates the deduplicated package catalog row keyed by
// package_fingerprint and returns its id.
func (r *NewSchemaRepo) UpsertPackageCatalog(ctx context.Context, tx pgx.Tx, e models.InstalledPackage) (string, error) {
	var id string
	err := tx.QueryRow(ctx, `
		INSERT INTO installed_packages
			(id, employee_id, package_name, version, category, source_manager,
			 install_path, publisher, description, detected_at, synced_at, package_fingerprint)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
		ON CONFLICT (package_fingerprint) DO UPDATE SET
			package_name = EXCLUDED.package_name,
			category = COALESCE(NULLIF(EXCLUDED.category, ''), installed_packages.category),
			description = COALESCE(NULLIF(EXCLUDED.description, ''), installed_packages.description),
			detected_at = EXCLUDED.detected_at,
			synced_at = EXCLUDED.synced_at
		RETURNING id
	`,
		e.ID, e.EmployeeID, e.PackageName, e.Version, e.Category, e.SourceManager,
		e.InstallPath, e.Publisher, e.Description, e.DetectedAt, e.SyncedAt, e.PackageFingerprint,
	).Scan(&id)
	if err != nil {
		return "", fmt.Errorf("upsert package catalog: %w", err)
	}
	return id, nil
}

// UpsertEmployeeAppLink links an employee to a catalog app, refreshing per-install metadata.
func (r *NewSchemaRepo) UpsertEmployeeAppLink(ctx context.Context, tx pgx.Tx, link models.EmployeeInstalledApplication) error {
	_, err := tx.Exec(ctx, `
		INSERT INTO employee_installed_applications
			(employee_id, installed_application_id, app_version, publisher, install_path, install_date,
			 first_seen_at, last_seen_at, is_active)
		VALUES ($1, $2, $3, $4, $5, $6, now(), now(), true)
		ON CONFLICT (employee_id, installed_application_id) DO UPDATE SET
			app_version = EXCLUDED.app_version,
			publisher = COALESCE(NULLIF(EXCLUDED.publisher, ''), employee_installed_applications.publisher),
			install_path = COALESCE(NULLIF(EXCLUDED.install_path, ''), employee_installed_applications.install_path),
			install_date = COALESCE(EXCLUDED.install_date, employee_installed_applications.install_date),
			last_seen_at = now(),
			is_active = true
	`,
		link.EmployeeID, link.InstalledApplicationID, link.AppVersion, link.Publisher, link.InstallPath, link.InstallDate,
	)
	if err != nil {
		return fmt.Errorf("upsert employee app link: %w", err)
	}
	return nil
}

// UpsertEmployeePackageLink links an employee to a catalog package, refreshing per-install metadata.
func (r *NewSchemaRepo) UpsertEmployeePackageLink(ctx context.Context, tx pgx.Tx, link models.EmployeeInstalledPackage) error {
	_, err := tx.Exec(ctx, `
		INSERT INTO employee_installed_packages
			(employee_id, installed_package_id, version, publisher, install_path,
			 first_seen_at, last_seen_at, is_active)
		VALUES ($1, $2, $3, $4, $5, now(), now(), true)
		ON CONFLICT (employee_id, installed_package_id) DO UPDATE SET
			version = EXCLUDED.version,
			publisher = COALESCE(NULLIF(EXCLUDED.publisher, ''), employee_installed_packages.publisher),
			install_path = COALESCE(NULLIF(EXCLUDED.install_path, ''), employee_installed_packages.install_path),
			last_seen_at = now(),
			is_active = true
	`,
		link.EmployeeID, link.InstalledPackageID, link.Version, link.Publisher, link.InstallPath,
	)
	if err != nil {
		return fmt.Errorf("upsert employee package link: %w", err)
	}
	return nil
}

// ────────────────────────────────
// App Status (key/value status rows; natural key employee_id+key)
// ────────────────────────────────

func (r *NewSchemaRepo) UpsertAppStatus(ctx context.Context, tx pgx.Tx, e models.AppStatus) error {
	_, err := tx.Exec(ctx, `
		INSERT INTO app_status (employee_id, key, value, updated_at)
		VALUES ($1, $2, $3, $4)
		ON CONFLICT (employee_id, key) DO UPDATE SET
			value = EXCLUDED.value,
			updated_at = EXCLUDED.updated_at
	`, e.EmployeeID, e.Key, e.Value, e.UpdatedAt)
	if err != nil {
		return fmt.Errorf("upsert app_status: %w", err)
	}
	return nil
}

// ────────────────────────────────
// Hardware Devices (USB / peripheral hotplug)
// ────────────────────────────────

func (r *NewSchemaRepo) BulkUpsertHardwareDevices(ctx context.Context, entries []models.HardwareDevice) (int, error) {
	if len(entries) == 0 {
		return 0, nil
	}
	batchSize := 500
	inserted := 0
	for i := 0; i < len(entries); i += batchSize {
		end := i + batchSize
		if end > len(entries) {
			end = len(entries)
		}
		batch := entries[i:end]
		valueStrings := make([]string, 0, len(batch))
		args := make([]interface{}, 0, len(batch)*10)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4,
				argIdx+5, argIdx+6, argIdx+7, argIdx+8, argIdx+9,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.DeviceClass, e.Vendor, e.Product,
				e.Serial, e.BusPath, e.DeviceNode, e.PluggedAt, e.UnpluggedAt,
			)
			argIdx += 10
		}

		query := fmt.Sprintf(`
			INSERT INTO hardware_devices
				(id, employee_id, device_class, vendor, product, serial, bus_path, device_node, plugged_at, unplugged_at)
			VALUES %s
			ON CONFLICT (id) DO UPDATE SET
				unplugged_at = COALESCE(EXCLUDED.unplugged_at, hardware_devices.unplugged_at),
				synced_at = NOW()
		`, strings.Join(valueStrings, ", "))

		tag, err := r.pool.Exec(ctx, query, args...)
		if err != nil {
			return inserted, fmt.Errorf("bulk upsert hardware_devices: %w", err)
		}
		inserted += int(tag.RowsAffected())
	}
	return inserted, nil
}

// ────────────────────────────────
// Permission Status (one row per permission method; keyed by check_id)
// ────────────────────────────────

func (r *NewSchemaRepo) BulkUpsertPermissionStatus(ctx context.Context, entries []models.PermissionStatus) (int, error) {
	if len(entries) == 0 {
		return 0, nil
	}
	batchSize := 500
	inserted := 0
	for i := 0; i < len(entries); i += batchSize {
		end := i + batchSize
		if end > len(entries) {
			end = len(entries)
		}
		batch := entries[i:end]
		valueStrings := make([]string, 0, len(batch))
		args := make([]interface{}, 0, len(batch)*9)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4,
				argIdx+5, argIdx+6, argIdx+7, argIdx+8,
			))
			args = append(args,
				e.CheckID, e.EmployeeID, e.SessionID, e.SessionType, e.Platform,
				e.CheckedAt, e.Method, e.Works, e.Details,
			)
			argIdx += 9
		}

		query := fmt.Sprintf(`
			INSERT INTO permission_status
				(check_id, employee_id, session_id, session_type, platform, checked_at, method, works, details)
			VALUES %s
			ON CONFLICT (check_id) DO UPDATE SET
				employee_id = EXCLUDED.employee_id,
				session_id = EXCLUDED.session_id,
				session_type = EXCLUDED.session_type,
				platform = EXCLUDED.platform,
				checked_at = EXCLUDED.checked_at,
				works = EXCLUDED.works,
				details = EXCLUDED.details,
				synced_at = NOW()
		`, strings.Join(valueStrings, ", "))

		tag, err := r.pool.Exec(ctx, query, args...)
		if err != nil {
			return inserted, fmt.Errorf("bulk upsert permission_status: %w", err)
		}
		inserted += int(tag.RowsAffected())
	}
	return inserted, nil
}

// ────────────────────────────────
// Storage Devices (children of device_hardware_info)
// ────────────────────────────────

func (r *NewSchemaRepo) BulkUpsertStorageDevices(ctx context.Context, entries []models.StorageDevice) (int, error) {
	if len(entries) == 0 {
		return 0, nil
	}
	batchSize := 500
	inserted := 0
	for i := 0; i < len(entries); i += batchSize {
		end := i + batchSize
		if end > len(entries) {
			end = len(entries)
		}
		batch := entries[i:end]
		valueStrings := make([]string, 0, len(batch))
		args := make([]interface{}, 0, len(batch)*6)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4, argIdx+5,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.DeviceHardwareID, e.DeviceType, e.Model, e.CapacityMB,
			)
			argIdx += 6
		}

		query := fmt.Sprintf(`
			INSERT INTO storage_devices
				(id, employee_id, device_hardware_id, device_type, model, capacity_mb)
			VALUES %s
			ON CONFLICT (id) DO NOTHING
		`, strings.Join(valueStrings, ", "))

		tag, err := r.pool.Exec(ctx, query, args...)
		if err != nil {
			return inserted, fmt.Errorf("bulk upsert storage_devices: %w", err)
		}
		inserted += int(tag.RowsAffected())
	}
	return inserted, nil
}

// ────────────────────────────────
// EMPLOYEE DETAIL READ VIEWS
// (web dashboard — GET /employees/:id/detail)
// ────────────────────────────────

// GetLatestDeviceHardware returns the most recent device_hardware_info snapshot for an employee.
func (r *NewSchemaRepo) GetLatestDeviceHardware(ctx context.Context, employeeID string) (*models.DeviceHardwareInfo, error) {
	query := `
		SELECT id, employee_id, COALESCE(device_id, '') AS device_id, COALESCE(mac_address, '') AS mac_address,
		       COALESCE(hostname, '') AS hostname, COALESCE(os_name, '') AS os_name, COALESCE(os_version, '') AS os_version,
		       COALESCE(cpu_model, '') AS cpu_model, cpu_cores, ram_total_mb,
		       COALESCE(storage_devices::TEXT, '[]') AS storage_devices,
		       COALESCE(gpu_model, '') AS gpu_model, gpu_vram_mb, collected_at, synced_at, created_at
		FROM device_hardware_info
		WHERE employee_id = $1 AND deleted_at IS NULL
		ORDER BY collected_at DESC
		LIMIT 1
	`
	var hw models.DeviceHardwareInfo
	err := r.pool.QueryRow(ctx, query, employeeID).Scan(
		&hw.ID, &hw.EmployeeID, &hw.DeviceID, &hw.MacAddress, &hw.Hostname, &hw.OsName, &hw.OsVersion,
		&hw.CpuModel, &hw.CpuCores, &hw.RamTotalMB, &hw.StorageDevices, &hw.GpuModel, &hw.GpuVramMB,
		&hw.CollectedAt, &hw.SyncedAt, &hw.CreatedAt,
	)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, nil
		}
		return nil, fmt.Errorf("get latest device_hardware_info: %w", err)
	}
	return &hw, nil
}

// ListStorageDevices returns the storage_devices rows for an employee.
func (r *NewSchemaRepo) ListStorageDevices(ctx context.Context, employeeID string) ([]models.StorageDevice, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, employee_id, device_hardware_id, COALESCE(device_type, '') AS device_type,
		       COALESCE(model, '') AS model, capacity_mb, synced_at, created_at
		FROM storage_devices
		WHERE employee_id = $1 AND deleted_at IS NULL
		ORDER BY capacity_mb DESC
	`, employeeID)
	if err != nil {
		return nil, fmt.Errorf("list storage_devices: %w", err)
	}
	defer rows.Close()

	var devices []models.StorageDevice
	for rows.Next() {
		var d models.StorageDevice
		if err := rows.Scan(
			&d.ID, &d.EmployeeID, &d.DeviceHardwareID, &d.DeviceType, &d.Model, &d.CapacityMB,
			&d.SyncedAt, &d.CreatedAt,
		); err != nil {
			return nil, fmt.Errorf("scan storage_device row: %w", err)
		}
		devices = append(devices, d)
	}
	return devices, rows.Err()
}

// GetLatestNetworkInfo returns the most recent network_info snapshot for an employee.
func (r *NewSchemaRepo) GetLatestNetworkInfo(ctx context.Context, employeeID string) (*models.NetworkInfo, error) {
	query := `
		SELECT id, employee_id, COALESCE(public_ip, '') AS public_ip, COALESCE(private_ip, '') AS private_ip,
		       COALESCE(mac_address, '') AS mac_address, COALESCE(network_interface_name, '') AS network_interface_name,
		       collected_at, synced_at, created_at
		FROM network_info
		WHERE employee_id = $1 AND deleted_at IS NULL
		ORDER BY collected_at DESC
		LIMIT 1
	`
	var n models.NetworkInfo
	err := r.pool.QueryRow(ctx, query, employeeID).Scan(
		&n.ID, &n.EmployeeID, &n.PublicIP, &n.PrivateIP, &n.MacAddress, &n.NetworkInterfaceName,
		&n.CollectedAt, &n.SyncedAt, &n.CreatedAt,
	)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, nil
		}
		return nil, fmt.Errorf("get latest network_info: %w", err)
	}
	return &n, nil
}

// ListEmployeeApplications returns the employee's currently-installed applications
// (catalog row joined with the per-employee junction row, active links only).
func (r *NewSchemaRepo) ListEmployeeApplications(ctx context.Context, employeeID string) ([]models.EmployeeApplicationDetail, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT ia.id, ia.app_name, COALESCE(ia.binary_name, '') AS binary_name,
		       COALESCE(eia.app_version, '') AS app_version,
		       COALESCE(eia.publisher, '') AS publisher,
		       COALESCE(eia.install_path, '') AS install_path,
		       eia.install_date, ia.is_browser, COALESCE(ia.categories, '') AS categories,
		       COALESCE(ia.desktop_id, '') AS desktop_id, eia.first_seen_at, eia.last_seen_at
		FROM employee_installed_applications eia
		JOIN installed_applications ia ON ia.id = eia.installed_application_id
		WHERE eia.employee_id = $1 AND eia.is_active = true AND ia.deleted_at IS NULL
		ORDER BY ia.app_name ASC
	`, employeeID)
	if err != nil {
		return nil, fmt.Errorf("list employee applications: %w", err)
	}
	defer rows.Close()

	var apps []models.EmployeeApplicationDetail
	for rows.Next() {
		var a models.EmployeeApplicationDetail
		if err := rows.Scan(
			&a.ID, &a.AppName, &a.BinaryName, &a.Version, &a.Publisher, &a.InstallPath,
			&a.InstallDate, &a.IsBrowser, &a.Categories, &a.DesktopID, &a.FirstSeenAt, &a.LastSeenAt,
		); err != nil {
			return nil, fmt.Errorf("scan employee application row: %w", err)
		}
		apps = append(apps, a)
	}
	return apps, rows.Err()
}

// ListEmployeePackages returns the employee's currently-installed packages
// (catalog row joined with the per-employee junction row, active links only).
func (r *NewSchemaRepo) ListEmployeePackages(ctx context.Context, employeeID string) ([]models.EmployeePackageDetail, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT ip.id, ip.package_name, COALESCE(eip.version, '') AS version,
		       COALESCE(ip.category, '') AS category, COALESCE(ip.source_manager, '') AS source_manager,
		       COALESCE(eip.install_path, '') AS install_path,
		       COALESCE(eip.publisher, '') AS publisher, COALESCE(ip.description, '') AS description,
		       eip.first_seen_at, eip.last_seen_at
		FROM employee_installed_packages eip
		JOIN installed_packages ip ON ip.id = eip.installed_package_id
		WHERE eip.employee_id = $1 AND eip.is_active = true AND ip.deleted_at IS NULL
		ORDER BY ip.package_name ASC
	`, employeeID)
	if err != nil {
		return nil, fmt.Errorf("list employee packages: %w", err)
	}
	defer rows.Close()

	var pkgs []models.EmployeePackageDetail
	for rows.Next() {
		var p models.EmployeePackageDetail
		if err := rows.Scan(
			&p.ID, &p.PackageName, &p.Version, &p.Category, &p.SourceManager,
			&p.InstallPath, &p.Publisher, &p.Description, &p.FirstSeenAt, &p.LastSeenAt,
		); err != nil {
			return nil, fmt.Errorf("scan employee package row: %w", err)
		}
		pkgs = append(pkgs, p)
	}
	return pkgs, rows.Err()
}

// ListHardwareDevices returns the peripheral (USB hotplug) history for an employee.
func (r *NewSchemaRepo) ListHardwareDevices(ctx context.Context, employeeID string) ([]models.HardwareDevice, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, employee_id, COALESCE(device_class, '') AS device_class, COALESCE(vendor, '') AS vendor,
		       COALESCE(product, '') AS product, COALESCE(serial, '') AS serial, COALESCE(bus_path, '') AS bus_path,
		       COALESCE(device_node, '') AS device_node, plugged_at, unplugged_at, synced_at, created_at
		FROM hardware_devices
		WHERE employee_id = $1 AND deleted_at IS NULL
		ORDER BY plugged_at DESC
	`, employeeID)
	if err != nil {
		return nil, fmt.Errorf("list hardware_devices: %w", err)
	}
	defer rows.Close()

	var devices []models.HardwareDevice
	for rows.Next() {
		var d models.HardwareDevice
		if err := rows.Scan(
			&d.ID, &d.EmployeeID, &d.DeviceClass, &d.Vendor, &d.Product, &d.Serial, &d.BusPath,
			&d.DeviceNode, &d.PluggedAt, &d.UnpluggedAt, &d.SyncedAt, &d.CreatedAt,
		); err != nil {
			return nil, fmt.Errorf("scan hardware_device row: %w", err)
		}
		devices = append(devices, d)
	}
	return devices, rows.Err()
}

// ListAppStatus returns the key/value status rows for an employee (heartbeat, etc.).
// Note: app_status has no deleted_at column (ephemeral key/value store).
func (r *NewSchemaRepo) ListAppStatus(ctx context.Context, employeeID string) ([]models.AppStatus, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT employee_id, key, value, updated_at, created_at
		FROM app_status
		WHERE employee_id = $1
		ORDER BY key ASC
	`, employeeID)
	if err != nil {
		return nil, fmt.Errorf("list app_status: %w", err)
	}
	defer rows.Close()

	var statuses []models.AppStatus
	for rows.Next() {
		var s models.AppStatus
		if err := rows.Scan(&s.EmployeeID, &s.Key, &s.Value, &s.UpdatedAt, &s.CreatedAt); err != nil {
			return nil, fmt.Errorf("scan app_status row: %w", err)
		}
		statuses = append(statuses, s)
	}
	return statuses, rows.Err()
}

// ListPermissionStatus returns the permission-method check rows for an employee.
func (r *NewSchemaRepo) ListPermissionStatus(ctx context.Context, employeeID string) ([]models.PermissionStatus, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT check_id, employee_id, COALESCE(session_id, '') AS session_id, COALESCE(session_type, '') AS session_type,
		       COALESCE(platform, '') AS platform, checked_at, COALESCE(method, '') AS method, works, COALESCE(details, '') AS details,
		       synced_at, created_at
		FROM permission_status
		WHERE employee_id = $1 AND deleted_at IS NULL
		ORDER BY checked_at DESC
	`, employeeID)
	if err != nil {
		return nil, fmt.Errorf("list permission_status: %w", err)
	}
	defer rows.Close()

	var perms []models.PermissionStatus
	for rows.Next() {
		var p models.PermissionStatus
		if err := rows.Scan(
			&p.CheckID, &p.EmployeeID, &p.SessionID, &p.SessionType, &p.Platform,
			&p.CheckedAt, &p.Method, &p.Works, &p.Details, &p.SyncedAt, &p.CreatedAt,
		); err != nil {
			return nil, fmt.Errorf("scan permission_status row: %w", err)
		}
		perms = append(perms, p)
	}
	return perms, rows.Err()
}

// GetEmployeeActivityStats returns derived counts over app_sessions / app_items.
func (r *NewSchemaRepo) GetEmployeeActivityStats(ctx context.Context, employeeID string) (*models.ActivityStats, error) {
	var s models.ActivityStats
	err := r.pool.QueryRow(ctx, `
		SELECT
			(SELECT COUNT(*) FROM app_sessions WHERE employee_id = $1 AND deleted_at IS NULL),
			(SELECT COUNT(*) FROM app_sessions WHERE employee_id = $1 AND deleted_at IS NULL AND ended_at IS NULL),
			(SELECT COUNT(*) FROM app_items WHERE employee_id = $1 AND deleted_at IS NULL),
			(SELECT MAX(started_at) FROM app_sessions WHERE employee_id = $1 AND deleted_at IS NULL)
	`, employeeID).Scan(&s.TotalSessions, &s.OpenSessions, &s.TotalItems, &s.LastActivityAt)
	if err != nil {
		return nil, fmt.Errorf("get employee activity stats: %w", err)
	}
	return &s, nil
}
