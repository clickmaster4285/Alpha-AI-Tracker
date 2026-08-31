package dto

import "time"

type HolidayResponse struct {
	ID    int    `json:"id"`
	Date  string `json:"date"`
	Label string `json:"label"`
}

type HolidayInput struct {
	Date  string `json:"date"`
	Label string `json:"label"`
}

type ScheduleResponse struct {
	ID            string            `json:"id"`
	Timezone      string            `json:"timezone"`
	GraceMinutes  int               `json:"graceMinutes"`
	WeeklyPattern map[string]string `json:"weeklyPattern"`
	Holidays      []HolidayResponse `json:"holidays"`
	ValidFrom     string            `json:"validFrom"`
	ValidTo       *string           `json:"validTo"`
}

type AttendanceResponse struct {
	EmployeeID      string     `json:"employeeId"`
	WorkDate        string     `json:"workDate"`
	FirstActiveAt   *time.Time `json:"firstActiveAt"`
	LastActiveAt    *time.Time `json:"lastActiveAt"`
	ActiveSeconds   int        `json:"activeSeconds"`
	IdleSeconds     int        `json:"idleSeconds"`
	OffShiftSeconds int        `json:"offShiftSeconds"`
	Status          string     `json:"status"`
	LateMinutes     int        `json:"lateMinutes"`
}

type AttendanceRangeResponse struct {
	Data       []AttendanceResponse `json:"data"`
	Total      int                  `json:"total"`
	Page       int                  `json:"page"`
	PerPage    int                  `json:"perPage"`
	TotalPages int                  `json:"totalPages"`
}
