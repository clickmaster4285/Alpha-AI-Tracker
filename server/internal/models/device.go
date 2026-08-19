package models

import "time"

// EmployeeDevice represents a registered employee desktop device.
type EmployeeDevice struct {
	ID            string     `json:"id" db:"id"`
	EmployeeID    string     `json:"employeeId" db:"employee_id"`
	MachineID     string     `json:"machineId" db:"machine_id"`
	Platform      string     `json:"platform" db:"platform"`
	ClientVersion string     `json:"clientVersion" db:"client_version"`
	DeviceName    string     `json:"deviceName" db:"device_name"`
	TokenHash     string     `json:"-" db:"token_hash"`
	CreatedAt     time.Time  `json:"createdAt" db:"created_at"`
	LastSeenAt    time.Time  `json:"lastSeenAt" db:"last_seen_at"`
	ExpiresAt     *time.Time `json:"expiresAt,omitempty" db:"expires_at"`
	RevokedAt     *time.Time `json:"revokedAt,omitempty" db:"revoked_at"`
}
