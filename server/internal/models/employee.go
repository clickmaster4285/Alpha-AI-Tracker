package models

import "time"

// Employee represents a tracked employee in the system.
type Employee struct {
	ID              string    `json:"id" db:"id"`
	EmployeeID      string    `json:"employeeId" db:"employee_id"`
	Name            string    `json:"name" db:"name"`
	Email           string    `json:"email" db:"email"`
	Role            string    `json:"role" db:"role"`
	Department      string    `json:"department" db:"department"`
	Shift           string    `json:"shift" db:"shift"`
	TrackingEnabled bool      `json:"trackingEnabled" db:"tracking_enabled"`
	TrackingStatus  string    `json:"trackingStatus" db:"tracking_status"`
	IsOnline        bool      `json:"isOnline" db:"is_online"`
	Avatar          string    `json:"avatar" db:"avatar"`
	AvatarColor     string    `json:"avatarColor" db:"avatar_color"`
	CreatedAt       time.Time `json:"createdAt" db:"created_at"`
	UpdatedAt       time.Time `json:"updatedAt" db:"updated_at"`
}

// EmployeePublic is the public-facing employee info.
type EmployeePublic struct {
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

// ToPublic converts an Employee to EmployeePublic.
func (e *Employee) ToPublic() EmployeePublic {
	return EmployeePublic{
		ID:              e.ID,
		EmployeeID:      e.EmployeeID,
		Name:            e.Name,
		Email:           e.Email,
		Role:            e.Role,
		Department:      e.Department,
		Shift:           e.Shift,
		TrackingEnabled: e.TrackingEnabled,
		TrackingStatus:  e.TrackingStatus,
		IsOnline:        e.IsOnline,
		Avatar:          e.Avatar,
		AvatarColor:     e.AvatarColor,
		CreatedAt:       e.CreatedAt,
		UpdatedAt:       e.UpdatedAt,
	}
}
