package repository

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/alpha-ai-tracker/server/internal/models"
)

// ActivityLogRepo handles database operations for activity logs.
type ActivityLogRepo struct {
	pool *pgxpool.Pool
}

// NewActivityLogRepo creates a new ActivityLogRepo.
func NewActivityLogRepo(pool *pgxpool.Pool) *ActivityLogRepo {
	return &ActivityLogRepo{pool: pool}
}

// BulkInsert inserts a batch of activity logs and returns the count inserted.
func (r *ActivityLogRepo) BulkInsert(ctx context.Context, logs []models.ActivityLog) (int, error) {
	if len(logs) == 0 {
		return 0, nil
	}

	// Use batch insert with COPY for efficiency
	// For simplicity, use INSERT with multiple rows
	batchSize := 100
	inserted := 0

	for i := 0; i < len(logs); i += batchSize {
		end := i + batchSize
		if end > len(logs) {
			end = len(logs)
		}
		batch := logs[i:end]

		valueStrings := make([]string, 0, len(batch))
		args := make([]interface{}, 0, len(batch)*14)
		argIdx := 1

		for _, log := range batch {
			valueStrings = append(valueStrings, fmt.Sprintf(
				"($%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d, $%d)",
				argIdx, argIdx+1, argIdx+2, argIdx+3, argIdx+4,
				argIdx+5, argIdx+6, argIdx+7, argIdx+8, argIdx+9,
				argIdx+10, argIdx+11, argIdx+12, argIdx+13,
			))
			args = append(args,
				log.ID, log.EmployeeID, log.MachineID, log.Timestamp,
				log.ProcessName, log.WindowTitle, log.ProcessID,
				log.CPUPercent, log.MemoryBytes, log.IsForeground,
				log.UserName, log.Platform, log.SessionID, log.EmployeeName,
			)
			argIdx += 14
		}

		query := fmt.Sprintf(`
			INSERT INTO activity_logs
				(id, employee_id, machine_id, timestamp, process_name, window_title,
				 process_id, cpu_percent, memory_bytes, is_foreground,
				 user_name, platform, session_id, employee_name)
			VALUES %s
			ON CONFLICT (id, employee_id) DO NOTHING
		`, strings.Join(valueStrings, ", "))

		tag, err := r.pool.Exec(ctx, query, args...)
		if err != nil {
			return inserted, fmt.Errorf("bulk insert activity logs: %w", err)
		}
		inserted += int(tag.RowsAffected())
	}

	return inserted, nil
}

// ListParams for filtering activity logs.
type ActivityLogListParams struct {
	EmployeeID string
	Search     string
	Platform   string
	Foreground *bool
	StartDate  *time.Time
	EndDate    *time.Time
	Page       int
	PerPage    int
}

// ListResult holds paginated activity log results.
type ActivityLogListResult struct {
	Logs       []models.ActivityLog
	Total      int
	Page       int
	PerPage    int
	TotalPages int
}

// List returns a paginated, filtered list of activity logs.
func (r *ActivityLogRepo) List(ctx context.Context, params ActivityLogListParams) (*ActivityLogListResult, error) {
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
			"(LOWER(process_name) LIKE LOWER($%d) OR LOWER(window_title) LIKE LOWER($%d))",
			argIdx, argIdx))
		args = append(args, "%"+params.Search+"%")
		argIdx++
	}
	if params.Platform != "" {
		conditions = append(conditions, fmt.Sprintf("platform = $%d", argIdx))
		args = append(args, params.Platform)
		argIdx++
	}
	if params.Foreground != nil {
		conditions = append(conditions, fmt.Sprintf("is_foreground = $%d", argIdx))
		args = append(args, *params.Foreground)
		argIdx++
	}
	if params.StartDate != nil {
		conditions = append(conditions, fmt.Sprintf("timestamp >= $%d", argIdx))
		args = append(args, *params.StartDate)
		argIdx++
	}
	if params.EndDate != nil {
		conditions = append(conditions, fmt.Sprintf("timestamp <= $%d", argIdx))
		args = append(args, *params.EndDate)
		argIdx++
	}

	whereClause := ""
	if len(conditions) > 0 {
		whereClause = "WHERE " + strings.Join(conditions, " AND ")
	}

	// Count total
	countQuery := fmt.Sprintf("SELECT COUNT(*) FROM activity_logs %s", whereClause)
	var total int
	if err := r.pool.QueryRow(ctx, countQuery, args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count activity logs: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	totalPages := (total + params.PerPage - 1) / params.PerPage

	// Fetch page
	query := fmt.Sprintf(`
		SELECT id, employee_id, machine_id, timestamp, process_name, window_title,
		       process_id, cpu_percent, memory_bytes, is_foreground,
		       user_name, platform, session_id, employee_name, synced_at, created_at
		FROM activity_logs %s
		ORDER BY timestamp DESC
		LIMIT $%d OFFSET $%d
	`, whereClause, argIdx, argIdx+1)
	args = append(args, params.PerPage, offset)

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list activity logs: %w", err)
	}
	defer rows.Close()

	var logs []models.ActivityLog
	for rows.Next() {
		var l models.ActivityLog
		if err := rows.Scan(
			&l.ID, &l.EmployeeID, &l.MachineID, &l.Timestamp,
			&l.ProcessName, &l.WindowTitle,
			&l.ProcessID, &l.CPUPercent, &l.MemoryBytes, &l.IsForeground,
			&l.UserName, &l.Platform, &l.SessionID, &l.EmployeeName,
			&l.SyncedAt, &l.CreatedAt,
		); err != nil {
			return nil, fmt.Errorf("scan activity log row: %w", err)
		}
		logs = append(logs, l)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate activity logs: %w", err)
	}

	return &ActivityLogListResult{
		Logs:       logs,
		Total:      total,
		Page:       params.Page,
		PerPage:    params.PerPage,
		TotalPages: totalPages,
	}, nil
}
