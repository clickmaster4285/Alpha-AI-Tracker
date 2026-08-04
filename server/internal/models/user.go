package models

import "time"

// User represents an employee/administrator in the system.
type User struct {
	ID             string    `json:"id" db:"id"`
	EmployeeID     string    `json:"employeeId" db:"employee_id"`
	Name           string    `json:"name" db:"name"`
	Email          string    `json:"email" db:"email"`
	PasswordHash   string    `json:"-" db:"password_hash"`
	Role           string    `json:"role" db:"role"`
	Department     string    `json:"department" db:"department"`
	Shift          string    `json:"shift" db:"shift"`
	TrackingEnabled bool     `json:"trackingEnabled" db:"tracking_enabled"`
	TrackingStatus string   `json:"trackingStatus" db:"tracking_status"`
	IsOnline       bool     `json:"isOnline" db:"is_online"`
	Avatar         string   `json:"avatar" db:"avatar"`
	AvatarColor    string   `json:"avatarColor" db:"avatar_color"`
	IsCompanyAdmin bool     `json:"isCompanyAdmin" db:"is_company_admin"`
	CreatedAt      time.Time    `json:"createdAt" db:"created_at"`
	UpdatedAt      time.Time    `json:"updatedAt" db:"updated_at"`
	DeletedAt      *time.Time   `json:"deletedAt" db:"deleted_at"`
}

// UserPublic is the public-facing user info (no password hash, safe for API responses).
type UserPublic struct {
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

// ToPublic converts a User to UserPublic (safe for API responses).
func (u *User) ToPublic() UserPublic {
	return UserPublic{
		ID:              u.ID,
		EmployeeID:      u.EmployeeID,
		Name:            u.Name,
		Email:           u.Email,
		Role:            u.Role,
		Department:      u.Department,
		Shift:           u.Shift,
		TrackingEnabled: u.TrackingEnabled,
		TrackingStatus:  u.TrackingStatus,
		IsOnline:        u.IsOnline,
		Avatar:          u.Avatar,
		AvatarColor:     u.AvatarColor,
		CreatedAt:       u.CreatedAt,
		UpdatedAt:       u.UpdatedAt,
	}
}
