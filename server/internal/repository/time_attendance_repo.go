package repository

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type ScheduleRecord struct {
	ShiftID      int
	StartTime    string
	EndTime      string
	WorkingDays  string
	Timezone     string
	GraceMinutes int
	ValidFrom    time.Time
}

type HolidayRecord struct {
	ID    int
	Date  time.Time
	Label string
}

type TimeAttendanceRepo struct {
	pool *pgxpool.Pool
}

func NewTimeAttendanceRepo(pool *pgxpool.Pool) *TimeAttendanceRepo {
	return &TimeAttendanceRepo{pool: pool}
}

func (r *TimeAttendanceRepo) GetScheduleForEmployee(ctx context.Context, employeeID string) (*ScheduleRecord, error) {
	var row ScheduleRecord
	err := r.pool.QueryRow(ctx, `
		SELECT s.id, s.start_time::TEXT, s.end_time::TEXT,
		       s.working_days, s.timezone, s.grace_minutes, e.created_at
		FROM employees e
		JOIN shifts s ON s.id = e.shift_id AND s.deleted_at IS NULL
		WHERE e.employee_id = $1 AND e.deleted_at IS NULL
	`, employeeID).Scan(
		&row.ShiftID, &row.StartTime, &row.EndTime, &row.WorkingDays,
		&row.Timezone, &row.GraceMinutes, &row.ValidFrom,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("get employee schedule: %w", err)
	}
	return &row, nil
}

func (r *TimeAttendanceRepo) ListHolidays(ctx context.Context, from, to time.Time) ([]HolidayRecord, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, holiday_date, label
		FROM company_holidays
		WHERE deleted_at IS NULL AND holiday_date >= $1::DATE AND holiday_date <= $2::DATE
		ORDER BY holiday_date ASC
	`, from, to)
	if err != nil {
		return nil, fmt.Errorf("list holidays: %w", err)
	}
	defer rows.Close()

	result := make([]HolidayRecord, 0)
	for rows.Next() {
		var h HolidayRecord
		if err := rows.Scan(&h.ID, &h.Date, &h.Label); err != nil {
			return nil, fmt.Errorf("scan holiday: %w", err)
		}
		result = append(result, h)
	}
	return result, rows.Err()
}

func (r *TimeAttendanceRepo) ListAllHolidays(ctx context.Context) ([]HolidayRecord, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, holiday_date, label
		FROM company_holidays
		WHERE deleted_at IS NULL
		ORDER BY holiday_date ASC
	`)
	if err != nil {
		return nil, fmt.Errorf("list all holidays: %w", err)
	}
	defer rows.Close()

	result := make([]HolidayRecord, 0)
	for rows.Next() {
		var h HolidayRecord
		if err := rows.Scan(&h.ID, &h.Date, &h.Label); err != nil {
			return nil, fmt.Errorf("scan holiday: %w", err)
		}
		result = append(result, h)
	}
	return result, rows.Err()
}

func (r *TimeAttendanceRepo) CreateHoliday(ctx context.Context, date time.Time, label string) (*HolidayRecord, error) {
	var h HolidayRecord
	err := r.pool.QueryRow(ctx, `
		INSERT INTO company_holidays (holiday_date, label)
		VALUES ($1::DATE, $2)
		RETURNING id, holiday_date, label
	`, date, label).Scan(&h.ID, &h.Date, &h.Label)
	if err != nil {
		return nil, fmt.Errorf("create holiday: %w", err)
	}
	return &h, nil
}

func (r *TimeAttendanceRepo) UpdateHoliday(ctx context.Context, id int, date time.Time, label string) (*HolidayRecord, error) {
	var h HolidayRecord
	err := r.pool.QueryRow(ctx, `
		UPDATE company_holidays SET holiday_date = $2::DATE, label = $3
		WHERE id = $1 AND deleted_at IS NULL
		RETURNING id, holiday_date, label
	`, id, date, label).Scan(&h.ID, &h.Date, &h.Label)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("update holiday: %w", err)
	}
	return &h, nil
}

func (r *TimeAttendanceRepo) DeleteHoliday(ctx context.Context, id int) (bool, error) {
	tag, err := r.pool.Exec(ctx, `
		UPDATE company_holidays SET deleted_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL
	`, id)
	if err != nil {
		return false, fmt.Errorf("delete holiday: %w", err)
	}
	return tag.RowsAffected() > 0, nil
}

func (r *TimeAttendanceRepo) ListSessionEvents(
	ctx context.Context, employeeID string, from, to time.Time,
) ([]models.SessionEvent, error) {
	rows, err := r.pool.Query(ctx, `
		WITH prior_state AS (
			SELECT DISTINCT ON (
				CASE WHEN event_type IN ('idle_start', 'idle_end') THEN 'idle' ELSE 'lock' END
			)
			       id, employee_id, event_type, os_username, event_at,
			       event_count, COALESCE(first_at, event_at) AS first_at,
			       COALESCE(last_at, event_at) AS last_at, synced_at, created_at
			FROM session_events
			WHERE employee_id = $1 AND deleted_at IS NULL AND event_at < $2
			  AND event_type IN ('idle_start', 'idle_end', 'screen_lock', 'screen_unlock')
			ORDER BY
				CASE WHEN event_type IN ('idle_start', 'idle_end') THEN 'idle' ELSE 'lock' END,
				event_at DESC
		),
		current_events AS (
			SELECT id, employee_id, event_type, os_username, event_at,
			       event_count, COALESCE(first_at, event_at) AS first_at,
			       COALESCE(last_at, event_at) AS last_at, synced_at, created_at
			FROM session_events
			WHERE employee_id = $1 AND deleted_at IS NULL
			  AND event_at >= $2 AND event_at < $3
		)
		SELECT * FROM (
			SELECT * FROM prior_state
			UNION ALL
			SELECT * FROM current_events
		) attendance_events
		ORDER BY event_at ASC
	`, employeeID, from, to)
	if err != nil {
		return nil, fmt.Errorf("list attendance events: %w", err)
	}
	defer rows.Close()

	result := make([]models.SessionEvent, 0)
	for rows.Next() {
		var e models.SessionEvent
		if err := rows.Scan(
			&e.ID, &e.EmployeeID, &e.EventType, &e.OsUsername, &e.EventAt,
			&e.EventCount, &e.FirstAt, &e.LastAt, &e.SyncedAt, &e.CreatedAt,
		); err != nil {
			return nil, fmt.Errorf("scan attendance event: %w", err)
		}
		result = append(result, e)
	}
	return result, rows.Err()
}

func (r *TimeAttendanceRepo) GetLastHeartbeat(ctx context.Context, employeeID string) (*time.Time, error) {
	var value string
	err := r.pool.QueryRow(ctx, `
		SELECT value FROM app_status WHERE employee_id = $1 AND key = 'last_heartbeat_at'
	`, employeeID).Scan(&value)
	if errors.Is(err, pgx.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("get heartbeat: %w", err)
	}
	parsed, err := time.Parse(time.RFC3339Nano, value)
	if err != nil {
		return nil, nil
	}
	return &parsed, nil
}
