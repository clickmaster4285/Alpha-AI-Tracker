package repository

import (
	"context"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

type TermsContent struct {
	ID            string
	Slug          string
	Heading       string
	Body          string
	TermsVersion  string
	IsSystem      bool
	SortOrder     int
	UpdatedAt     time.Time
	CreatedAt     time.Time
}

type TermsContentRepo struct {
	pool *pgxpool.Pool
}

func NewTermsContentRepo(pool *pgxpool.Pool) *TermsContentRepo {
	return &TermsContentRepo{pool: pool}
}

func (r *TermsContentRepo) ListAll(ctx context.Context) ([]TermsContent, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, slug, heading, body, terms_version, is_system, sort_order, updated_at, created_at
		FROM terms_content
		WHERE deleted_at IS NULL
		ORDER BY sort_order ASC, created_at ASC
	`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var results []TermsContent
	for rows.Next() {
		var tc TermsContent
		if err := rows.Scan(&tc.ID, &tc.Slug, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.IsSystem, &tc.SortOrder, &tc.UpdatedAt, &tc.CreatedAt); err != nil {
			return nil, err
		}
		results = append(results, tc)
	}
	return results, nil
}

func (r *TermsContentRepo) GetBySlug(ctx context.Context, slug string) (*TermsContent, error) {
	var tc TermsContent
	err := r.pool.QueryRow(ctx, `
		SELECT id, slug, heading, body, terms_version, is_system, sort_order, updated_at, created_at
		FROM terms_content
		WHERE slug = $1 AND deleted_at IS NULL
	`, slug).Scan(&tc.ID, &tc.Slug, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.IsSystem, &tc.SortOrder, &tc.UpdatedAt, &tc.CreatedAt)
	if err != nil {
		return nil, err
	}
	return &tc, nil
}

func (r *TermsContentRepo) GetByID(ctx context.Context, id string) (*TermsContent, error) {
	var tc TermsContent
	err := r.pool.QueryRow(ctx, `
		SELECT id, slug, heading, body, terms_version, is_system, sort_order, updated_at, created_at
		FROM terms_content
		WHERE id = $1 AND deleted_at IS NULL
	`, id).Scan(&tc.ID, &tc.Slug, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.IsSystem, &tc.SortOrder, &tc.UpdatedAt, &tc.CreatedAt)
	if err != nil {
		return nil, err
	}
	return &tc, nil
}

func (r *TermsContentRepo) Update(ctx context.Context, id, heading, body, termsVersion string) (*TermsContent, error) {
	var tc TermsContent
	err := r.pool.QueryRow(ctx, `
		UPDATE terms_content
		SET heading = $2, body = $3, terms_version = $4, updated_at = NOW()
		WHERE id = $1 AND deleted_at IS NULL
		RETURNING id, slug, heading, body, terms_version, is_system, sort_order, updated_at, created_at
	`, id, heading, body, termsVersion).Scan(&tc.ID, &tc.Slug, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.IsSystem, &tc.SortOrder, &tc.UpdatedAt, &tc.CreatedAt)
	if err != nil {
		return nil, err
	}
	return &tc, nil
}

func (r *TermsContentRepo) Create(ctx context.Context, slug, heading, body, termsVersion string, sortOrder int) (*TermsContent, error) {
	var tc TermsContent
	err := r.pool.QueryRow(ctx, `
		INSERT INTO terms_content (id, slug, heading, body, terms_version, is_system, sort_order, updated_at, created_at)
		VALUES (gen_random_uuid()::text, $1, $2, $3, $4, false, $5, NOW(), NOW())
		RETURNING id, slug, heading, body, terms_version, is_system, sort_order, updated_at, created_at
	`, slug, heading, body, termsVersion, sortOrder).Scan(&tc.ID, &tc.Slug, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.IsSystem, &tc.SortOrder, &tc.UpdatedAt, &tc.CreatedAt)
	if err != nil {
		return nil, err
	}
	return &tc, nil
}

func (r *TermsContentRepo) Delete(ctx context.Context, id string) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE terms_content
		SET deleted_at = NOW()
		WHERE id = $1 AND is_system = false AND deleted_at IS NULL
	`, id)
	return err
}

func (r *TermsContentRepo) GetMaxSortOrder(ctx context.Context) int {
	var maxOrder int
	err := r.pool.QueryRow(ctx, `
		SELECT COALESCE(MAX(sort_order), 0)
		FROM terms_content
		WHERE deleted_at IS NULL
	`).Scan(&maxOrder)
	if err != nil {
		return 0
	}
	return maxOrder
}
