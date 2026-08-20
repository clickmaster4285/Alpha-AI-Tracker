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

type DeviceRepo struct {
	pool *pgxpool.Pool
}

func NewDeviceRepo(pool *pgxpool.Pool) *DeviceRepo {
	return &DeviceRepo{pool: pool}
}

// UpsertDevice creates a new active device record or updates an existing one for the same (employee_id, machine_id).
func (r *DeviceRepo) UpsertDevice(
	ctx context.Context,
	employeeID, machineID, platform, clientVersion, deviceName, tokenHash string,
	expiresAt *time.Time,
) (*models.EmployeeDevice, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin tx: %w", err)
	}
	defer tx.Rollback(ctx)

	// Revoke any existing active device for this employee & machine so tokenHash is rotated safely
	_, _ = tx.Exec(ctx, `
		UPDATE employee_devices
		SET revoked_at = NOW()
		WHERE employee_id = $1 AND machine_id = $2 AND revoked_at IS NULL
	`, employeeID, machineID)

	query := `
		INSERT INTO employee_devices (
			employee_id, machine_id, platform, client_version, device_name, token_hash, expires_at
		) VALUES ($1, $2, $3, $4, $5, $6, $7)
		RETURNING id, employee_id, machine_id, platform, client_version, device_name,
		          token_hash, created_at, last_seen_at, expires_at, revoked_at
	`

	var d models.EmployeeDevice
	err = tx.QueryRow(ctx, query,
		employeeID, machineID, platform, clientVersion, deviceName, tokenHash, expiresAt,
	).Scan(
		&d.ID, &d.EmployeeID, &d.MachineID, &d.Platform, &d.ClientVersion, &d.DeviceName,
		&d.TokenHash, &d.CreatedAt, &d.LastSeenAt, &d.ExpiresAt, &d.RevokedAt,
	)
	if err != nil {
		return nil, fmt.Errorf("insert employee_device: %w", err)
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit tx: %w", err)
	}

	return &d, nil
}

// GetByTokenHash retrieves an unrevoked device by its token hash.
func (r *DeviceRepo) GetByTokenHash(ctx context.Context, tokenHash string) (*models.EmployeeDevice, error) {
	query := `
		SELECT id, employee_id, machine_id, platform, client_version, device_name,
		       token_hash, created_at, last_seen_at, expires_at, revoked_at
		FROM employee_devices
		WHERE token_hash = $1 AND revoked_at IS NULL
	`

	var d models.EmployeeDevice
	err := r.pool.QueryRow(ctx, query, tokenHash).Scan(
		&d.ID, &d.EmployeeID, &d.MachineID, &d.Platform, &d.ClientVersion, &d.DeviceName,
		&d.TokenHash, &d.CreatedAt, &d.LastSeenAt, &d.ExpiresAt, &d.RevokedAt,
	)
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, nil
		}
		return nil, fmt.Errorf("query device by token: %w", err)
	}

	if d.ExpiresAt != nil && time.Now().After(*d.ExpiresAt) {
		return nil, nil // expired token
	}

	return &d, nil
}

// TouchLastSeen updates the last_seen_at timestamp for a device.
func (r *DeviceRepo) TouchLastSeen(ctx context.Context, deviceID string) error {
	_, err := r.pool.Exec(ctx, `
		UPDATE employee_devices
		SET last_seen_at = NOW()
		WHERE id = $1 AND revoked_at IS NULL
	`, deviceID)
	return err
}

// ListByEmployeeID returns all devices for an employee ordered by last_seen_at DESC.
func (r *DeviceRepo) ListByEmployeeID(ctx context.Context, employeeID string) ([]models.EmployeeDevice, error) {
	query := `
		SELECT id, employee_id, machine_id, platform, client_version, device_name,
		       token_hash, created_at, last_seen_at, expires_at, revoked_at
		FROM employee_devices
		WHERE employee_id = $1
		ORDER BY last_seen_at DESC
	`

	rows, err := r.pool.Query(ctx, query, employeeID)
	if err != nil {
		return nil, fmt.Errorf("list employee devices: %w", err)
	}
	defer rows.Close()

	var devices []models.EmployeeDevice
	for rows.Next() {
		var d models.EmployeeDevice
		if err := rows.Scan(
			&d.ID, &d.EmployeeID, &d.MachineID, &d.Platform, &d.ClientVersion, &d.DeviceName,
			&d.TokenHash, &d.CreatedAt, &d.LastSeenAt, &d.ExpiresAt, &d.RevokedAt,
		); err != nil {
			return nil, fmt.Errorf("scan employee device: %w", err)
		}
		devices = append(devices, d)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate employee devices: %w", err)
	}

	return devices, nil
}

// RevokeDevice sets revoked_at for a specific device.
func (r *DeviceRepo) RevokeDevice(ctx context.Context, deviceID string) error {
	tag, err := r.pool.Exec(ctx, `
		UPDATE employee_devices
		SET revoked_at = NOW()
		WHERE id = $1 AND revoked_at IS NULL
	`, deviceID)
	if err != nil {
		return fmt.Errorf("revoke device: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("device not found or already revoked")
	}
	return nil
}
