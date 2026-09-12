package dto

import "time"

type CreateLiveViewSessionRequest struct {
	EmployeeID string `json:"employeeId"`
	Reason     string `json:"reason"`
	Duration   int    `json:"durationMinutes"`
}

type LiveViewSessionResponse struct {
	ID          string     `json:"id"`
	EmployeeID  string     `json:"employeeId"`
	RequestedBy string     `json:"requestedBy"`
	Status      string     `json:"status"`
	Reason      string     `json:"reason"`
	RoomName    string     `json:"-"`
	RequestedAt time.Time  `json:"requestedAt"`
	ApprovedAt  *time.Time `json:"approvedAt,omitempty"`
	StartedAt   *time.Time `json:"startedAt,omitempty"`
	EndedAt     *time.Time `json:"endedAt,omitempty"`
	ExpiresAt   time.Time  `json:"expiresAt"`
	EndReason   string     `json:"endReason,omitempty"`
}

type LiveViewSessionListResponse struct {
	Data       []LiveViewSessionResponse `json:"data"`
	Total      int                       `json:"total"`
	Page       int                       `json:"page"`
	PerPage    int                       `json:"perPage"`
	TotalPages int                       `json:"totalPages"`
}
