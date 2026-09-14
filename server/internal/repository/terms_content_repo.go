package repository

import (
	"context"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

type TermsContent struct {
	ID            string
	FeatureID     string
	Heading       string
	Body          string
	TermsVersion  string
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
		SELECT id, feature_id, heading, body, terms_version, updated_at, created_at
		FROM terms_content
		WHERE deleted_at IS NULL
		ORDER BY created_at ASC
	`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var results []TermsContent
	for rows.Next() {
		var tc TermsContent
		if err := rows.Scan(&tc.ID, &tc.FeatureID, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.UpdatedAt, &tc.CreatedAt); err != nil {
			return nil, err
		}
		results = append(results, tc)
	}
	return results, nil
}

func (r *TermsContentRepo) GetByFeatureID(ctx context.Context, featureID string) (*TermsContent, error) {
	var tc TermsContent
	err := r.pool.QueryRow(ctx, `
		SELECT id, feature_id, heading, body, terms_version, updated_at, created_at
		FROM terms_content
		WHERE feature_id = $1 AND deleted_at IS NULL
	`, featureID).Scan(&tc.ID, &tc.FeatureID, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.UpdatedAt, &tc.CreatedAt)
	if err != nil {
		return nil, err
	}
	return &tc, nil
}

func (r *TermsContentRepo) Upsert(ctx context.Context, featureID, heading, body, termsVersion string) (*TermsContent, error) {
	var tc TermsContent
	err := r.pool.QueryRow(ctx, `
		INSERT INTO terms_content (id, feature_id, heading, body, terms_version, updated_at, created_at)
		VALUES (gen_random_uuid()::text, $1, $2, $3, $4, NOW(), NOW())
		ON CONFLICT (feature_id) WHERE deleted_at IS NULL
		DO UPDATE SET heading = $2, body = $3, terms_version = $4, updated_at = NOW()
		RETURNING id, feature_id, heading, body, terms_version, updated_at, created_at
	`, featureID, heading, body, termsVersion).Scan(&tc.ID, &tc.FeatureID, &tc.Heading, &tc.Body, &tc.TermsVersion, &tc.UpdatedAt, &tc.CreatedAt)
	if err != nil {
		return nil, err
	}
	return &tc, nil
}
