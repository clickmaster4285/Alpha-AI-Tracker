package repository

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type DlpRepo struct {
	pool *pgxpool.Pool
}

func NewDlpRepo(pool *pgxpool.Pool) *DlpRepo {
	return &DlpRepo{pool: pool}
}

// DlpAlertListParams filters GET /dlp-alerts.
type DlpAlertListParams struct {
	Status     string
	Severity   string
	Trigger    string
	EmployeeID string
	Search     string
	DateFrom   *time.Time
	DateTo     *time.Time
	Page       int
	PerPage    int
}

func (r *DlpRepo) ListRules(ctx context.Context) ([]models.DlpRule, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, name, "trigger", pattern, action, severity, enabled, apply_to_all, created_at, updated_at
		FROM dlp_rules
		WHERE deleted_at IS NULL
		ORDER BY created_at DESC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var rules []models.DlpRule
	for rows.Next() {
		var rule models.DlpRule
		if err := rows.Scan(
			&rule.ID, &rule.Name, &rule.Trigger, &rule.Pattern, &rule.Action,
			&rule.Severity, &rule.Enabled, &rule.ApplyToAll, &rule.CreatedAt, &rule.UpdatedAt,
		); err != nil {
			return nil, err
		}
		rules = append(rules, rule)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}

	for i := range rules {
		deps, err := r.listRuleDepartments(ctx, rules[i].ID)
		if err != nil {
			return nil, err
		}
		rules[i].DepartmentIDs = deps
	}
	return rules, nil
}

// ListActiveRulesForEmployee returns enabled rules that apply to the employee
// (apply_to_all OR department junction match). Used by DeviceAuth GET /dlp-rules/active.
func (r *DlpRepo) ListActiveRulesForEmployee(ctx context.Context, employeeID string) ([]models.DlpRule, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT r.id, r.name, r."trigger", r.pattern, r.action, r.severity, r.enabled, r.apply_to_all, r.created_at, r.updated_at
		FROM dlp_rules r
		WHERE r.deleted_at IS NULL
		  AND r.enabled = TRUE
		  AND (
		    r.apply_to_all = TRUE
		    OR EXISTS (
		      SELECT 1
		      FROM dlp_rule_departments rd
		      JOIN employees e ON e.department_id = rd.department_id
		      WHERE rd.rule_id = r.id
		        AND e.employee_id = $1
		        AND e.deleted_at IS NULL
		    )
		  )
		ORDER BY r.created_at ASC`, employeeID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var rules []models.DlpRule
	for rows.Next() {
		var rule models.DlpRule
		if err := rows.Scan(
			&rule.ID, &rule.Name, &rule.Trigger, &rule.Pattern, &rule.Action,
			&rule.Severity, &rule.Enabled, &rule.ApplyToAll, &rule.CreatedAt, &rule.UpdatedAt,
		); err != nil {
			return nil, err
		}
		rules = append(rules, rule)
	}
	return rules, rows.Err()
}

func (r *DlpRepo) GetRule(ctx context.Context, id string) (*models.DlpRule, error) {
	var rule models.DlpRule
	err := r.pool.QueryRow(ctx, `
		SELECT id, name, "trigger", pattern, action, severity, enabled, apply_to_all, created_at, updated_at
		FROM dlp_rules
		WHERE id = $1 AND deleted_at IS NULL`, id).Scan(
		&rule.ID, &rule.Name, &rule.Trigger, &rule.Pattern, &rule.Action,
		&rule.Severity, &rule.Enabled, &rule.ApplyToAll, &rule.CreatedAt, &rule.UpdatedAt,
	)
	if err != nil {
		return nil, err
	}
	deps, err := r.listRuleDepartments(ctx, rule.ID)
	if err != nil {
		return nil, err
	}
	rule.DepartmentIDs = deps
	return &rule, nil
}

func (r *DlpRepo) CreateRule(ctx context.Context, rule models.DlpRule, departmentIDs []int) (*models.DlpRule, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback(ctx)

	id := uuid.New().String()
	err = tx.QueryRow(ctx, `
		INSERT INTO dlp_rules (id, name, "trigger", pattern, action, severity, enabled, apply_to_all)
		VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
		RETURNING id, name, "trigger", pattern, action, severity, enabled, apply_to_all, created_at, updated_at`,
		id, rule.Name, rule.Trigger, rule.Pattern, rule.Action, rule.Severity, rule.Enabled, rule.ApplyToAll,
	).Scan(
		&rule.ID, &rule.Name, &rule.Trigger, &rule.Pattern, &rule.Action,
		&rule.Severity, &rule.Enabled, &rule.ApplyToAll, &rule.CreatedAt, &rule.UpdatedAt,
	)
	if err != nil {
		return nil, err
	}

	if !rule.ApplyToAll {
		if err := r.replaceRuleDepartmentsTx(ctx, tx, rule.ID, departmentIDs); err != nil {
			return nil, err
		}
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	rule.DepartmentIDs = departmentIDs
	if rule.ApplyToAll {
		rule.DepartmentIDs = nil
	}
	return &rule, nil
}

func (r *DlpRepo) UpdateRule(ctx context.Context, id string, rule models.DlpRule, departmentIDs *[]int) (*models.DlpRule, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback(ctx)

	var updated models.DlpRule
	err = tx.QueryRow(ctx, `
		UPDATE dlp_rules SET
			name = $2,
			"trigger" = $3,
			pattern = $4,
			action = $5,
			severity = $6,
			enabled = $7,
			apply_to_all = $8,
			updated_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL
		RETURNING id, name, "trigger", pattern, action, severity, enabled, apply_to_all, created_at, updated_at`,
		id, rule.Name, rule.Trigger, rule.Pattern, rule.Action, rule.Severity, rule.Enabled, rule.ApplyToAll,
	).Scan(
		&updated.ID, &updated.Name, &updated.Trigger, &updated.Pattern, &updated.Action,
		&updated.Severity, &updated.Enabled, &updated.ApplyToAll, &updated.CreatedAt, &updated.UpdatedAt,
	)
	if err != nil {
		return nil, err
	}

	if departmentIDs != nil {
		if updated.ApplyToAll {
			if _, err := tx.Exec(ctx, `DELETE FROM dlp_rule_departments WHERE rule_id = $1`, id); err != nil {
				return nil, err
			}
			updated.DepartmentIDs = nil
		} else {
			if err := r.replaceRuleDepartmentsTx(ctx, tx, id, *departmentIDs); err != nil {
				return nil, err
			}
			updated.DepartmentIDs = *departmentIDs
		}
	} else {
		deps, err := r.listRuleDepartments(ctx, id)
		if err != nil {
			return nil, err
		}
		updated.DepartmentIDs = deps
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	return &updated, nil
}

func (r *DlpRepo) SoftDeleteRule(ctx context.Context, id string) error {
	tag, err := r.pool.Exec(ctx, `
		UPDATE dlp_rules SET deleted_at = NOW(), updated_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL`, id)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return pgx.ErrNoRows
	}
	return nil
}

func (r *DlpRepo) listRuleDepartments(ctx context.Context, ruleID string) ([]int, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT department_id FROM dlp_rule_departments WHERE rule_id = $1 ORDER BY department_id`, ruleID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var ids []int
	for rows.Next() {
		var id int
		if err := rows.Scan(&id); err != nil {
			return nil, err
		}
		ids = append(ids, id)
	}
	return ids, rows.Err()
}

func (r *DlpRepo) replaceRuleDepartmentsTx(ctx context.Context, tx pgx.Tx, ruleID string, departmentIDs []int) error {
	if _, err := tx.Exec(ctx, `DELETE FROM dlp_rule_departments WHERE rule_id = $1`, ruleID); err != nil {
		return err
	}
	for _, depID := range departmentIDs {
		if _, err := tx.Exec(ctx, `
			INSERT INTO dlp_rule_departments (rule_id, department_id) VALUES ($1, $2)
			ON CONFLICT DO NOTHING`, ruleID, depID); err != nil {
			return err
		}
	}
	return nil
}

// BulkUpsertAlerts inserts/updates client-minted alerts. Status/notes/assigned_to
// from an existing admin triage are preserved on conflict.
func (r *DlpRepo) BulkUpsertAlerts(ctx context.Context, employeeID string, deviceID *string, entries []models.DlpAlert) (int, error) {
	if len(entries) == 0 {
		return 0, nil
	}
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return 0, err
	}
	defer tx.Rollback(ctx)

	synced := 0
	for _, e := range entries {
		var detail any
		if e.DetailJSON != nil && *e.DetailJSON != "" {
			detail = json.RawMessage(*e.DetailJSON)
		}
		_, err := tx.Exec(ctx, `
			INSERT INTO dlp_alerts (
				id, employee_id, device_id, rule_id, "trigger", severity, status,
				file_or_url, detail_json, event_at, synced_at
			) VALUES (
				$1, $2, $3, $4, $5, $6, 'open',
				$7, $8, $9, NOW()
			)
			ON CONFLICT (id) DO UPDATE SET
				device_id = COALESCE(EXCLUDED.device_id, dlp_alerts.device_id),
				rule_id = COALESCE(EXCLUDED.rule_id, dlp_alerts.rule_id),
				"trigger" = EXCLUDED."trigger",
				severity = EXCLUDED.severity,
				file_or_url = EXCLUDED.file_or_url,
				detail_json = COALESCE(EXCLUDED.detail_json, dlp_alerts.detail_json),
				event_at = EXCLUDED.event_at,
				synced_at = NOW(),
				updated_at = NOW()`,
			e.ID, employeeID, deviceID, e.RuleID, e.Trigger, e.Severity,
			e.FileOrURL, detail, e.EventAt,
		)
		if err != nil {
			return synced, err
		}
		synced++
	}
	if err := tx.Commit(ctx); err != nil {
		return 0, err
	}
	return synced, nil
}

func (r *DlpRepo) ListAlerts(ctx context.Context, p DlpAlertListParams) ([]models.DlpAlert, int, error) {
	if p.Page < 1 {
		p.Page = 1
	}
	if p.PerPage < 1 {
		p.PerPage = 20
	}
	if p.PerPage > 100 {
		p.PerPage = 100
	}

	where := []string{"a.deleted_at IS NULL"}
	args := []any{}
	argN := 1

	add := func(clause string, v any) {
		where = append(where, clause)
		args = append(args, v)
		argN++
	}

	if p.Status != "" {
		add(fmt.Sprintf("a.status = $%d", argN), p.Status)
	}
	if p.Severity != "" {
		add(fmt.Sprintf("a.severity = $%d", argN), p.Severity)
	}
	if p.Trigger != "" {
		add(fmt.Sprintf(`a."trigger" = $%d`, argN), p.Trigger)
	}
	if p.EmployeeID != "" {
		add(fmt.Sprintf("a.employee_id = $%d", argN), p.EmployeeID)
	}
	if p.Search != "" {
		add(fmt.Sprintf("(a.file_or_url ILIKE $%d OR a.id ILIKE $%d OR e.name ILIKE $%d OR a.employee_id ILIKE $%d)", argN, argN, argN, argN), "%"+p.Search+"%")
	}
	if p.DateFrom != nil {
		add(fmt.Sprintf("a.event_at >= $%d", argN), *p.DateFrom)
	}
	if p.DateTo != nil {
		add(fmt.Sprintf("a.event_at <= $%d", argN), *p.DateTo)
	}

	whereSQL := strings.Join(where, " AND ")
	countSQL := fmt.Sprintf(`
		SELECT COUNT(*)
		FROM dlp_alerts a
		LEFT JOIN employees e ON e.employee_id = a.employee_id AND e.deleted_at IS NULL
		WHERE %s`, whereSQL)

	var total int
	if err := r.pool.QueryRow(ctx, countSQL, args...).Scan(&total); err != nil {
		return nil, 0, err
	}

	offset := (p.Page - 1) * p.PerPage
	listArgs := append(append([]any{}, args...), p.PerPage, offset)
	listSQL := fmt.Sprintf(`
		SELECT a.id, a.employee_id, a.device_id::text, a.rule_id::text, a."trigger", a.severity, a.status,
		       a.file_or_url, a.detail_json::text, a.assigned_to::text, a.notes, a.event_at, a.synced_at,
		       a.created_at, a.updated_at, COALESCE(e.name, '')
		FROM dlp_alerts a
		LEFT JOIN employees e ON e.employee_id = a.employee_id AND e.deleted_at IS NULL
		WHERE %s
		ORDER BY a.event_at DESC
		LIMIT $%d OFFSET $%d`, whereSQL, argN, argN+1)

	rows, err := r.pool.Query(ctx, listSQL, listArgs...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	var alerts []models.DlpAlert
	for rows.Next() {
		var a models.DlpAlert
		var deviceID, ruleID, detail, assigned *string
		if err := rows.Scan(
			&a.ID, &a.EmployeeID, &deviceID, &ruleID, &a.Trigger, &a.Severity, &a.Status,
			&a.FileOrURL, &detail, &assigned, &a.Notes, &a.EventAt, &a.SyncedAt,
			&a.CreatedAt, &a.UpdatedAt, &a.EmployeeName,
		); err != nil {
			return nil, 0, err
		}
		a.DeviceID = deviceID
		a.RuleID = ruleID
		a.DetailJSON = detail
		a.AssignedTo = assigned
		alerts = append(alerts, a)
	}
	return alerts, total, rows.Err()
}

func (r *DlpRepo) PatchAlert(ctx context.Context, id string, status, assignedTo, notes *string) (*models.DlpAlert, error) {
	sets := []string{"updated_at = NOW()"}
	args := []any{id}
	n := 2
	if status != nil {
		sets = append(sets, fmt.Sprintf("status = $%d", n))
		args = append(args, *status)
		n++
	}
	if assignedTo != nil {
		if *assignedTo == "" {
			sets = append(sets, "assigned_to = NULL")
		} else {
			sets = append(sets, fmt.Sprintf("assigned_to = $%d", n))
			args = append(args, *assignedTo)
			n++
		}
	}
	if notes != nil {
		sets = append(sets, fmt.Sprintf("notes = $%d", n))
		args = append(args, *notes)
		n++
	}

	sql := fmt.Sprintf(`
		UPDATE dlp_alerts SET %s
		WHERE id = $1 AND deleted_at IS NULL
		RETURNING id, employee_id, device_id::text, rule_id::text, "trigger", severity, status,
		          file_or_url, detail_json::text, assigned_to::text, notes, event_at, synced_at,
		          created_at, updated_at`, strings.Join(sets, ", "))

	var a models.DlpAlert
	var deviceID, ruleID, detail, assigned *string
	err := r.pool.QueryRow(ctx, sql, args...).Scan(
		&a.ID, &a.EmployeeID, &deviceID, &ruleID, &a.Trigger, &a.Severity, &a.Status,
		&a.FileOrURL, &detail, &assigned, &a.Notes, &a.EventAt, &a.SyncedAt,
		&a.CreatedAt, &a.UpdatedAt,
	)
	if err != nil {
		return nil, err
	}
	a.DeviceID = deviceID
	a.RuleID = ruleID
	a.DetailJSON = detail
	a.AssignedTo = assigned
	return &a, nil
}
