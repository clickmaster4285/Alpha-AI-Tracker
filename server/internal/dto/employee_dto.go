package dto

import "time"

// ────────────────────────────────
// Request DTOs
// ────────────────────────────────

// CreateEmployeeRequest is the payload for creating a new employee.
type CreateEmployeeRequest struct {
	Name       string  `json:"name"`
	Email      string  `json:"email"`
	Department string  `json:"department"`
	Role       string  `json:"role"`
	Shift      string  `json:"shift,omitempty"`
}

// UpdateEmployeeRequest is the payload for updating an employee.
type UpdateEmployeeRequest struct {
	Name            *string `json:"name,omitempty"`
	Email           *string `json:"email,omitempty"`
	Department      *string `json:"department,omitempty"`
	Role            *string `json:"role,omitempty"`
	Shift           *string `json:"shift,omitempty"`
	TrackingEnabled *bool   `json:"trackingEnabled,omitempty"`
	TrackingStatus  *string `json:"trackingStatus,omitempty"`
	IsOnline        *bool   `json:"isOnline,omitempty"`
}

// EmployeeLoginRequest is the payload for employee login via desktop client.
type EmployeeLoginRequest struct {
	EmployeeID string `json:"employeeId"`
	SecretKey  string `json:"secretKey"`
}

// GenerateSecretResponse is returned when generating a login secret.
type GenerateSecretResponse struct {
	Secret    string `json:"secret"`
	ExpiresIn int    `json:"expiresIn"` // seconds
}

// ────────────────────────────────
// Response DTOs
// ────────────────────────────────

// EmployeeResponse is the public API response for an employee.
type EmployeeResponse struct {
	ID              string    `json:"id"`
	EmployeeID      string    `json:"employeeId"`
	Name            string    `json:"name"`
	Email           string    `json:"email"`
	Role            string    `json:"role"`
	Department      string    `json:"department"`
	Shift           string    `json:"shift"`
	TrackingEnabled bool      `json:"trackingEnabled"`
	TrackingStatus  string    `json:"trackingStatus"`
	IsOnline        bool      `json:"isOnline"`
	Avatar          string    `json:"avatar"`
	AvatarColor     string    `json:"avatarColor"`
	CreatedAt       time.Time `json:"createdAt"`
	UpdatedAt       time.Time `json:"updatedAt"`
}

// EmployeeListResponse is a paginated list response.
type EmployeeListResponse struct {
	Data       []EmployeeResponse `json:"data"`
	Total      int                `json:"total"`
	Page       int                `json:"page"`
	PerPage    int                `json:"perPage"`
	TotalPages int                `json:"totalPages"`
}

// EmployeeLoginResponse is returned on successful employee login.
type EmployeeLoginResponse struct {
	Employee EmployeeResponse `json:"employee"`
	Token    string           `json:"token,omitempty"`
}
