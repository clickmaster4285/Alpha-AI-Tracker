package repository

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type DlpRepo struct {
	pool *pgxpool.Pool
}

func NewDlpRepo(pool *pgxpool.Pool) *DlpRepo {
	return &DlpRepo{pool: pool}
}

// ── Rules ───────────────────────────────────────────────────────────────────

func (r *DlpRepo) ListRules(ctx context.Context) ([]models.DlpRule, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id::text, name, enabled, trigger, match_json, action, severity, scope_json,
		       created_at, updated_at
		FROM dlp_rules
		WHERE deleted_at IS NULL
		ORDER BY created_at DESC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	return scanDlpRules(rows)
}

func (r *DlpRepo) ListEnabledRules(ctx context.Context) ([]models.DlpRule, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id::text, name, enabled, trigger, match_json, action, severity, scope_json,
		       created_at, updated_at
		FROM dlp_rules
		WHERE deleted_at IS NULL AND enabled = TRUE
		ORDER BY created_at ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	return scanDlpRules(rows)
}

func (r *DlpRepo) GetRule(ctx context.Context, id string) (*models.DlpRule, error) {
	row := r.pool.QueryRow(ctx, `
		SELECT id::text, name, enabled, trigger, match_json, action, severity, scope_json,
		       created_at, updated_at
		FROM dlp_rules WHERE id = $1::uuid AND deleted_at IS NULL`, id)
	rule, err := scanDlpRule(row)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	return rule, err
}

func (r *DlpRepo) CreateRule(ctx context.Context, name, trigger string, match, scope json.RawMessage, action, severity string, enabled bool) (*models.DlpRule, error) {
	if len(match) == 0 {
		match = json.RawMessage(`{}`)
	}
	if len(scope) == 0 {
		scope = json.RawMessage(`{"all":true}`)
	}
	row := r.pool.QueryRow(ctx, `
		INSERT INTO dlp_rules (name, enabled, trigger, match_json, action, severity, scope_json)
		VALUES ($1, $2, $3, $4::jsonb, $5, $6, $7::jsonb)
		RETURNING id::text, name, enabled, trigger, match_json, action, severity, scope_json, created_at, updated_at`,
		name, enabled, trigger, []byte(match), action, severity, []byte(scope))
	return scanDlpRule(row)
}

func (r *DlpRepo) UpdateRule(ctx context.Context, id string, name *string, enabled *bool, trigger *string, match *json.RawMessage, action, severity *string, scope *json.RawMessage) (*models.DlpRule, error) {
	cur, err := r.GetRule(ctx, id)
	if err != nil || cur == nil {
		return cur, err
	}
	if name != nil {
		cur.Name = *name
	}
	if enabled != nil {
		cur.Enabled = *enabled
	}
	if trigger != nil {
		cur.Trigger = *trigger
	}
	if match != nil {
		cur.MatchJSON = *match
	}
	if action != nil {
		cur.Action = *action
	}
	if severity != nil {
		cur.Severity = *severity
	}
	if scope != nil {
		cur.ScopeJSON = *scope
	}
	row := r.pool.QueryRow(ctx, `
		UPDATE dlp_rules SET
			name = $2, enabled = $3, trigger = $4,
			match_json = $5::jsonb, action = $6, severity = $7, scope_json = $8::jsonb
		WHERE id = $1::uuid AND deleted_at IS NULL
		RETURNING id::text, name, enabled, trigger, match_json, action, severity, scope_json, created_at, updated_at`,
		id, cur.Name, cur.Enabled, cur.Trigger, []byte(cur.MatchJSON), cur.Action, cur.Severity, []byte(cur.ScopeJSON))
	return scanDlpRule(row)
}

func (r *DlpRepo) SoftDeleteRule(ctx context.Context, id string) error {
	tag, err := r.pool.Exec(ctx, `
		UPDATE dlp_rules SET deleted_at = NOW() WHERE id = $1::uuid AND deleted_at IS NULL`, id)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("dlp rule not found")
	}
	return nil
}

// ── Policy versions ─────────────────────────────────────────────────────────

func (r *DlpRepo) InsertPolicyVersion(ctx context.Context, bundle json.RawMessage, hash string, publishedBy *string) (*models.DlpPolicyVersion, error) {
	var row pgx.Row
	if publishedBy != nil && *publishedBy != "" {
		row = r.pool.QueryRow(ctx, `
			INSERT INTO dlp_policy_versions (bundle_json, bundle_hash, published_by)
			VALUES ($1::jsonb, $2, $3::uuid)
			RETURNING version, bundle_json, bundle_hash, published_at`,
			[]byte(bundle), hash, *publishedBy)
	} else {
		row = r.pool.QueryRow(ctx, `
			INSERT INTO dlp_policy_versions (bundle_json, bundle_hash)
			VALUES ($1::jsonb, $2)
			RETURNING version, bundle_json, bundle_hash, published_at`,
			[]byte(bundle), hash)
	}
	var v models.DlpPolicyVersion
	if err := row.Scan(&v.Version, &v.BundleJSON, &v.BundleHash, &v.PublishedAt); err != nil {
		return nil, err
	}
	return &v, nil
}

func (r *DlpRepo) LatestPolicyVersion(ctx context.Context) (*models.DlpPolicyVersion, error) {
	row := r.pool.QueryRow(ctx, `
		SELECT version, bundle_json, bundle_hash, published_at
		FROM dlp_policy_versions
		ORDER BY version DESC LIMIT 1`)
	var v models.DlpPolicyVersion
	err := row.Scan(&v.Version, &v.BundleJSON, &v.BundleHash, &v.PublishedAt)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &v, nil
}

func (r *DlpRepo) GetPolicyVersion(ctx context.Context, version int64) (*models.DlpPolicyVersion, error) {
	row := r.pool.QueryRow(ctx, `
		SELECT version, bundle_json, bundle_hash, published_at
		FROM dlp_policy_versions WHERE version = $1`, version)
	var v models.DlpPolicyVersion
	err := row.Scan(&v.Version, &v.BundleJSON, &v.BundleHash, &v.PublishedAt)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &v, nil
}

// UpdatePolicyBundle stamps the final bundle JSON/hash onto an already-inserted version row.
func (r *DlpRepo) UpdatePolicyBundle(ctx context.Context, version int64, bundle json.RawMessage, hash string) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE dlp_policy_versions SET bundle_json = $2::jsonb, bundle_hash = $3
		WHERE version = $1`, version, []byte(bundle), hash)
	return err
}

// ── Alerts ──────────────────────────────────────────────────────────────────

func (r *DlpRepo) UpsertAlerts(ctx context.Context, employeeID, machineID string, items []struct {
	ID            string
	RuleID        *string
	PolicyVersion int64
	Type          string
	Severity      string
	ActionTaken   string
	OccurredAt    time.Time
	Evidence      json.RawMessage
}) ([]string, []string, error) {
	accepted := make([]string, 0, len(items))
	rejected := make([]string, 0)
	for _, it := range items {
		if it.ID == "" || it.OccurredAt.IsZero() {
			rejected = append(rejected, it.ID)
			continue
		}
		sev := normalizeSeverity(it.Severity)
		taken := normalizeActionTaken(it.ActionTaken)
		ev := it.Evidence
		if len(ev) == 0 {
			ev = json.RawMessage(`{}`)
		}
		_, err := r.pool.Exec(ctx, `
			INSERT INTO dlp_alerts (
				id, rule_id, policy_version, employee_id, machine_id,
				type, severity, action_taken, occurred_at, evidence_json, synced_at
			) VALUES (
				$1::uuid, $2::uuid, $3, $4, $5,
				$6, $7, $8, $9, $10::jsonb, NOW()
			)
			ON CONFLICT (id) DO UPDATE SET
				synced_at = NOW(),
				evidence_json = EXCLUDED.evidence_json,
				action_taken = EXCLUDED.action_taken`,
			it.ID, nullableUUID(it.RuleID), it.PolicyVersion, employeeID, machineID,
			it.Type, sev, taken, it.OccurredAt, []byte(ev))
		if err != nil {
			rejected = append(rejected, it.ID)
			continue
		}
		accepted = append(accepted, it.ID)
	}
	return accepted, rejected, nil
}

type DlpAlertListFilter struct {
	Search     string
	Status     string
	Severity   string
	Type       string
	EmployeeID string
	DateFrom   time.Time
	DateTo     time.Time
	Page       int
	PerPage    int
}

// alertWhereOpts controls which of the "self-referential" filters are
// applied to a given query.
type alertWhereOpts struct {
	// Exclude the status filter. Used by the status counts so the status
	// chips keep showing the FULL distribution across the other active
	// filters (search / severity / type / employee / date) even while one
	// status is selected — otherwise picking "Open" would zero out every
	// other chip and the user could not see what else is waiting.
	ExcludeStatus bool
	// Exclude the type filter. Used by the type list so the type dropdown
	// keeps offering every type available under the other filters, even
	// while one type is selected.
	ExcludeType bool
}

// buildAlertWhere builds the shared WHERE clause (plus its args) for the alert
// list, the status counts and the type list.
func buildAlertWhere(f DlpAlertListFilter, opts alertWhereOpts) (string, []any) {
	where := []string{"1=1"}
	args := []any{}
	n := 1
	if f.Search != "" {
		where = append(where, fmt.Sprintf(`(
			a.id::text ILIKE $%d OR a.employee_id ILIKE $%d OR COALESCE(e.name,'') ILIKE $%d
			OR a.type ILIKE $%d OR a.evidence_json::text ILIKE $%d
		)`, n, n, n, n, n))
		args = append(args, "%"+f.Search+"%")
		n++
	}
	if !opts.ExcludeStatus && f.Status != "" {
		where = append(where, fmt.Sprintf("a.status = $%d", n))
		args = append(args, f.Status)
		n++
	}
	if f.Severity != "" {
		where = append(where, fmt.Sprintf("a.severity = $%d", n))
		args = append(args, f.Severity)
		n++
	}
	if !opts.ExcludeType && f.Type != "" {
		where = append(where, fmt.Sprintf("a.type = $%d", n))
		args = append(args, f.Type)
		n++
	}
	if f.EmployeeID != "" {
		// The alert's `employee_id` column holds the human code (EMP-XXXXX),
		// but callers may pass the employees-table UUID (the web's employee
		// picker URL key). Accept either so a deep link from another page
		// resolves to the same employee.
		where = append(where, fmt.Sprintf("(a.employee_id = $%d OR a.employee_id = COALESCE((SELECT e.employee_id FROM employees e WHERE e.id::text = $%d AND e.deleted_at IS NULL), ''))", n, n))
		args = append(args, f.EmployeeID)
		n++
	}
	if !f.DateFrom.IsZero() {
		where = append(where, fmt.Sprintf("a.occurred_at >= $%d", n))
		args = append(args, f.DateFrom)
		n++
	}
	if !f.DateTo.IsZero() {
		where = append(where, fmt.Sprintf("a.occurred_at <= $%d", n))
		args = append(args, f.DateTo)
		n++
	}
	return strings.Join(where, " AND "), args
}

func (r *DlpRepo) ListAlerts(ctx context.Context, f DlpAlertListFilter) ([]models.DlpAlert, int, error) {
	if f.Page < 1 {
		f.Page = 1
	}
	if f.PerPage < 1 || f.PerPage > 100 {
		f.PerPage = 30
	}
	wsql, args := buildAlertWhere(f, alertWhereOpts{})

	var total int
	countSQL := `SELECT COUNT(*) FROM dlp_alerts a LEFT JOIN employees e ON e.employee_id = a.employee_id AND e.deleted_at IS NULL WHERE ` + wsql
	if err := r.pool.QueryRow(ctx, countSQL, args...).Scan(&total); err != nil {
		return nil, 0, err
	}

	offset := (f.Page - 1) * f.PerPage
	limitArg := len(args) + 1
	offsetArg := len(args) + 2
	args = append(args, f.PerPage, offset)
	listSQL := fmt.Sprintf(`
		SELECT a.id::text, a.rule_id::text, a.policy_version, a.employee_id, a.machine_id,
		       a.type, a.severity, a.action_taken, a.occurred_at, a.evidence_json,
		       a.status, a.assigned_to, a.notes, a.synced_at, a.created_at, a.updated_at,
		       COALESCE(e.name, '')
		FROM dlp_alerts a
		LEFT JOIN employees e ON e.employee_id = a.employee_id AND e.deleted_at IS NULL
		WHERE %s
		ORDER BY a.occurred_at DESC
		LIMIT $%d OFFSET $%d`, wsql, limitArg, offsetArg)

	rows, err := r.pool.Query(ctx, listSQL, args...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	out := make([]models.DlpAlert, 0)
	for rows.Next() {
		var a models.DlpAlert
		var ruleID *string
		if err := rows.Scan(
			&a.ID, &ruleID, &a.PolicyVersion, &a.EmployeeID, &a.MachineID,
			&a.Type, &a.Severity, &a.ActionTaken, &a.OccurredAt, &a.EvidenceJSON,
			&a.Status, &a.AssignedTo, &a.Notes, &a.SyncedAt, &a.CreatedAt, &a.UpdatedAt,
			&a.EmployeeName,
		); err != nil {
			return nil, 0, err
		}
		a.RuleID = ruleID
		out = append(out, a)
	}
	return out, total, rows.Err()
}

// AlertStatusCounts returns the open / investigating / resolved /
// false-positive distribution for the CURRENTLY ACTIVE FILTERS — except the
// status filter itself, so the status chips keep showing the full
// distribution across the other active filters (search / severity / type /
// employee / date) even while one status is selected.
func (r *DlpRepo) AlertStatusCounts(ctx context.Context, f DlpAlertListFilter) (open, investigating, resolved, falsePositive int, err error) {
	wsql, args := buildAlertWhere(f, alertWhereOpts{ExcludeStatus: true})
	countSQL := `
		SELECT
			COUNT(*) FILTER (WHERE a.status = 'open'),
			COUNT(*) FILTER (WHERE a.status = 'investigating'),
			COUNT(*) FILTER (WHERE a.status = 'resolved'),
			COUNT(*) FILTER (WHERE a.status = 'false_positive')
		FROM dlp_alerts a
		LEFT JOIN employees e ON e.employee_id = a.employee_id AND e.deleted_at IS NULL
		WHERE ` + wsql
	if err = r.pool.QueryRow(ctx, countSQL, args...).Scan(&open, &investigating, &resolved, &falsePositive); err != nil {
		return 0, 0, 0, 0, err
	}
	return
}

// AlertTypeCounts returns the distinct alert types present under the current
// filters — except the type filter itself (and the status filter), so the type
// dropdown keeps offering every available type even while one is selected.
func (r *DlpRepo) AlertTypeCounts(ctx context.Context, f DlpAlertListFilter) ([]string, error) {
	wsql, args := buildAlertWhere(f, alertWhereOpts{ExcludeStatus: true, ExcludeType: true})
	rows, err := r.pool.Query(ctx, `
		SELECT DISTINCT a.type
		FROM dlp_alerts a
		LEFT JOIN employees e ON e.employee_id = a.employee_id AND e.deleted_at IS NULL
		WHERE `+wsql+` AND a.type <> ''
		ORDER BY a.type`, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	out := []string{}
	for rows.Next() {
		var t string
		if err := rows.Scan(&t); err != nil {
			return nil, err
		}
		out = append(out, t)
	}
	return out, rows.Err()
}

func (r *DlpRepo) UpdateAlert(ctx context.Context, id string, status, assignedTo, notes *string) (*models.DlpAlert, error) {
	row := r.pool.QueryRow(ctx, `
		UPDATE dlp_alerts SET
			status = COALESCE($2, status),
			assigned_to = COALESCE($3, assigned_to),
			notes = COALESCE($4, notes)
		WHERE id = $1::uuid
		RETURNING id::text, rule_id::text, policy_version, employee_id, machine_id,
		          type, severity, action_taken, occurred_at, evidence_json,
		          status, assigned_to, notes, synced_at, created_at, updated_at`,
		id, status, assignedTo, notes)
	var a models.DlpAlert
	var ruleID *string
	err := row.Scan(
		&a.ID, &ruleID, &a.PolicyVersion, &a.EmployeeID, &a.MachineID,
		&a.Type, &a.Severity, &a.ActionTaken, &a.OccurredAt, &a.EvidenceJSON,
		&a.Status, &a.AssignedTo, &a.Notes, &a.SyncedAt, &a.CreatedAt, &a.UpdatedAt,
	)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	a.RuleID = ruleID
	return &a, nil
}

// ── Shadow IT ───────────────────────────────────────────────────────────────

type ShadowItListFilter struct {
	Search     string
	AuthStatus string
	Page       int
	PerPage    int
}

func (r *DlpRepo) ListShadowItApps(ctx context.Context, f ShadowItListFilter) ([]models.ShadowItApp, int, error) {
	if f.Page < 1 {
		f.Page = 1
	}
	if f.PerPage < 1 || f.PerPage > 100 {
		f.PerPage = 30
	}
	where := []string{"deleted_at IS NULL"}
	args := []any{}
	n := 1
	if f.Search != "" {
		where = append(where, fmt.Sprintf("name ILIKE $%d", n))
		args = append(args, "%"+f.Search+"%")
		n++
	}
	if f.AuthStatus != "" {
		where = append(where, fmt.Sprintf("auth_status = $%d", n))
		args = append(args, f.AuthStatus)
		n++
	}
	wsql := strings.Join(where, " AND ")
	var total int
	if err := r.pool.QueryRow(ctx, `SELECT COUNT(*) FROM shadow_it_apps WHERE `+wsql, args...).Scan(&total); err != nil {
		return nil, 0, err
	}
	offset := (f.Page - 1) * f.PerPage
	args = append(args, f.PerPage, offset)
	rows, err := r.pool.Query(ctx, fmt.Sprintf(`
		SELECT id, name, name_normalized, kind, auth_status, risk,
		       first_detected_at, user_count, last_seen_at, created_at, updated_at
		FROM shadow_it_apps WHERE %s
		ORDER BY last_seen_at DESC
		LIMIT $%d OFFSET $%d`, wsql, n, n+1), args...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()
	out := make([]models.ShadowItApp, 0)
	for rows.Next() {
		var a models.ShadowItApp
		if err := rows.Scan(
			&a.ID, &a.Name, &a.NameNormalized, &a.Kind, &a.AuthStatus, &a.Risk,
			&a.FirstDetectedAt, &a.UserCount, &a.LastSeenAt, &a.CreatedAt, &a.UpdatedAt,
		); err != nil {
			return nil, 0, err
		}
		out = append(out, a)
	}
	return out, total, rows.Err()
}

func (r *DlpRepo) ShadowItStatusCounts(ctx context.Context) (approved, unauthorized, underReview int, err error) {
	err = r.pool.QueryRow(ctx, `
		SELECT
			COUNT(*) FILTER (WHERE auth_status = 'approved'),
			COUNT(*) FILTER (WHERE auth_status = 'unauthorized'),
			COUNT(*) FILTER (WHERE auth_status = 'under_review')
		FROM shadow_it_apps WHERE deleted_at IS NULL`).Scan(&approved, &unauthorized, &underReview)
	return
}

func (r *DlpRepo) UpdateShadowItApp(ctx context.Context, id int64, authStatus, risk *string) (*models.ShadowItApp, error) {
	row := r.pool.QueryRow(ctx, `
		UPDATE shadow_it_apps SET
			auth_status = COALESCE($2, auth_status),
			risk = COALESCE($3, risk)
		WHERE id = $1 AND deleted_at IS NULL
		RETURNING id, name, name_normalized, kind, auth_status, risk,
		          first_detected_at, user_count, last_seen_at, created_at, updated_at`,
		id, authStatus, risk)
	var a models.ShadowItApp
	err := row.Scan(
		&a.ID, &a.Name, &a.NameNormalized, &a.Kind, &a.AuthStatus, &a.Risk,
		&a.FirstDetectedAt, &a.UserCount, &a.LastSeenAt, &a.CreatedAt, &a.UpdatedAt,
	)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &a, nil
}

func (r *DlpRepo) ListUnauthorizedShadowDeny(ctx context.Context) (domains, appNames []string, err error) {
	rows, err := r.pool.Query(ctx, `
		SELECT name, kind FROM shadow_it_apps
		WHERE deleted_at IS NULL AND auth_status = 'unauthorized'`)
	if err != nil {
		return nil, nil, err
	}
	defer rows.Close()
	for rows.Next() {
		var name, kind string
		if err := rows.Scan(&name, &kind); err != nil {
			return nil, nil, err
		}
		if kind == "website" || kind == "saas" {
			domains = append(domains, strings.ToLower(name))
		} else {
			appNames = append(appNames, name)
		}
	}
	return domains, appNames, rows.Err()
}

// ── Removal challenges ──────────────────────────────────────────────────────

func (r *DlpRepo) InsertRemovalChallenge(ctx context.Context, machineID, employeeID, tokenHash, purpose string, expiresAt time.Time, issuedBy *string) (string, error) {
	var id string
	if issuedBy != nil && *issuedBy != "" {
		err := r.pool.QueryRow(ctx, `
			INSERT INTO removal_challenges (machine_id, employee_id, token_hash, purpose, expires_at, issued_by)
			VALUES ($1, $2, $3, $4, $5, $6::uuid)
			RETURNING id::text`, machineID, employeeID, tokenHash, purpose, expiresAt, *issuedBy).Scan(&id)
		return id, err
	}
	err := r.pool.QueryRow(ctx, `
		INSERT INTO removal_challenges (machine_id, employee_id, token_hash, purpose, expires_at)
		VALUES ($1, $2, $3, $4, $5)
		RETURNING id::text`, machineID, employeeID, tokenHash, purpose, expiresAt).Scan(&id)
	return id, err
}

// ConsumeRemovalChallenge validates hash for the calling machine and marks used.
func (r *DlpRepo) ConsumeRemovalChallenge(ctx context.Context, machineID, tokenHash, purpose string) (valid bool, storedPurpose string, err error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return false, "", err
	}
	defer tx.Rollback(ctx)

	var id, p string
	err = tx.QueryRow(ctx, `
		SELECT id::text, purpose FROM removal_challenges
		WHERE token_hash = $1 AND machine_id = $2
		  AND used_at IS NULL AND expires_at > NOW()
		FOR UPDATE`, tokenHash, machineID).Scan(&id, &p)
	if err == pgx.ErrNoRows {
		return false, "", nil
	}
	if err != nil {
		return false, "", err
	}
	if purpose != "" && p != "both" && p != purpose {
		return false, p, nil
	}
	_, err = tx.Exec(ctx, `UPDATE removal_challenges SET used_at = NOW() WHERE id = $1::uuid`, id)
	if err != nil {
		return false, "", err
	}
	if err := tx.Commit(ctx); err != nil {
		return false, "", err
	}
	return true, p, nil
}

// ── helpers ─────────────────────────────────────────────────────────────────

func scanDlpRules(rows pgx.Rows) ([]models.DlpRule, error) {
	out := make([]models.DlpRule, 0)
	for rows.Next() {
		rule, err := scanDlpRule(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, *rule)
	}
	return out, rows.Err()
}

type scannable interface {
	Scan(dest ...any) error
}

func scanDlpRule(row scannable) (*models.DlpRule, error) {
	var rule models.DlpRule
	err := row.Scan(
		&rule.ID, &rule.Name, &rule.Enabled, &rule.Trigger, &rule.MatchJSON,
		&rule.Action, &rule.Severity, &rule.ScopeJSON, &rule.CreatedAt, &rule.UpdatedAt,
	)
	if err != nil {
		return nil, err
	}
	return &rule, nil
}

func nullableUUID(s *string) any {
	if s == nil || *s == "" {
		return nil
	}
	return *s
}

func normalizeSeverity(s string) string {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "critical", "high", "medium", "low":
		return strings.ToLower(s)
	default:
		return "medium"
	}
}

func normalizeActionTaken(s string) string {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "alerted", "blocked", "blocked_failed":
		return strings.ToLower(s)
	case "alert":
		return "alerted"
	case "block", "alert_block", "alert+block":
		return "blocked"
	default:
		return "alerted"
	}
}

// HashRemovalToken returns hex(sha256(raw)).
func HashRemovalToken(raw string) string {
	sum := sha256.Sum256([]byte(raw))
	return hex.EncodeToString(sum[:])
}
