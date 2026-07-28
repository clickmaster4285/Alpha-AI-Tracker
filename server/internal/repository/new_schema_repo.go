package repository

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/alpha-ai-tracker/server/internal/models"
)

type NewSchemaRepo struct {
	pool *pgxpool.Pool
}

func NewNewSchemaRepo(pool *pgxpool.Pool) *NewSchemaRepo {
	return &NewSchemaRepo{pool: pool}
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
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4,
				argIdx+5, argIdx+6, argIdx+7, argIdx+8, argIdx+9, argIdx+10,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.AppName, e.AppVersion, e.Publisher,
				e.InstallPath, e.InstallDate, e.UninstallString, e.ChangeType, e.DetectedAt, time.Now(),
			)
			argIdx += 11
		}

		query := fmt.Sprintf(`
			INSERT INTO installed_applications
				(id, employee_id, app_name, app_version, publisher, install_path,
				 install_date, uninstall_string, change_type, detected_at, synced_at)
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
		args := make([]interface{}, 0, len(batch)*11)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4,
				argIdx+5, argIdx+6, argIdx+7, argIdx+8, argIdx+9, argIdx+10, argIdx+11,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.ProcessName, e.AppDisplayName, e.StartedAt,
				e.EndedAt, e.MachineID, e.SessionID, e.Platform, e.ProcessID, e.ParentProcessID, time.Now(),
			)
			argIdx += 12
		}

		query := fmt.Sprintf(`
			INSERT INTO app_sessions
				(id, employee_id, process_name, app_display_name, started_at,
				 ended_at, machine_id, session_id, platform, process_id, parent_process_id, synced_at)
			VALUES %s
			ON CONFLICT (id) DO UPDATE SET
				ended_at = COALESCE(EXCLUDED.ended_at, app_sessions.ended_at),
				parent_process_id = COALESCE(EXCLUDED.parent_process_id, app_sessions.parent_process_id),
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
		args := make([]interface{}, 0, len(batch)*9)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4,
				argIdx+5, argIdx+6, argIdx+7, argIdx+8, argIdx+9, argIdx+10,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.AppSessionID, e.ParentItemID, e.ItemType,
				e.Title, e.Identifier, e.Url, e.Domain, e.OpenedAt, e.ClosedAt,
			)
			argIdx += 11
		}

		query := fmt.Sprintf(`
			INSERT INTO app_items
				(id, employee_id, app_session_id, parent_item_id, item_type,
				 title, identifier, url, domain, opened_at, closed_at)
			VALUES %s
			ON CONFLICT (id) DO UPDATE SET
				title = EXCLUDED.title,
				identifier = EXCLUDED.identifier,
				url = COALESCE(NULLIF(EXCLUDED.url, ''), app_items.url),
				domain = COALESCE(NULLIF(EXCLUDED.domain, ''), app_items.domain),
				parent_item_id = COALESCE(EXCLUDED.parent_item_id, app_items.parent_item_id),
				closed_at = COALESCE(EXCLUDED.closed_at, app_items.closed_at),
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
		       machine_id, session_id, platform, process_id, parent_process_id, synced_at, created_at
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
			&s.MachineID, &s.SessionID, &s.Platform, &s.ProcessID, &s.ParentProcessID, &s.SyncedAt, &s.CreatedAt,
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
		       title, identifier, url, domain, opened_at, closed_at, synced_at, created_at
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
			&item.OpenedAt, &item.ClosedAt, &item.SyncedAt, &item.CreatedAt,
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
