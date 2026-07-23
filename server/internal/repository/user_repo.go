package repository

import (
	"context"
	"fmt"
	"strings"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/alpha-ai-tracker/server/internal/models"
)

// UserRepo handles database operations for users.
type UserRepo struct {
	pool *pgxpool.Pool
}

// NewUserRepo creates a new UserRepo.
func NewUserRepo(pool *pgxpool.Pool) *UserRepo {
	return &UserRepo{pool: pool}
}

// ListParams defines query parameters for listing users.
type ListParams struct {
	Search     string
	Department string
	Role       string
	Status     string // "tracked" or "untracked"
	Page       int
	PerPage    int
}

// ListResult holds paginated results.
type ListResult struct {
	Users      []models.User
	Total      int
	Page       int
	PerPage    int
	TotalPages int
}

// List returns a paginated, filtered list of users.
func (r *UserRepo) List(ctx context.Context, params ListParams) (*ListResult, error) {
	if params.Page < 1 {
		params.Page = 1
	}
	if params.PerPage < 1 || params.PerPage > 100 {
		params.PerPage = 10
	}

	var conditions []string
	var args []interface{}
	argIdx := 1

	if params.Search != "" {
		conditions = append(conditions, fmt.Sprintf("(LOWER(name) LIKE LOWER($%d) OR LOWER(email) LIKE LOWER($%d) OR LOWER(employee_id) LIKE LOWER($%d))", argIdx, argIdx, argIdx))
		args = append(args, "%"+params.Search+"%")
		argIdx++
	}
	if params.Department != "" {
		conditions = append(conditions, fmt.Sprintf("department = $%d", argIdx))
		args = append(args, params.Department)
		argIdx++
	}
	if params.Role != "" {
		conditions = append(conditions, fmt.Sprintf("role = $%d", argIdx))
		args = append(args, params.Role)
		argIdx++
	}
	if params.Status == "tracked" {
		conditions = append(conditions, "tracking_status = 'tracked'")
	} else if params.Status == "untracked" {
		conditions = append(conditions, "tracking_status = 'untracked'")
	}

	whereClause := ""
	if len(conditions) > 0 {
		whereClause = "WHERE " + strings.Join(conditions, " AND ")
	}

	// Count total
	countQuery := fmt.Sprintf("SELECT COUNT(*) FROM users %s", whereClause)
	var total int
	if err := r.pool.QueryRow(ctx, countQuery, args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count users: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	totalPages := (total + params.PerPage - 1) / params.PerPage

	// Fetch page
	query := fmt.Sprintf(`
		SELECT id, employee_id, name, email, password_hash, role, department, shift,
		       tracking_enabled, tracking_status, is_online, avatar, avatar_color,
		       is_company_admin, created_at, updated_at
		FROM users %s
		ORDER BY created_at DESC
		LIMIT $%d OFFSET $%d
	`, whereClause, argIdx, argIdx+1)
	args = append(args, params.PerPage, offset)

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list users: %w", err)
	}
	defer rows.Close()

	users, err := pgx.CollectRows(rows, pgx.RowToStructByName[models.User])
	if err != nil {
		return nil, fmt.Errorf("collect users: %w", err)
	}

	return &ListResult{
		Users:      users,
		Total:      total,
		Page:       params.Page,
		PerPage:    params.PerPage,
		TotalPages: totalPages,
	}, nil
}

// getByID returns a user by their UUID (internal helper).
func (r *UserRepo) getByID(ctx context.Context, query string, args ...interface{}) (*models.User, error) {
	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("query user: %w", err)
	}
	defer rows.Close()

	user, err := pgx.CollectOneRow(rows, pgx.RowToStructByName[models.User])
	if err != nil {
		if err == pgx.ErrNoRows {
			return nil, nil
		}
		return nil, fmt.Errorf("collect user: %w", err)
	}
	return &user, nil
}

// GetByID returns a user by their UUID.
func (r *UserRepo) GetByID(ctx context.Context, id string) (*models.User, error) {
	return r.getByID(ctx, `
		SELECT id, employee_id, name, email, password_hash, role, department, shift,
		       tracking_enabled, tracking_status, is_online, avatar, avatar_color,
		       is_company_admin, created_at, updated_at
		FROM users WHERE id = $1
	`, id)
}

// GetByEmail returns a user by their email.
func (r *UserRepo) GetByEmail(ctx context.Context, email string) (*models.User, error) {
	return r.getByID(ctx, `
		SELECT id, employee_id, name, email, password_hash, role, department, shift,
		       tracking_enabled, tracking_status, is_online, avatar, avatar_color,
		       is_company_admin, created_at, updated_at
		FROM users WHERE email = $1
	`, email)
}

// Create inserts a new user and returns it.
func (r *UserRepo) Create(ctx context.Context, u *models.User) (*models.User, error) {
	query := `
		INSERT INTO users (name, email, password_hash, role, department, shift,
		                   tracking_enabled, tracking_status, is_online, is_company_admin)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
		RETURNING id, employee_id, name, email, password_hash, role, department, shift,
		          tracking_enabled, tracking_status, is_online, avatar, avatar_color,
		          is_company_admin, created_at, updated_at
	`
	return r.getByID(ctx, query,
		u.Name, u.Email, u.PasswordHash, u.Role, u.Department, u.Shift,
		u.TrackingEnabled, u.TrackingStatus, u.IsOnline, u.IsCompanyAdmin,
	)
}

// Update partially updates a user and returns the updated record.
func (r *UserRepo) Update(ctx context.Context, id string, updates map[string]interface{}) (*models.User, error) {
	if len(updates) == 0 {
		return r.GetByID(ctx, id)
	}

	var setClauses []string
	var args []interface{}
	argIdx := 1
	args = append(args, id)

	allowedFields := map[string]string{
		"name": "name", "email": "email", "department": "department",
		"role": "role", "shift": "shift", "tracking_enabled": "tracking_enabled",
		"tracking_status": "tracking_status", "is_online": "is_online",
	}

	for field, dbCol := range allowedFields {
		if val, ok := updates[field]; ok {
			setClauses = append(setClauses, fmt.Sprintf("%s = $%d", dbCol, argIdx+1))
			args = append(args, val)
			argIdx++
		}
	}

	if len(setClauses) == 0 {
		return r.GetByID(ctx, id)
	}

	setClauses = append(setClauses, "updated_at = NOW()")

	query := fmt.Sprintf(`
		UPDATE users SET %s
		WHERE id = $1
		RETURNING id, employee_id, name, email, password_hash, role, department, shift,
		          tracking_enabled, tracking_status, is_online, avatar, avatar_color,
		          is_company_admin, created_at, updated_at
	`, strings.Join(setClauses, ", "))

	return r.getByID(ctx, query, args...)
}

// Delete removes a user by ID.
func (r *UserRepo) Delete(ctx context.Context, id string) error {
	tag, err := r.pool.Exec(ctx, "DELETE FROM users WHERE id = $1", id)
	if err != nil {
		return fmt.Errorf("delete user: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("user not found")
	}
	return nil
}

// CountCompanyAdmins returns the number of company_admin users.
func (r *UserRepo) CountCompanyAdmins(ctx context.Context) (int, error) {
	var count int
	err := r.pool.QueryRow(ctx, "SELECT COUNT(*) FROM users WHERE is_company_admin = true").Scan(&count)
	if err != nil {
		return 0, fmt.Errorf("count company admins: %w", err)
	}
	return count, nil
}

// IsUniqueEmail checks if an email is already taken (excluding a given user ID).
func (r *UserRepo) IsUniqueEmail(ctx context.Context, email, excludeID string) (bool, error) {
	var exists bool
	var err error
	if excludeID != "" {
		err = r.pool.QueryRow(ctx, "SELECT EXISTS(SELECT 1 FROM users WHERE email = $1 AND id != $2)", email, excludeID).Scan(&exists)
	} else {
		err = r.pool.QueryRow(ctx, "SELECT EXISTS(SELECT 1 FROM users WHERE email = $1)", email).Scan(&exists)
	}
	if err != nil {
		return false, fmt.Errorf("check email uniqueness: %w", err)
	}
	return !exists, nil
}

// EnsureCompanyAdminExists is a helper type for the service layer
type CompanyAdminCheck struct {
	Exists bool
}
