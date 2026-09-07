package repository

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"math"
	"strconv"
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
		args := make([]interface{}, 0, len(batch)*9)
		argIdx := 1

		for _, e := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4,
				argIdx+5, argIdx+6, argIdx+7, argIdx+8,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.EventType, e.OsUsername, e.EventAt,
				e.EventCount, e.FirstAt, e.LastAt, time.Now(),
			)
			argIdx += 9
		}

		query := fmt.Sprintf(`
			INSERT INTO session_events
				(id, employee_id, event_type, os_username, event_at,
				 event_count, first_at, last_at, synced_at)
			VALUES %s
			ON CONFLICT (id) DO UPDATE SET
				event_type = EXCLUDED.event_type,
				os_username = EXCLUDED.os_username,
				event_at = EXCLUDED.event_at,
				event_count = EXCLUDED.event_count,
				first_at = EXCLUDED.first_at,
				last_at = EXCLUDED.last_at,
				synced_at = EXCLUDED.synced_at
			WHERE session_events.employee_id = EXCLUDED.employee_id
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
			now := time.Now()
			// Default last_activity_at to started_at when client omits it
			// (older client builds pre-2026-09-02) so the sweep has a
			// sane baseline from row 1.
			lastActivity := e.LastActivityAt
			if lastActivity == nil {
				lastActivity = &e.StartedAt
			}
			args = append(args,
				e.ID, e.EmployeeID, e.ProcessName, e.AppDisplayName, e.StartedAt,
				e.EndedAt, e.MachineID, e.SessionID, e.Platform, e.ProcessID, e.ParentProcessID,
				e.InstalledAppID, e.InstalledPackageID, e.GroupedBy, e.CgroupScope, e.ContextLabel,
				e.ForegroundSeconds, e.BackgroundSeconds,
				now,
				lastActivity,
				now, // last_sync_at = NOW() — server records the moment this row arrived
			)
			argIdx += 21
		}

		// Upsert semantics for the 4-state lifecycle (2026-09-02 + OFFLINE 2026-09-02):
		//   States: ACTIVE → OFFLINE → STALE → CLOSED.
		//     ACTIVE  = client is syncing in real time (< SESSION_STALE_AFTER_MINUTES)
		//     OFFLINE = client hasn't synced for STALE_AFTER min but < 24h (client unreachable)
		//     STALE   = sweep confirmed the gap is real (≥ STALE_AFTER min of no sync)
		//     CLOSED  = terminal (≥ CLOSE_AFTER hours of no sync, or client sent ended_at)
		//   - last_sync_at / last_activity_at always refresh from the client
		//     (the client is the live source for the activity it observed).
		//   - foreground_seconds / background_seconds still EXCLUDED-overwrite
		//     (client keeps a cumulative total).
		//   - ended_at + status: see CASE blocks below.
		query := fmt.Sprintf(`
			INSERT INTO app_sessions
				(id, employee_id, process_name, app_display_name, started_at,
				 ended_at, machine_id, session_id, platform, process_id, parent_process_id,
				 installed_app_id, installed_package_id, grouped_by, cgroup_scope, context_label,
				 foreground_seconds, background_seconds,
				 synced_at, last_activity_at, last_sync_at)
			VALUES %s
			ON CONFLICT (id) DO UPDATE SET
				parent_process_id = COALESCE(EXCLUDED.parent_process_id, app_sessions.parent_process_id),
				installed_app_id = COALESCE(EXCLUDED.installed_app_id, app_sessions.installed_app_id),
				installed_package_id = COALESCE(EXCLUDED.installed_package_id, app_sessions.installed_package_id),
				grouped_by = COALESCE(EXCLUDED.grouped_by, app_sessions.grouped_by),
				cgroup_scope = COALESCE(EXCLUDED.cgroup_scope, app_sessions.cgroup_scope),
				context_label = COALESCE(EXCLUDED.context_label, app_sessions.context_label),
				foreground_seconds = EXCLUDED.foreground_seconds,
				background_seconds = EXCLUDED.background_seconds,
				synced_at = EXCLUDED.synced_at,
				last_activity_at = EXCLUDED.last_activity_at,
				last_sync_at = EXCLUDED.last_sync_at,
				-- Client-driven status transitions.
				--   * Client says ended_at + last_sync_at just landed → CLOSED
				--     (finalizes an ACTIVE/OFFLINE/STALE row immediately, no 24h wait)
				--   * Client re-uploads an OFFLINE or STALE row with ended_at=NULL → ACTIVE
				--     (network came back, the process is still alive; the per-machine
				--     sweeper had flipped the row, the live client proves the machine
				--     is back, so resurrect).
				--   * CLOSED is TERMINAL. Once closed (by the client or by the sweeper
				--     after CLOSE_AFTER_HOURS), a re-uploaded ended_at=NULL must NOT
				--     resurrect the row -- that would override the sweeper's frozen
				--     ended_at and re-open a finalized duration. The row stays CLOSED.
				--   * Otherwise keep the existing server-side state (the sweeper
				--     manages ACTIVE→OFFLINE→STALE→CLOSED per-machine).
				status = CASE
					WHEN EXCLUDED.ended_at IS NOT NULL
						AND app_sessions.status IN ('ACTIVE','OFFLINE','STALE') THEN 'CLOSED'
					WHEN EXCLUDED.ended_at IS NULL
						AND app_sessions.status IN ('OFFLINE','STALE') THEN 'ACTIVE'
					ELSE app_sessions.status
				END,
				ended_at = CASE
					WHEN EXCLUDED.ended_at IS NOT NULL THEN EXCLUDED.ended_at
					WHEN app_sessions.status IN ('OFFLINE','STALE')
						AND EXCLUDED.ended_at IS NULL THEN NULL
					ELSE app_sessions.ended_at
				END
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

		// Orphan preflight: app_items.app_session_id has a NOT NULL FK to
		// app_sessions.id. The client sends sessions and items in separate
		// HTTP calls — a retry, a second client process, or a network blip
		// can land the items batch BEFORE the parent session row arrives,
		// which used to 500 the whole batch and silently drop every row
		// (the client marks them sent on the 2xx it would have wanted).
		// The fix: query which app_session_ids actually exist right now,
		// drop the orphans from THIS batch, and proceed. The dropped rows
		// are re-sent on the client's next sync (where the parent session
		// is already in the DB). The orphan count is logged at WARN with
		// the first 20 ids so the client-side root cause stays visible.
		//
		// Why preflight (not ON CONFLICT DO NOTHING on the FK): Postgres
		// checks FKs at row-insert time, not at ON CONFLICT time, so the
		// preflight is the only way to make the rest of the batch succeed
		// atomically without aborting the whole statement.
		//
		// Why log (not return error): the orphan rows are recoverable —
		// the client's next sync will re-send them after the parent
		// session lands. Returning an error here would make the client
		// treat the entire batch as failed and stop sending until a
		// restart, which is exactly the wrong failure mode.
		orphanIDs, survivorIdx := r.filterOrphanAppItems(ctx, batch)
		if len(orphanIDs) > 0 {
			logOrphanAppItems(entries, orphanIDs)
		}
		if len(survivorIdx) == 0 {
			// entire batch was orphans — nothing to insert, but the
			// request itself is well-formed, so report 0 inserted.
			continue
		}
		batch = survivorIdxOf(batch, survivorIdx)

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

// filterOrphanAppItems returns the set of appSessionId values in `batch`
// that are missing from app_sessions right now, plus the indices of the
// batch rows that are NOT orphans (so the caller can re-slice). The
// query is O(1) round-trips (one SELECT with ANY($1) over the distinct
// ids in the batch, indexed lookup on app_sessions.id).
func (r *NewSchemaRepo) filterOrphanAppItems(ctx context.Context, batch []models.AppItem) (orphanIDs []string, survivorIdx []int) {
	seen := make(map[string]struct{}, len(batch))
	distinct := make([]string, 0, len(batch))
	for _, e := range batch {
		if e.AppSessionID == "" {
			continue
		}
		if _, ok := seen[e.AppSessionID]; ok {
			continue
		}
		seen[e.AppSessionID] = struct{}{}
		distinct = append(distinct, e.AppSessionID)
	}
	if len(distinct) == 0 {
		// every row had an empty app_session_id — that's a client bug,
		// but the FK is NOT NULL on this column, so those rows would
		// fail to insert anyway. Treat them all as orphans (the server
		// can't tell if it's a missing FK or a NULL FK — both are bad).
		for i, e := range batch {
			if e.AppSessionID == "" {
				orphanIDs = append(orphanIDs, "<empty>")
				continue
			}
			survivorIdx = append(survivorIdx, i)
		}
		return orphanIDs, survivorIdx
	}

	rows, err := r.pool.Query(ctx, "SELECT id FROM app_sessions WHERE id = ANY($1)", distinct)
	if err != nil {
		// If the preflight itself fails, fail closed — return the full
		// batch so the caller 500s. Better to alert than to silently
		// drop data on a transient pg error.
		return nil, nil
	}
	defer rows.Close()
	present := make(map[string]struct{}, len(distinct))
	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err == nil {
			present[id] = struct{}{}
		}
	}

	orphanSet := make(map[string]struct{})
	for i, e := range batch {
		// Empty appSessionID is treated as an orphan (the FK is NOT NULL
		// on app_items.app_session_id, so the INSERT would 500 anyway).
		if e.AppSessionID == "" {
			if _, already := orphanSet["<empty>"]; !already {
				orphanIDs = append(orphanIDs, "<empty>")
				orphanSet["<empty>"] = struct{}{}
			}
			continue
		}
		if _, ok := present[e.AppSessionID]; !ok {
			if _, already := orphanSet[e.AppSessionID]; !already {
				orphanIDs = append(orphanIDs, e.AppSessionID)
				orphanSet[e.AppSessionID] = struct{}{}
			}
			continue
		}
		survivorIdx = append(survivorIdx, i)
	}
	return orphanIDs, survivorIdx
}

// survivorIdxOf returns the entries in `batch` at the indices in `idx`,
// preserving order. The idx slice comes from filterOrphanAppItems.
func survivorIdxOf(batch []models.AppItem, idx []int) []models.AppItem {
	out := make([]models.AppItem, 0, len(idx))
	for _, i := range idx {
		out = append(out, batch[i])
	}
	return out
}

// logOrphanAppItems writes a single WARN line per batch with the orphan
// count and the first 20 appSessionIds. Using log.Printf here (not
// the structured logger) because this file is mid-import-build and
// adding a logger dep is out of scope. The cap keeps a single misbehaving
// client from spamming the log on every sync.
func logOrphanAppItems(entries []models.AppItem, orphanIDs []string) {
	cap := 20
	preview := orphanIDs
	if len(preview) > cap {
		preview = preview[:cap]
	}
	emp := ""
	if len(entries) > 0 {
		emp = entries[0].EmployeeID
	}
	log.Printf(
		"[new_schema] WARN: app-items batch contained %d orphan app_session_id(s) for employee=%s (dropped from this batch, will re-sync next pass). ids=%v%s",
		len(orphanIDs), emp, preview,
		ifMore(len(orphanIDs), cap),
	)
}

func ifMore(have, cap int) string {
	if have <= cap {
		return ""
	}
	return fmt.Sprintf(" (and %d more)", have-cap)
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
		       foreground_seconds, background_seconds, synced_at,
		       status, last_activity_at, last_sync_at, created_at
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
			&s.ForegroundSeconds, &s.BackgroundSeconds, &s.SyncedAt,
			&s.Status, &s.LastActivityAt, &s.LastSyncAt, &s.CreatedAt,
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
// App Sessions Usage (per-app aggregate for web dashboard)
// ────────────────────────────────
//
// The web "App Usage" page needs per-application totals, not the raw
// session list. Summing (endedAt - startedAt) per row inflates the
// total when one window opens multiple tabs (each tab was historically
// a separate row in the client's data; we still defensively use
// MIN/MAX here so the math is right even before any client-side
// dedupe lands). Returns one row per (appDisplayName, processName)
// with: sessionCount, firstOpenedAt, lastClosedAt, totalDurationSeconds.

type AppSessionUsageListParams struct {
	EmployeeID string
	Search     string
	Platform   string
	DateFrom   time.Time
	DateTo     time.Time
	Page       int
	PerPage    int
}

type AppSessionUsageRow struct {
	AppDisplayName     string
	ProcessName        string
	SessionCount       int
	FirstOpenedAt      time.Time
	LastClosedAt       time.Time
	TotalDurationSeconds float64
}

type AppSessionUsageListResult struct {
	Rows      []AppSessionUsageRow
	Total     int
	Page      int
	PerPage   int
	TotalPages int
}

func (r *NewSchemaRepo) AggregateAppSessionsUsage(ctx context.Context, params AppSessionUsageListParams) (*AppSessionUsageListResult, error) {
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

	// Count distinct (app_display_name, process_name) groups so the page
	// can paginate through results, matching the rest of the dashboard.
	countQuery := fmt.Sprintf(`
		SELECT COUNT(*) FROM (
			SELECT 1 FROM app_sessions %s
			GROUP BY app_display_name, process_name
		) g
	`, whereClause)
	var total int
	if err := r.pool.QueryRow(ctx, countQuery, args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count app_sessions usage: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	totalPages := (total + params.PerPage - 1) / params.PerPage

	// Per-app aggregate:
	//   - sessionCount        = COUNT(*)
	//   - firstOpenedAt       = MIN(started_at)
	//   - lastClosedAt        = MAX(COALESCE(ended_at, last_sync_at, started_at))
	//     (2026-09-02 3-state lifecycle: for ACTIVE/STALE sessions the real
	//      "end" we know about is last_sync_at; for CLOSED rows ended_at
	//      is final. Using COALESCE picks the most-recent truthful moment
	//      regardless of status — same shape the web page uses for the
	//      STALE/CLOSED case in sessionDurationSeconds.)
	//   - totalDurationSeconds = SUM of (COALESCE(ended, last_sync, started) - started_at)
	//     NOTE: this is the SUM-OF-DURATIONS shape. The page intentionally
	//     displays max(lastClosed) - min(firstOpened) for the "Duration"
	//     column so multi-tab sessions never inflate the per-app total.
	//     The SUM is provided for backwards-compat and for the
	//     "Active Time" tile that sums across all apps.
	query := fmt.Sprintf(`
		SELECT app_display_name,
		       COALESCE(process_name, '') AS process_name,
		       COUNT(*) AS session_count,
		       MIN(started_at) AS first_opened_at,
		       MAX(COALESCE(ended_at, last_sync_at, started_at)) AS last_closed_at,
		       COALESCE(SUM(EXTRACT(EPOCH FROM (COALESCE(ended_at, last_sync_at, started_at) - started_at))), 0) AS total_duration_seconds
		FROM app_sessions %s
		GROUP BY app_display_name, process_name
		ORDER BY total_duration_seconds DESC
		LIMIT $%d OFFSET $%d
	`, whereClause, argIdx, argIdx+1)
	args = append(args, params.PerPage, offset)

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("aggregate app_sessions usage: %w", err)
	}
	defer rows.Close()

	var usage []AppSessionUsageRow
	for rows.Next() {
		var u AppSessionUsageRow
		if err := rows.Scan(
			&u.AppDisplayName,
			&u.ProcessName,
			&u.SessionCount,
			&u.FirstOpenedAt,
			&u.LastClosedAt,
			&u.TotalDurationSeconds,
		); err != nil {
			return nil, fmt.Errorf("scan app_sessions usage row: %w", err)
		}
		usage = append(usage, u)
	}

	return &AppSessionUsageListResult{
		Rows:       usage,
		Total:      total,
		Page:       params.Page,
		PerPage:    params.PerPage,
		TotalPages: totalPages,
	}, nil
}

// ────────────────────────────────
// Per-app session list (powers the /employee-journey/apps chevron expand)
// ────────────────────────────────
//
// The "App Usage" page lists one row per (appDisplayName, processName).
// When the user clicks a row's chevron, the page calls this endpoint to
// fetch the raw app_sessions rows that make up that app's aggregate —
// with server-side pagination so an employee who opened Chrome 200
// times in a week doesn't ship 200 rows up front. The page is responsible
// for re-keying the call when the date filter changes (the URL `?from`
// and `?to` query params become part of the React Query key).
//
// `appDisplayName` and `processName` MUST both be passed: the
// `(appDisplayName, processName)` tuple is the same grouping key the
// aggregate uses, so missing either would match too many or too few
// rows. `appDisplayName` may be empty (rare — only when the aggregate
// group has no display name and fell back to processName alone); an
// empty processName matches rows where process_name IS NULL (Postgres
// NULLs don't match `= ''`, so we special-case to IS NULL).

type AppSessionForAppListParams struct {
	EmployeeID     string
	AppDisplayName string
	ProcessName    string
	DateFrom       time.Time
	DateTo         time.Time
	Page           int
	PerPage        int
}

func (r *NewSchemaRepo) ListAppSessionsForApp(ctx context.Context, params AppSessionForAppListParams) (*AppSessionListResult, error) {
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

	// Group key: match the aggregate's GROUP BY exactly so the per-app
	// session list is consistent with the per-app row's sessionCount.
	// Postgres NULL doesn't match `= ''`, so each side of the pair needs
	// its own IS NULL / = '' branch.
	//
	// Both real cases:
	//   - appDisplayName + processName both non-empty: exact match
	//   - one or both empty: the empty side is treated as "match IS NULL
	//     OR = ''" so it catches the rows the aggregate grouped together.
	if params.AppDisplayName == "" {
		conditions = append(conditions, fmt.Sprintf("(app_display_name = '' OR app_display_name IS NULL)"))
	} else {
		conditions = append(conditions, fmt.Sprintf("app_display_name = $%d", argIdx))
		args = append(args, params.AppDisplayName)
		argIdx++
	}
	if params.ProcessName == "" {
		conditions = append(conditions, "(process_name = '' OR process_name IS NULL)")
	} else {
		conditions = append(conditions, fmt.Sprintf("process_name = $%d", argIdx))
		args = append(args, params.ProcessName)
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

	whereClause := "WHERE " + strings.Join(conditions, " AND ")

	// Reuse the same count + SELECT shape as ListAppSessions.
	countQuery := fmt.Sprintf("SELECT COUNT(*) FROM app_sessions %s", whereClause)
	var total int
	if err := r.pool.QueryRow(ctx, countQuery, args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count app_sessions for app: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	totalPages := (total + params.PerPage - 1) / params.PerPage

	query := fmt.Sprintf(`
		SELECT id, employee_id, process_name, app_display_name, started_at, ended_at,
		       machine_id, session_id, platform, process_id, parent_process_id,
		       installed_app_id, installed_package_id, grouped_by, cgroup_scope, context_label,
		       foreground_seconds, background_seconds, synced_at,
		       status, last_activity_at, last_sync_at, created_at
		FROM app_sessions %s
		ORDER BY started_at DESC
		LIMIT $%d OFFSET $%d
	`, whereClause, argIdx, argIdx+1)
	args = append(args, params.PerPage, offset)

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list app_sessions for app: %w", err)
	}
	defer rows.Close()

	var sessions []models.AppSession
	for rows.Next() {
		var s models.AppSession
		if err := rows.Scan(
			&s.ID, &s.EmployeeID, &s.ProcessName, &s.AppDisplayName, &s.StartedAt, &s.EndedAt,
			&s.MachineID, &s.SessionID, &s.Platform, &s.ProcessID, &s.ParentProcessID,
			&s.InstalledAppID, &s.InstalledPackageID, &s.GroupedBy, &s.CgroupScope, &s.ContextLabel,
			&s.ForegroundSeconds, &s.BackgroundSeconds, &s.SyncedAt,
			&s.Status, &s.LastActivityAt, &s.LastSyncAt, &s.CreatedAt,
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
		// Search the page title/identifier AND the exact URL/domain — searching
		// "why update" must surface the google.com/search?q=why+update page itself,
		// not just rows whose title mentions it.
		conditions = append(conditions, fmt.Sprintf(
			"(LOWER(title) LIKE LOWER($%d) OR LOWER(identifier) LIKE LOWER($%d) OR LOWER(COALESCE(url, '')) LIKE LOWER($%d) OR LOWER(COALESCE(domain, '')) LIKE LOWER($%d))",
			argIdx, argIdx, argIdx, argIdx))
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
// app_fingerprint and returns its id. Before inserting, it checks for an existing
// non-deleted row with the same normalized app_name (cross-OS dedup: same product
// arriving from Linux .desktop + Windows Start Menu gets one catalog row).
func (r *NewSchemaRepo) UpsertApplicationCatalog(ctx context.Context, tx pgx.Tx, e models.InstalledApplication) (string, error) {
	var id string

	// 1. Look for an existing non-deleted row by normalized app_name.
	normalizedName := normalizeAppName(e.AppName)
	err := tx.QueryRow(ctx, `
		SELECT id FROM installed_applications
		WHERE deleted_at IS NULL
		  AND regexp_replace(lower(app_name), '[^a-z0-9]', '', 'g') = $1
		LIMIT 1
	`, normalizedName).Scan(&id)
	if err == nil {
		// Found existing row — update metadata in place, return its id.
		err = tx.QueryRow(ctx, `
			UPDATE installed_applications SET
				app_name = $2,
				binary_name = COALESCE(NULLIF($3, ''), binary_name),
				is_browser = is_browser OR $4,
				desktop_id = COALESCE(NULLIF($5, ''), desktop_id),
				categories = COALESCE(NULLIF($6, ''), categories),
				detected_at = $7,
				synced_at = $8
			WHERE id = $1
			RETURNING id
		`, id, e.AppName, e.BinaryName, e.IsBrowser, e.DesktopID, e.Categories, e.DetectedAt, e.SyncedAt).Scan(&id)
		if err == nil {
			return id, nil
		}
		if !errors.Is(err, pgx.ErrNoRows) {
			return "", fmt.Errorf("update existing catalog: %w", err)
		}
	}

	// 2. No existing row — insert new.
	err = tx.QueryRow(ctx, `
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

// normalizeAppName produces the cross-OS merge key for an app display name:
// lowercase, strip every non-alphanumeric character.
func normalizeAppName(name string) string {
	var b strings.Builder
	for _, r := range strings.ToLower(name) {
		if r >= 'a' && r <= 'z' || r >= '0' && r <= '9' {
			b.WriteRune(r)
		}
	}
	return b.String()
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

// GetBrowserNameByProcessName returns the friendly app_name from installed_applications
// for a given process/binary name where is_browser = true. Returns empty string when
// no catalog entry exists (the caller should fall back to the raw process name).
func (r *NewSchemaRepo) GetBrowserNameByProcessName(ctx context.Context, processName string) (string, error) {
	if strings.TrimSpace(processName) == "" {
		return "", nil
	}
	var name string
	err := r.pool.QueryRow(ctx, `
		SELECT app_name
		FROM installed_applications
		WHERE deleted_at IS NULL
		  AND is_browser = true
		  AND (binary_name = $1 OR app_name = $1)
		LIMIT 1
	`, processName).Scan(&name)
	if err == pgx.ErrNoRows {
		return "", nil
	}
	if err != nil {
		return "", fmt.Errorf("lookup browser name for %s: %w", processName, err)
	}
	return name, nil
}

// ────────────────────────────────
// Location Samples (Phase 3 GPS)
// ────────────────────────────────

func (r *NewSchemaRepo) BulkUpsertLocationSamples(ctx context.Context, entries []models.LocationSample) (int, error) {
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
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4, argIdx+5, argIdx+6, argIdx+7, argIdx+8,
			))
			args = append(args,
				e.ID, e.EmployeeID, e.Latitude, e.Longitude, e.AccuracyM, e.AltitudeM,
				e.Source, e.Address, e.CapturedAt,
			)
			argIdx += 9
		}

		query := fmt.Sprintf(`
			INSERT INTO location_samples
				(id, employee_id, latitude, longitude, accuracy_m, altitude_m, source, address, captured_at)
			VALUES %s
			ON CONFLICT (id) DO UPDATE SET
				latitude = EXCLUDED.latitude,
				longitude = EXCLUDED.longitude,
				accuracy_m = EXCLUDED.accuracy_m,
				altitude_m = EXCLUDED.altitude_m,
				source = EXCLUDED.source,
				address = EXCLUDED.address,
				captured_at = EXCLUDED.captured_at,
				synced_at = NOW()
		`, strings.Join(valueStrings, ", "))

		tag, err := r.pool.Exec(ctx, query, args...)
		if err != nil {
			return inserted, fmt.Errorf("bulk upsert location_samples: %w", err)
		}
		inserted += int(tag.RowsAffected())
	}
	return inserted, nil
}

type LocationSampleListParams struct {
	EmployeeID string
	DateFrom   time.Time
	DateTo     time.Time
	Page       int
	PerPage    int
}

type LocationSampleListResult struct {
	Items      []models.LocationSample
	Total      int
	Page       int
	PerPage    int
	TotalPages int
}

// ────────────────────────────────
// Hours Insights
// ────────────────────────────────

type HoursInsightsParams struct {
	EmployeeID string
	DateFrom   time.Time
	DateTo     time.Time
	Preset     string
}

type HoursInsightsSummary struct {
	TotalSeconds        float64
	ProductiveSeconds   float64
	UnproductiveSeconds float64
	NeutralSeconds      float64
	FocusScore          float64
	AppCount            int
	SiteCount           int
}

type HoursInsightsChartBucket struct {
	Bucket       string
	Productive   float64
	Unproductive float64
	Neutral      float64
}

type HoursInsightsAppBucket struct {
	Bucket string
	Apps   map[string]float64
}

type HoursInsightsAppMeta struct {
	Name         string
	TotalSeconds float64
	Color        string
	Category     string
	Type         string
	SessionCount int
}

type HoursInsightsTopItem struct {
	Name         string
	Kind         string
	Category     string
	Type         string
	Color        string
	TotalSeconds float64
	FocusScore   float64
	IsBrowser    bool
}

type HoursInsightsResult struct {
	EmployeeID   string
	EmployeeName string
	Department   string
	RangeFrom    time.Time
	RangeTo      time.Time
	RangeLabel   string
	Summary      HoursInsightsSummary
	Chart        []HoursInsightsChartBucket
	AppChart     []HoursInsightsAppBucket
	TopApps      []HoursInsightsAppMeta
	TopItems     []HoursInsightsTopItem
}


func (r *NewSchemaRepo) ListLocationSamples(ctx context.Context, params LocationSampleListParams) (*LocationSampleListResult, error) {
	if params.Page < 1 {
		params.Page = 1
	}
	if params.PerPage < 1 || params.PerPage > 100 {
		params.PerPage = 30
	}

	var conditions []string
	var args []interface{}
	argIdx := 1

	conditions = append(conditions, "ls.deleted_at IS NULL")

	if params.EmployeeID != "" {
		conditions = append(conditions, fmt.Sprintf("ls.employee_id = $%d", argIdx))
		args = append(args, params.EmployeeID)
		argIdx++
	}
	if !params.DateFrom.IsZero() {
		conditions = append(conditions, fmt.Sprintf("ls.captured_at >= $%d", argIdx))
		args = append(args, params.DateFrom)
		argIdx++
	}
	if !params.DateTo.IsZero() {
		conditions = append(conditions, fmt.Sprintf("ls.captured_at <= $%d", argIdx))
		args = append(args, params.DateTo)
		argIdx++
	}

	whereClause := "WHERE " + strings.Join(conditions, " AND ")

	countQuery := fmt.Sprintf("SELECT COUNT(*) FROM location_samples ls %s", whereClause)
	var total int
	if err := r.pool.QueryRow(ctx, countQuery, args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count location_samples: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	listArgs := append(append([]interface{}{}, args...), params.PerPage, offset)
	limitIdx := argIdx
	offsetIdx := argIdx + 1

	query := fmt.Sprintf(`
		SELECT ls.id, ls.employee_id, COALESCE(e.name, '') AS employee_name,
		       ls.latitude, ls.longitude, ls.accuracy_m, ls.altitude_m,
		       ls.source, ls.address, ls.captured_at, ls.synced_at, ls.created_at
		FROM location_samples ls
		LEFT JOIN employees e ON e.employee_id = ls.employee_id AND e.deleted_at IS NULL
		%s
		ORDER BY ls.captured_at DESC
		LIMIT $%d OFFSET $%d
	`, whereClause, limitIdx, offsetIdx)

	rows, err := r.pool.Query(ctx, query, listArgs...)
	if err != nil {
		return nil, fmt.Errorf("list location_samples: %w", err)
	}
	defer rows.Close()

	var items []models.LocationSample
	for rows.Next() {
		var s models.LocationSample
		if err := rows.Scan(
			&s.ID, &s.EmployeeID, &s.EmployeeName, &s.Latitude, &s.Longitude, &s.AccuracyM, &s.AltitudeM,
			&s.Source, &s.Address, &s.CapturedAt, &s.SyncedAt, &s.CreatedAt,
		); err != nil {
			return nil, fmt.Errorf("scan location_sample row: %w", err)
		}
		items = append(items, s)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}

	totalPages := total / params.PerPage
	if total%params.PerPage != 0 {
		totalPages++
	}

	return &LocationSampleListResult{
		Items:      items,
		Total:      total,
		Page:       params.Page,
		PerPage:    params.PerPage,
		TotalPages: totalPages,
	}, nil
}

// GetHoursInsights returns aggregated usage data for the hours-insights page.
// It computes summary stats, chart buckets, individual application chart buckets,
// and a top-items list for a single employee over the requested date range.
func (r *NewSchemaRepo) GetHoursInsights(ctx context.Context, params HoursInsightsParams) (*HoursInsightsResult, error) {
	if params.EmployeeID == "" {
		return nil, fmt.Errorf("employee_id is required")
	}
	if params.DateFrom.IsZero() {
		switch params.Preset {
		case "today":
			now := time.Now()
			params.DateFrom = time.Date(now.Year(), now.Month(), now.Day(), 0, 0, 0, 0, now.Location())
			params.DateTo = time.Date(now.Year(), now.Month(), now.Day(), 23, 59, 59, 999999999, now.Location())
		case "yesterday":
			y := time.Now().AddDate(0, 0, -1)
			params.DateFrom = time.Date(y.Year(), y.Month(), y.Day(), 0, 0, 0, 0, y.Location())
			params.DateTo = time.Date(y.Year(), y.Month(), y.Day(), 23, 59, 59, 999999999, y.Location())
		case "7d":
			now := time.Now()
			start := now.AddDate(0, 0, -6)
			params.DateFrom = time.Date(start.Year(), start.Month(), start.Day(), 0, 0, 0, 0, start.Location())
			params.DateTo = now
		case "30d":
			now := time.Now()
			start := now.AddDate(0, 0, -29)
			params.DateFrom = time.Date(start.Year(), start.Month(), start.Day(), 0, 0, 0, 0, start.Location())
			params.DateTo = now
		default:
			params.DateFrom = time.Now().AddDate(0, 0, -30)
			params.DateTo = time.Now()
		}
	} else if params.DateTo.IsZero() {
		params.DateTo = time.Now()
	}

	result := &HoursInsightsResult{
		EmployeeID: params.EmployeeID,
		RangeFrom:  params.DateFrom,
		RangeTo:    params.DateTo,
		Chart:      []HoursInsightsChartBucket{},
		AppChart:   []HoursInsightsAppBucket{},
		TopApps:    []HoursInsightsAppMeta{},
		TopItems:   []HoursInsightsTopItem{},
	}

	// 1) Employee info
	if err := r.pool.QueryRow(ctx, `
		SELECT e.name, COALESCE(d.name, '')
		FROM employees e
		LEFT JOIN departments d ON d.id = e.department_id AND d.deleted_at IS NULL
		WHERE e.employee_id = $1 AND e.deleted_at IS NULL
	`, params.EmployeeID).Scan(&result.EmployeeName, &result.Department); err != nil {
		if err == pgx.ErrNoRows {
			return nil, fmt.Errorf("employee not found")
		}
		return nil, fmt.Errorf("load employee: %w", err)
	}

	// Determine bucket size for chart: hourly for ranges <= 3 days, daily otherwise.
	bucketSize := "1 day"
	bucketField := "day"
	maxBucketSeconds := 86400.0
	if params.DateTo.Sub(params.DateFrom) <= 72*time.Hour {
		bucketSize = "1 hour"
		bucketField = "hour"
		maxBucketSeconds = 3600.0
	}
	bucketFormat := "HH24:00"
	if bucketSize == "1 day" {
		bucketFormat = "Mon DD"
	}

	// 2) Summary + top items in one query using CTEs.
	// Bounding rules:
	// - app_sessions: if ended_at is set, use ended_at; if status is ACTIVE with recent sync, bound by NOW();
	//   otherwise use COALESCE(last_activity_at, last_sync_at, started_at). Never run away.
	// - app_items: bound by parent session (s.ended_at / s.last_sync_at) when closed_at is NULL.
	//   item_type is restricted to 'browser_tab' to avoid double-counting navigation events.
	summaryQuery := `
	WITH params AS (
		SELECT $1::varchar AS emp_id, $2::timestamptz AS from_ts, $3::timestamptz AS to_ts
	),
	emp AS (
		SELECT e.employee_id, e.name, COALESCE(d.name, '') AS department
		FROM employees e
		LEFT JOIN departments d ON d.id = e.department_id AND d.deleted_at IS NULL
		WHERE e.employee_id = (SELECT emp_id FROM params) AND e.deleted_at IS NULL
	),
	app_usage AS (
		SELECT
			s.app_display_name,
			s.installed_app_id,
			GREATEST(s.started_at, (SELECT from_ts FROM params)) AS eff_start,
			LEAST(
				CASE 
					WHEN s.ended_at IS NOT NULL THEN s.ended_at
					WHEN s.status = 'ACTIVE' AND s.last_sync_at > NOW() - INTERVAL '10 minutes' THEN LEAST(NOW(), (SELECT to_ts FROM params))
					ELSE COALESCE(s.last_activity_at, s.last_sync_at, s.started_at)
				END,
				(SELECT to_ts FROM params)
			) AS eff_end,
			s.foreground_seconds,
			s.background_seconds
		FROM app_sessions s
		WHERE s.employee_id = (SELECT emp_id FROM params)
			AND s.deleted_at IS NULL
			AND s.started_at < (SELECT to_ts FROM params)
			AND COALESCE(s.ended_at, s.last_sync_at, s.last_activity_at, s.started_at) > (SELECT from_ts FROM params)
			AND COALESCE(s.ended_at, s.last_sync_at, s.last_activity_at, s.started_at) > s.started_at
	),
	site_usage AS (
		SELECT
			ai.domain,
			GREATEST(ai.opened_at, (SELECT from_ts FROM params)) AS eff_start,
			LEAST(
				COALESCE(ai.closed_at, s.ended_at, s.last_sync_at, s.last_activity_at, ai.opened_at),
				(SELECT to_ts FROM params)
			) AS eff_end
		FROM app_items ai
		LEFT JOIN app_sessions s ON s.id = ai.app_session_id
		WHERE ai.employee_id = (SELECT emp_id FROM params)
			AND ai.deleted_at IS NULL
			AND ai.item_type = 'browser_tab'
			AND ai.domain IS NOT NULL
			AND ai.domain <> ''
			AND ai.opened_at < (SELECT to_ts FROM params)
			AND COALESCE(ai.closed_at, s.ended_at, s.last_sync_at, s.last_activity_at, ai.opened_at) > (SELECT from_ts FROM params)
			AND COALESCE(ai.closed_at, s.ended_at, s.last_sync_at, s.last_activity_at, ai.opened_at) > ai.opened_at
	),
	app_cat AS (
		SELECT DISTINCT ON (ia.id) ia.id,
			NULLIF(mt.name, '') AS type_name,
			COALESCE(NULLIF(mt.color, ''), '#6b7280') AS type_color
		FROM installed_applications ia
		LEFT JOIN monitoring_types mt ON mt.id = ia.type_id AND mt.deleted_at IS NULL
		WHERE ia.employee_id = (SELECT emp_id FROM params) AND ia.deleted_at IS NULL
	),
	site_cat AS (
		SELECT DISTINCT ON (ms.domain) ms.domain,
			NULLIF(mt.name, '') AS type_name,
			COALESCE(NULLIF(mt.color, ''), '#6b7280') AS type_color
		FROM monitoring_sites ms
		LEFT JOIN monitoring_types mt ON mt.id = ms.type_id AND mt.deleted_at IS NULL
		WHERE ms.domain IN (SELECT DISTINCT domain FROM site_usage) AND ms.deleted_at IS NULL
	),
	app_summary AS (
		SELECT
			COALESCE(SUM(EXTRACT(EPOCH FROM (eff_end - eff_start))), 0) AS total_sec,
			COALESCE(SUM(CASE WHEN ac.type_name = 'Productive' THEN EXTRACT(EPOCH FROM (eff_end - eff_start)) ELSE 0 END), 0) AS productive_sec,
			COALESCE(SUM(CASE WHEN ac.type_name = 'Unproductive' THEN EXTRACT(EPOCH FROM (eff_end - eff_start)) ELSE 0 END), 0) AS unproductive_sec,
			COALESCE(SUM(CASE WHEN ac.type_name = 'Neutral' OR ac.type_name IS NULL THEN EXTRACT(EPOCH FROM (eff_end - eff_start)) ELSE 0 END), 0) AS neutral_sec,
			COUNT(DISTINCT app_display_name) AS app_count,
			COALESCE(SUM(foreground_seconds), 0) AS fg_sec,
			COALESCE(SUM(background_seconds), 0) AS bg_sec
		FROM app_usage au
		LEFT JOIN app_cat ac ON ac.id = au.installed_app_id
	),
	site_summary AS (
		SELECT
			COALESCE(SUM(EXTRACT(EPOCH FROM (eff_end - eff_start))), 0) AS total_sec,
			COUNT(DISTINCT su.domain) AS site_count
		FROM site_usage su
	),
	combined AS (
		SELECT
			CASE WHEN asu.total_sec > 0 THEN asu.total_sec ELSE ssu.total_sec END AS total_seconds,
			asu.productive_sec AS productive_seconds,
			asu.unproductive_sec AS unproductive_seconds,
			asu.neutral_sec AS neutral_seconds,
			asu.app_count,
			ssu.site_count,
			asu.fg_sec AS fg_sec,
			asu.bg_sec AS bg_sec
		FROM app_summary asu, site_summary ssu
	),
	top_apps AS (
		SELECT
			au.app_display_name AS name,
			'app' AS kind,
			COALESCE(ac.type_name, 'Neutral') AS category,
			COALESCE(ac.type_name, 'Neutral') AS type,
			COALESCE(ac.type_color, '#6b7280') AS color,
			SUM(EXTRACT(EPOCH FROM (au.eff_end - au.eff_start))) AS totalSeconds,
			COUNT(*) AS session_count,
			CASE WHEN SUM(au.foreground_seconds + au.background_seconds) > 0
				THEN ROUND(SUM(au.foreground_seconds) / SUM(au.foreground_seconds + au.background_seconds) * 1000) / 10
				ELSE 0 END AS focusScore,
			COALESCE(ia.is_browser, FALSE) AS isBrowser
		FROM app_usage au
		LEFT JOIN app_cat ac ON ac.id = au.installed_app_id
		LEFT JOIN installed_applications ia ON ia.id = au.installed_app_id AND ia.deleted_at IS NULL
		WHERE au.app_display_name <> '' AND au.app_display_name IS NOT NULL
		GROUP BY au.app_display_name, ac.type_name, ac.type_color, ia.is_browser
	),
	top_sites AS (
		SELECT
			su.domain AS name,
			'site' AS kind,
			COALESCE(sc.type_name, 'Neutral') AS category,
			COALESCE(sc.type_name, 'Neutral') AS type,
			COALESCE(sc.type_color, '#6b7280') AS color,
			SUM(EXTRACT(EPOCH FROM (su.eff_end - su.eff_start))) AS totalSeconds,
			COUNT(*) AS session_count,
			(SELECT CASE WHEN c.fg_sec + c.bg_sec > 0 THEN ROUND(c.fg_sec / (c.fg_sec + c.bg_sec) * 1000) / 10 ELSE 0 END FROM combined c) AS focusScore,
			FALSE AS isBrowser
		FROM site_usage su
		LEFT JOIN site_cat sc ON sc.domain = su.domain
		WHERE su.domain <> ''
		GROUP BY su.domain, sc.type_name, sc.type_color
	)
	SELECT
		(SELECT row_to_json(e) FROM (SELECT employee_id, name, department FROM emp WHERE employee_id = (SELECT emp_id FROM params)) e) AS employee,
		(SELECT row_to_json(c) FROM combined c) AS summary,
		COALESCE(
			(SELECT json_agg(t ORDER BY t.totalSeconds DESC) FROM (
				SELECT name, kind, category, type, color, totalSeconds, focusScore, isBrowser FROM top_apps
				UNION ALL
				SELECT name, kind, category, type, color, totalSeconds, focusScore, isBrowser FROM top_sites
				LIMIT 20
			) t),
			'[]'::json
		) AS top_items,
		COALESCE(
			(SELECT json_agg(t ORDER BY t.totalSeconds DESC) FROM top_apps t),
			'[]'::json
		) AS raw_top_apps
	`

	var employeeJSON []byte
	var summaryJSON []byte
	var topItemsJSON []byte
	var rawTopAppsJSON []byte

	if err := r.pool.QueryRow(ctx, summaryQuery, params.EmployeeID, params.DateFrom, params.DateTo).Scan(
		&employeeJSON, &summaryJSON, &topItemsJSON, &rawTopAppsJSON,
	); err != nil {
		return nil, fmt.Errorf("hours insights summary query: %w", err)
	}

	// Parse employee JSON
	var empMap map[string]interface{}
	if err := json.Unmarshal(employeeJSON, &empMap); err != nil {
		return nil, fmt.Errorf("parse employee json: %w", err)
	}
	if name, ok := empMap["name"].(string); ok {
		result.EmployeeName = name
	}
	if dept, ok := empMap["department"].(string); ok {
		result.Department = dept
	}

	// Parse summary JSON
	var summaryMap map[string]interface{}
	if err := json.Unmarshal(summaryJSON, &summaryMap); err != nil {
		return nil, fmt.Errorf("parse summary json: %w", err)
	}
	result.Summary = HoursInsightsSummary{
		TotalSeconds:        parseFloat(summaryMap["total_seconds"]),
		ProductiveSeconds:   parseFloat(summaryMap["productive_seconds"]),
		UnproductiveSeconds: parseFloat(summaryMap["unproductive_seconds"]),
		NeutralSeconds:      parseFloat(summaryMap["neutral_seconds"]),
		AppCount:            int(parseFloat(summaryMap["app_count"])),
		SiteCount:           int(parseFloat(summaryMap["site_count"])),
	}
	fg := parseFloat(summaryMap["fg_sec"])
	bg := parseFloat(summaryMap["bg_sec"])
	if fg+bg > 0 {
		result.Summary.FocusScore = math.Round(fg / (fg + bg) * 1000) / 10
	}

	// Parse top items JSON
	var topItems []HoursInsightsTopItem
	if err := json.Unmarshal(topItemsJSON, &topItems); err != nil {
		return nil, fmt.Errorf("parse top items json: %w", err)
	}
	result.TopItems = topItems

	// Parse raw top apps
	type RawAppItem struct {
		Name         string  `json:"name"`
		TotalSeconds float64 `json:"totalseconds"`
		Category     string  `json:"category"`
		Type         string  `json:"type"`
		SessionCount int     `json:"session_count"`
	}
	var rawApps []RawAppItem
	_ = json.Unmarshal(rawTopAppsJSON, &rawApps)

	// Build TopApps with curated color palette
	palette := []string{
		"#3b82f6", // Blue
		"#8b5cf6", // Purple
		"#10b981", // Emerald
		"#f59e0b", // Amber
		"#06b6d4", // Cyan
		"#ec4899", // Pink
	}
	topAppNamesMap := make(map[string]bool)
	limitTop := len(rawApps)
	if limitTop > 6 {
		limitTop = 6
	}
	for i := 0; i < limitTop; i++ {
		color := palette[i%len(palette)]
		result.TopApps = append(result.TopApps, HoursInsightsAppMeta{
			Name:         rawApps[i].Name,
			TotalSeconds: rawApps[i].TotalSeconds,
			Color:        color,
			Category:     rawApps[i].Category,
			Type:         rawApps[i].Type,
			SessionCount: rawApps[i].SessionCount,
		})
		topAppNamesMap[rawApps[i].Name] = true
	}

	// 3) Chart data — Productivity buckets
	chartQuery := `
	WITH params AS (
		SELECT $1::varchar AS emp_id, $2::timestamptz AS from_ts, $3::timestamptz AS to_ts
	),
	app_usage AS (
		SELECT
			s.app_display_name,
			s.installed_app_id,
			GREATEST(s.started_at, (SELECT from_ts FROM params)) AS eff_start,
			LEAST(
				CASE 
					WHEN s.ended_at IS NOT NULL THEN s.ended_at
					WHEN s.status = 'ACTIVE' AND s.last_sync_at > NOW() - INTERVAL '10 minutes' THEN LEAST(NOW(), (SELECT to_ts FROM params))
					ELSE COALESCE(s.last_activity_at, s.last_sync_at, s.started_at)
				END,
				(SELECT to_ts FROM params)
			) AS eff_end
		FROM app_sessions s
		WHERE s.employee_id = (SELECT emp_id FROM params)
			AND s.deleted_at IS NULL
			AND s.started_at < (SELECT to_ts FROM params)
			AND COALESCE(s.ended_at, s.last_sync_at, s.last_activity_at, s.started_at) > (SELECT from_ts FROM params)
			AND COALESCE(s.ended_at, s.last_sync_at, s.last_activity_at, s.started_at) > s.started_at
	),
	app_cat AS (
		SELECT DISTINCT ON (ia.id) ia.id,
			NULLIF(mt.name, '') AS type_name,
			COALESCE(NULLIF(mt.color, ''), '#6b7280') AS type_color
		FROM installed_applications ia
		LEFT JOIN monitoring_types mt ON mt.id = ia.type_id AND mt.deleted_at IS NULL
		WHERE ia.employee_id = (SELECT emp_id FROM params) AND ia.deleted_at IS NULL
	),
	buckets AS (
		SELECT generate_series(
			date_trunc($4, (SELECT from_ts FROM params)),
			date_trunc($4, (SELECT to_ts FROM params)),
			$5::interval
		) AS bucket_start
	)
	SELECT
		to_char(b.bucket_start, $6) AS bucket,
		COALESCE(SUM(CASE WHEN au.app_display_name IS NOT NULL AND ac.type_name = 'Productive' THEN EXTRACT(EPOCH FROM (LEAST(au.eff_end, b.bucket_start + $5::interval) - GREATEST(au.eff_start, b.bucket_start))) ELSE 0 END), 0) AS productive,
		COALESCE(SUM(CASE WHEN au.app_display_name IS NOT NULL AND ac.type_name = 'Unproductive' THEN EXTRACT(EPOCH FROM (LEAST(au.eff_end, b.bucket_start + $5::interval) - GREATEST(au.eff_start, b.bucket_start))) ELSE 0 END), 0) AS unproductive,
		COALESCE(SUM(CASE WHEN au.app_display_name IS NOT NULL AND (ac.type_name = 'Neutral' OR ac.type_name IS NULL) THEN EXTRACT(EPOCH FROM (LEAST(au.eff_end, b.bucket_start + $5::interval) - GREATEST(au.eff_start, b.bucket_start))) ELSE 0 END), 0) AS neutral
	FROM buckets b
	LEFT JOIN app_usage au ON au.eff_start < b.bucket_start + $5::interval AND au.eff_end > b.bucket_start
	LEFT JOIN app_cat ac ON ac.id = au.installed_app_id
	GROUP BY b.bucket_start
	ORDER BY b.bucket_start
	`

	rows, err := r.pool.Query(ctx, chartQuery,
		params.EmployeeID, params.DateFrom, params.DateTo,
		bucketField, bucketSize, bucketFormat,
	)
	if err != nil {
		return nil, fmt.Errorf("hours insights chart query: %w", err)
	}
	defer rows.Close()

	bucketOrder := make([]string, 0)
	for rows.Next() {
		var b HoursInsightsChartBucket
		if err := rows.Scan(&b.Bucket, &b.Productive, &b.Unproductive, &b.Neutral); err != nil {
			return nil, fmt.Errorf("scan chart row: %w", err)
		}
		result.Chart = append(result.Chart, b)
		bucketOrder = append(bucketOrder, b.Bucket)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}

	// 4) Chart data — Individual application buckets
	appChartQuery := `
	WITH params AS (
		SELECT $1::varchar AS emp_id, $2::timestamptz AS from_ts, $3::timestamptz AS to_ts
	),
	app_usage AS (
		SELECT
			s.app_display_name,
			GREATEST(s.started_at, (SELECT from_ts FROM params)) AS eff_start,
			LEAST(
				CASE 
					WHEN s.ended_at IS NOT NULL THEN s.ended_at
					WHEN s.status = 'ACTIVE' AND s.last_sync_at > NOW() - INTERVAL '10 minutes' THEN LEAST(NOW(), (SELECT to_ts FROM params))
					ELSE COALESCE(s.last_activity_at, s.last_sync_at, s.started_at)
				END,
				(SELECT to_ts FROM params)
			) AS eff_end
		FROM app_sessions s
		WHERE s.employee_id = (SELECT emp_id FROM params)
			AND s.deleted_at IS NULL
			AND s.started_at < (SELECT to_ts FROM params)
			AND COALESCE(s.ended_at, s.last_sync_at, s.last_activity_at, s.started_at) > (SELECT from_ts FROM params)
			AND COALESCE(s.ended_at, s.last_sync_at, s.last_activity_at, s.started_at) > s.started_at
	),
	buckets AS (
		SELECT generate_series(
			date_trunc($4, (SELECT from_ts FROM params)),
			date_trunc($4, (SELECT to_ts FROM params)),
			$5::interval
		) AS bucket_start
	)
	SELECT
		to_char(b.bucket_start, $6) AS bucket,
		au.app_display_name,
		COALESCE(SUM(EXTRACT(EPOCH FROM (
			LEAST(au.eff_end, b.bucket_start + $5::interval) -
			GREATEST(au.eff_start, b.bucket_start)
		))), 0) AS duration_sec
	FROM buckets b
	JOIN app_usage au ON au.eff_start < b.bucket_start + $5::interval AND au.eff_end > b.bucket_start
	WHERE au.app_display_name <> '' AND au.app_display_name IS NOT NULL
	GROUP BY b.bucket_start, au.app_display_name
	ORDER BY b.bucket_start
	`

	appRows, err := r.pool.Query(ctx, appChartQuery,
		params.EmployeeID, params.DateFrom, params.DateTo,
		bucketField, bucketSize, bucketFormat,
	)
	if err != nil {
		return nil, fmt.Errorf("hours insights app chart query: %w", err)
	}
	defer appRows.Close()

	appBucketMap := make(map[string]map[string]float64)
	for appRows.Next() {
		var bStr, appName string
		var dur float64
		if err := appRows.Scan(&bStr, &appName, &dur); err != nil {
			return nil, fmt.Errorf("scan app chart row: %w", err)
		}
		if _, ok := appBucketMap[bStr]; !ok {
			appBucketMap[bStr] = make(map[string]float64)
		}
		if dur > maxBucketSeconds {
			dur = maxBucketSeconds
		}
		if topAppNamesMap[appName] {
			appBucketMap[bStr][appName] += dur
		} else {
			appBucketMap[bStr]["Other"] += dur
		}
	}
	if err := appRows.Err(); err != nil {
		return nil, err
	}

	// Populate AppChart in matching bucket order
	for _, bStr := range bucketOrder {
		apps := appBucketMap[bStr]
		if apps == nil {
			apps = make(map[string]float64)
		}
		result.AppChart = append(result.AppChart, HoursInsightsAppBucket{
			Bucket: bStr,
			Apps:   apps,
		})
	}

	return result, nil
}

// parseFloat is a tiny helper for JSON number extraction.
func parseFloat(v interface{}) float64 {
	switch n := v.(type) {
	case float64:
		return n
	case string:
		if f, err := strconv.ParseFloat(n, 64); err == nil {
			return f
		}
	}
	return 0
}
