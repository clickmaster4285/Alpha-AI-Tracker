package repository

import (
	"context"
	"fmt"
	"math"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type LiveViewRepo struct {
	pool *pgxpool.Pool
}

func NewLiveViewRepo(pool *pgxpool.Pool) *LiveViewRepo {
	return &LiveViewRepo{pool: pool}
}

func (r *LiveViewRepo) Create(ctx context.Context, employeeID, userID, reason, roomName string, expiresAt time.Time) (*dto.LiveViewSessionResponse, error) {
	var out dto.LiveViewSessionResponse
	err := r.pool.QueryRow(ctx, `
		INSERT INTO live_view_sessions
		    (employee_id, requested_by_user_id, reason, room_name, expires_at)
		VALUES ($1, $2, $3, $4, $5)
		RETURNING id::text, employee_id, requested_by_user_id::text, status, reason,
		          room_name, requested_at, approved_at, started_at, ended_at, expires_at,
		          COALESCE(end_reason, '')
	`, employeeID, userID, reason, roomName, expiresAt).Scan(
		&out.ID, &out.EmployeeID, &out.RequestedBy, &out.Status, &out.Reason,
		&out.RoomName, &out.RequestedAt, &out.ApprovedAt, &out.StartedAt,
		&out.EndedAt, &out.ExpiresAt, &out.EndReason,
	)
	if err != nil {
		return nil, fmt.Errorf("create live view session: %w", err)
	}
	return &out, nil
}

func (r *LiveViewRepo) Get(ctx context.Context, id string) (*dto.LiveViewSessionResponse, error) {
	var out dto.LiveViewSessionResponse
	err := r.pool.QueryRow(ctx, `
		SELECT id::text, employee_id, requested_by_user_id::text, status, reason,
		       room_name, requested_at, approved_at, started_at, ended_at, expires_at,
		       COALESCE(end_reason, '')
		FROM live_view_sessions
		WHERE id = $1
	`, id).Scan(
		&out.ID, &out.EmployeeID, &out.RequestedBy, &out.Status, &out.Reason,
		&out.RoomName, &out.RequestedAt, &out.ApprovedAt, &out.StartedAt,
		&out.EndedAt, &out.ExpiresAt, &out.EndReason,
	)
	if err != nil {
		if err == pgx.ErrNoRows {
			return nil, nil
		}
		return nil, fmt.Errorf("get live view session: %w", err)
	}
	return &out, nil
}

func (r *LiveViewRepo) List(ctx context.Context, page, perPage int) (*dto.LiveViewSessionListResponse, error) {
	if page < 1 {
		page = 1
	}
	if perPage < 1 || perPage > 100 {
		perPage = 20
	}
	var total int
	if err := r.pool.QueryRow(ctx, `SELECT COUNT(*) FROM live_view_sessions`).Scan(&total); err != nil {
		return nil, fmt.Errorf("count live view sessions: %w", err)
	}
	rows, err := r.pool.Query(ctx, `
		SELECT id::text, employee_id, requested_by_user_id::text, status, reason,
		       room_name, requested_at, approved_at, started_at, ended_at, expires_at,
		       COALESCE(end_reason, '')
		FROM live_view_sessions
		ORDER BY requested_at DESC
		LIMIT $1 OFFSET $2
	`, perPage, (page-1)*perPage)
	if err != nil {
		return nil, fmt.Errorf("list live view sessions: %w", err)
	}
	defer rows.Close()
	data := make([]dto.LiveViewSessionResponse, 0)
	for rows.Next() {
		var item dto.LiveViewSessionResponse
		if err := rows.Scan(&item.ID, &item.EmployeeID, &item.RequestedBy, &item.Status,
			&item.Reason, &item.RoomName, &item.RequestedAt, &item.ApprovedAt,
			&item.StartedAt, &item.EndedAt, &item.ExpiresAt, &item.EndReason); err != nil {
			return nil, fmt.Errorf("scan live view session: %w", err)
		}
		data = append(data, item)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate live view sessions: %w", err)
	}
	return &dto.LiveViewSessionListResponse{
		Data: data, Total: total, Page: page, PerPage: perPage,
		TotalPages: int(math.Ceil(float64(total) / float64(perPage))),
	}, nil
}

func (r *LiveViewRepo) Stop(ctx context.Context, id, reason string, status string) (*dto.LiveViewSessionResponse, error) {
	var out dto.LiveViewSessionResponse
	err := r.pool.QueryRow(ctx, `
		UPDATE live_view_sessions
		SET status = $2, ended_at = NOW(), end_reason = $3, updated_at = NOW()
		WHERE id = $1
		  AND status IN ('REQUESTED', 'APPROVED', 'STARTING', 'ACTIVE', 'STOPPING')
		RETURNING id::text, employee_id, requested_by_user_id::text, status, reason,
		          room_name, requested_at, approved_at, started_at, ended_at, expires_at,
		          COALESCE(end_reason, '')
	`, id, status, reason).Scan(
		&out.ID, &out.EmployeeID, &out.RequestedBy, &out.Status, &out.Reason,
		&out.RoomName, &out.RequestedAt, &out.ApprovedAt, &out.StartedAt,
		&out.EndedAt, &out.ExpiresAt, &out.EndReason,
	)
	if err != nil {
		if err == pgx.ErrNoRows {
			return nil, nil
		}
		return nil, fmt.Errorf("stop live view session: %w", err)
	}
	return &out, nil
}

func (r *LiveViewRepo) Expire(ctx context.Context, now time.Time) (int64, error) {
	result, err := r.pool.Exec(ctx, `
		UPDATE live_view_sessions
		SET status = 'EXPIRED', ended_at = $1, end_reason = 'lease expired', updated_at = $1
		WHERE expires_at <= $1
		  AND status IN ('REQUESTED', 'APPROVED', 'STARTING', 'ACTIVE', 'STOPPING')
	`, now)
	if err != nil {
		return 0, fmt.Errorf("expire live view sessions: %w", err)
	}
	return result.RowsAffected(), nil
}

func (r *LiveViewRepo) Audit(ctx context.Context, sessionID, actorUserID, employeeID, action, outcome, reason, correlationID string) error {
	_, err := r.pool.Exec(ctx, `
		INSERT INTO live_view_audit_events
		    (session_id, actor_user_id, employee_id, action, outcome, reason, correlation_id)
		VALUES (NULLIF($1, '')::uuid, NULLIF($2, '')::uuid, NULLIF($3, ''), $4, $5, NULLIF($6, ''), NULLIF($7, ''))
	`, sessionID, actorUserID, employeeID, action, outcome, reason, correlationID)
	if err != nil {
		return fmt.Errorf("write live view audit event: %w", err)
	}
	return nil
}
