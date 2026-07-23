package dto

import "time"

// ────────────────────────────────
// Request DTOs
// ────────────────────────────────

// CreateUserRequest is the payload for creating a new user.
type CreateUserRequest struct {
	Name           string `json:"name" validate:"required,min=2,max=255"`
	Email          string `json:"email" validate:"required,email"`
	Password       string `json:"password,omitempty" validate:"omitempty,min=6"`
	Department     string `json:"department" validate:"required"`
	Role           string `json:"role" validate:"required"`
	Shift          string `json:"shift" validate:"omitempty"`
	TrackingEnabled *bool  `json:"trackingEnabled"`
}

// UpdateUserRequest is the payload for updating a user.
type UpdateUserRequest struct {
	Name           *string `json:"name,omitempty" validate:"omitempty,min=2,max=255"`
	Email          *string `json:"email,omitempty" validate:"omitempty,email"`
	Department     *string `json:"department,omitempty"`
	Role           *string `json:"role,omitempty"`
	Shift          *string `json:"shift,omitempty"`
	TrackingEnabled *bool   `json:"trackingEnabled"`
	TrackingStatus *string `json:"trackingStatus,omitempty"`
	IsOnline       *bool   `json:"isOnline,omitempty"`
}

// LoginRequest is the payload for user authentication.
type LoginRequest struct {
	Email    string `json:"email" validate:"required,email"`
	Password string `json:"password" validate:"required,min=1"`
}

// ────────────────────────────────
// Response DTOs
// ────────────────────────────────

// UserResponse is the public API response for a user.
type UserResponse struct {
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

// UserListResponse is a paginated list response.
type UserListResponse struct {
	Data       []UserResponse `json:"data"`
	Total      int            `json:"total"`
	Page       int            `json:"page"`
	PerPage    int            `json:"perPage"`
	TotalPages int            `json:"totalPages"`
}

// LoginResponse is returned on successful login.
type LoginResponse struct {
	User  UserResponse `json:"user"`
	Token string       `json:"token,omitempty"` // Token is returned for reference; actual auth is via cookie
}

// AuthCheckResponse is returned when checking current auth status.
type AuthCheckResponse struct {
	Authenticated bool        `json:"authenticated"`
	User          interface{} `json:"user,omitempty"`
}

// APIError is a standard error response.
type APIError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Detail  string `json:"detail,omitempty"`
}

// HealthResponse is the health check endpoint response.
type HealthResponse struct {
	Status    string `json:"status"`
	Timestamp string `json:"timestamp"`
	Database  string `json:"database"`
}
