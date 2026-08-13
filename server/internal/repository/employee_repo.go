package repository

import (
	"context"
	"errors"
	"fmt"
	"strings"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/alpha-ai-tracker/server/internal/models"
)

// EmployeeRepo handles database operations for employees.
type EmployeeRepo struct {
	pool *pgxpool.Pool
}

// NewEmployeeRepo creates a new EmployeeRepo.
func NewEmployeeRepo(pool *pgxpool.Pool) *EmployeeRepo {
	return &EmployeeRepo{pool: pool}
}

// ListParams defines query parameters for listing employees.
type EmployeeListParams struct {
	Search     string
	Department string
	Status     string // "tracked" or "untracked"
	Page       int
	PerPage    int
}

// EmployeeListResult holds paginated results.
type EmployeeListResult struct {
	Employees  []models.Employee
	Total      int
	Page       int
	PerPage    int
	TotalPages int
}

// List returns a paginated, filtered list of employees.
func (r *EmployeeRepo) List(ctx context.Context, params EmployeeListParams) (*EmployeeListResult, error) {
	if params.Page < 1 {
		params.Page = 1
	}
	if params.PerPage < 1 || params.PerPage > 100 {
		params.PerPage = 10
	}

	var conditions []string
	var args []interface{}
	argIdx := 1

	conditions = append(conditions, "e.deleted_at IS NULL")

	if params.Search != "" {
		conditions = append(conditions, fmt.Sprintf("(LOWER(e.name) LIKE LOWER($%d) OR LOWER(e.email) LIKE LOWER($%d) OR LOWER(e.employee_id) LIKE LOWER($%d))", argIdx, argIdx, argIdx))
		args = append(args, "%"+params.Search+"%")
		argIdx++
	}
	if params.Department != "" {
		conditions = append(conditions, fmt.Sprintf("department = $%d", argIdx))
		args = append(args, params.Department)
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
	countQuery := fmt.Sprintf("SELECT COUNT(*) FROM employees e %s", whereClause)
	var total int
	if err := r.pool.QueryRow(ctx, countQuery, args...).Scan(&total); err != nil {
		return nil, fmt.Errorf("count employees: %w", err)
	}

	offset := (params.Page - 1) * params.PerPage
	totalPages := (total + params.PerPage - 1) / params.PerPage

	// Fetch page — use manual scan to avoid RowToStructByName issues with nullable columns
	query := fmt.Sprintf(`
		SELECT e.id, e.employee_id, e.name, e.email,
		       COALESCE(d.name, e.department) AS department,
		       e.department_id, e.shift,
		       e.tracking_enabled, e.tracking_status, e.is_online,
		           COALESCE(e.avatar, '') AS avatar, COALESCE(e.avatar_color, '') AS avatar_color,
		       e.created_at, e.updated_at, e.deleted_at
		FROM employees e
		LEFT JOIN departments d ON e.department_id = d.id
		%s
		ORDER BY e.created_at DESC
		LIMIT $%d OFFSET $%d
	`, whereClause, argIdx, argIdx+1)
	args = append(args, params.PerPage, offset)

	rows, err := r.pool.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("list employees: %w", err)
	}
	defer rows.Close()

	var employees []models.Employee
	for rows.Next() {
		var e models.Employee
		if err := rows.Scan(
			&e.ID, &e.EmployeeID, &e.Name, &e.Email,
			&e.Department, &e.DepartmentID, &e.Shift,
			&e.TrackingEnabled, &e.TrackingStatus, &e.IsOnline,
			&e.Avatar, &e.AvatarColor,
			&e.CreatedAt, &e.UpdatedAt, &e.DeletedAt,
		); err != nil {
			return nil, fmt.Errorf("scan employee row: %w", err)
		}
		employees = append(employees, e)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate employees: %w", err)
	}

	return &EmployeeListResult{
		Employees:  employees,
		Total:      total,
		Page:       params.Page,
		PerPage:    params.PerPage,
		TotalPages: totalPages,
	}, nil
}

// getByID is an internal helper to fetch a single employee row.
func (r *EmployeeRepo) getByID(ctx context.Context, query string, args ...interface{}) (*models.Employee, error) {
	return execGetByID(ctx, r.pool, query, args...)
}

// GetByID returns an employee by UUID.
func (r *EmployeeRepo) GetByID(ctx context.Context, id string) (*models.Employee, error) {
	return r.getByID(ctx, `
		SELECT e.id, e.employee_id, e.name, e.email,
		       COALESCE(d.name, e.department) AS department,
		       e.department_id, e.shift,
		       e.tracking_enabled, e.tracking_status, e.is_online,
		           COALESCE(e.avatar, '') AS avatar, COALESCE(e.avatar_color, '') AS avatar_color,
		       e.created_at, e.updated_at, e.deleted_at
		FROM employees e
		LEFT JOIN departments d ON e.department_id = d.id
		WHERE e.id = $1 AND e.deleted_at IS NULL
	`, id)
}

// GetByEmployeeID returns an employee by their employee ID (EMP-XXXXX).
func (r *EmployeeRepo) GetByEmployeeID(ctx context.Context, employeeID string) (*models.Employee, error) {
	return r.getByID(ctx, `
		SELECT e.id, e.employee_id, e.name, e.email,
		       COALESCE(d.name, e.department) AS department,
		       e.department_id, e.shift,
		       e.tracking_enabled, e.tracking_status, e.is_online,
		           COALESCE(e.avatar, '') AS avatar, COALESCE(e.avatar_color, '') AS avatar_color,
		       e.created_at, e.updated_at, e.deleted_at
		FROM employees e
		LEFT JOIN departments d ON e.department_id = d.id
		WHERE e.employee_id = $1 AND e.deleted_at IS NULL
	`, employeeID)
}

// GetByEmail returns an employee by email.
func (r *EmployeeRepo) GetByEmail(ctx context.Context, email string) (*models.Employee, error) {
	return r.getByID(ctx, `
		SELECT e.id, e.employee_id, e.name, e.email,
		       COALESCE(d.name, e.department) AS department,
		       e.department_id, e.shift,
		       e.tracking_enabled, e.tracking_status, e.is_online,
		           COALESCE(e.avatar, '') AS avatar, COALESCE(e.avatar_color, '') AS avatar_color,
		       e.created_at, e.updated_at, e.deleted_at
		FROM employees e
		LEFT JOIN departments d ON e.department_id = d.id
		WHERE e.email = $1 AND e.deleted_at IS NULL
	`, email)
}

// Create inserts a new employee and returns it.
// Uses a transaction for atomicity.
func (r *EmployeeRepo) Create(ctx context.Context, e *models.Employee) (*models.Employee, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	query := `
		INSERT INTO employees (employee_id, name, email, department, department_id, shift,
		                       tracking_enabled, tracking_status, is_online)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
		RETURNING id, employee_id, name, email,
		          COALESCE((SELECT name FROM departments WHERE id = $5), $4) AS department,
		          $5 AS department_id, shift,
		          tracking_enabled, tracking_status, is_online,
		          COALESCE(avatar, '') AS avatar, COALESCE(avatar_color, '') AS avatar_color,
		          created_at, updated_at, deleted_at
	`
	emp, err := execGetByID(ctx, tx, query,
		e.EmployeeID, e.Name, e.Email, e.Department, e.DepartmentID, e.Shift,
		e.TrackingEnabled, e.TrackingStatus, e.IsOnline,
	)
	if err != nil {
		// Check for duplicate key violations
		if isDuplicateKeyError(err) {
			return nil, fmt.Errorf("duplicate employee record: %w", err)
		}
		return nil, err
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit tx: %w", err)
	}
	return emp, nil
}

// Update partially updates an employee and returns the updated record.
// Uses a transaction for atomicity.
func (r *EmployeeRepo) Update(ctx context.Context, id string, updates map[string]interface{}) (*models.Employee, error) {
	if len(updates) == 0 {
		return r.GetByID(ctx, id)
	}

	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	var setClauses []string
	var args []interface{}
	argIdx := 1
	args = append(args, id)

	allowedFields := map[string]string{
		"name": "name", "email": "email", "department": "department",
		"department_id": "department_id",
		"shift": "shift", "tracking_enabled": "tracking_enabled",
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
		UPDATE employees SET %s
		WHERE id = $1 AND deleted_at IS NULL
		RETURNING id, employee_id, name, email,
		          COALESCE((SELECT name FROM departments WHERE id = employees.department_id), department) AS department,
		          department_id, shift,
		          tracking_enabled, tracking_status, is_online,
		          COALESCE(avatar, '') AS avatar, COALESCE(avatar_color, '') AS avatar_color,
		          created_at, updated_at, deleted_at
	`, strings.Join(setClauses, ", "))

	emp, err := execGetByID(ctx, tx, query, args...)
	if err != nil {
		return nil, err
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit tx: %w", err)
	}
	return emp, nil
}

// Delete removes an employee by ID.
// Uses a transaction for atomicity.
func (r *EmployeeRepo) Delete(ctx context.Context, id string) error {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	tag, err := tx.Exec(ctx, "UPDATE employees SET deleted_at = NOW() WHERE id = $1 AND deleted_at IS NULL", id)
	if err != nil {
		return fmt.Errorf("delete employee: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("employee not found")
	}

	if err := tx.Commit(ctx); err != nil {
		return fmt.Errorf("commit tx: %w", err)
	}
	return nil
}

// GetDepartments returns all department names.
func (r *EmployeeRepo) GetDepartments(ctx context.Context) ([]string, error) {
	rows, err := r.pool.Query(ctx, "SELECT name FROM departments WHERE deleted_at IS NULL ORDER BY name")
	if err != nil {
		return nil, fmt.Errorf("get departments: %w", err)
	}
	defer rows.Close()

	var depts []string
	for rows.Next() {
		var d string
		if err := rows.Scan(&d); err != nil {
			return nil, fmt.Errorf("scan department: %w", err)
		}
		depts = append(depts, d)
	}
	return depts, nil
}

// ────────────────────────────────
// Transaction-aware helpers
// ────────────────────────────────

// isDuplicateKeyError checks if an error is a PostgreSQL unique constraint violation.
func isDuplicateKeyError(err error) bool {
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) {
		return pgErr.Code == "23505"
	}
	return false
}

// scanEmployeeRow scans a single employee row using manual field scan.
// Uses manual scanning to avoid RowToStructByName issues with nullable columns.
// Note: rows are closed by CollectOneRow internally.
func scanEmployeeRow(rows pgx.Rows) (*models.Employee, error) {
	emp, err := pgx.CollectOneRow(rows, func(row pgx.CollectableRow) (models.Employee, error) {
		var e models.Employee
		err := row.Scan(
			&e.ID, &e.EmployeeID, &e.Name, &e.Email,
			&e.Department, &e.DepartmentID, &e.Shift,
			&e.TrackingEnabled, &e.TrackingStatus, &e.IsOnline,
			&e.Avatar, &e.AvatarColor,
			&e.CreatedAt, &e.UpdatedAt, &e.DeletedAt,
		)
		return e, err
	})
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, nil
		}
		return nil, fmt.Errorf("scan employee: %w", err)
	}
	return &emp, nil
}

// execGetByID executes a query against a transaction and scans a single employee row.
type queryable interface {
	Query(ctx context.Context, sql string, args ...interface{}) (pgx.Rows, error)
}

func execGetByID(ctx context.Context, q queryable, query string, args ...interface{}) (*models.Employee, error) {
	rows, err := q.Query(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("query employee: %w", err)
	}
	return scanEmployeeRow(rows)
}

// GenerateEmployeeID generates the next employee ID in EMP-XXXXX format.
func (r *EmployeeRepo) GenerateEmployeeID(ctx context.Context) (string, error) {
	var id string
	err := r.pool.QueryRow(ctx, "SELECT 'EMP-' || LPAD(NEXTVAL('employee_id_seq')::TEXT, 5, '0')").Scan(&id)
	if err != nil {
		return "", fmt.Errorf("generate employee id: %w", err)
	}
	return id, nil
}
