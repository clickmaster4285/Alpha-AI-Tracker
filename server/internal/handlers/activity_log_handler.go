package handlers

import (
	"net/http"
	"strconv"
	"time"

	"github.com/labstack/echo/v4"
	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
)

// ActivityLogHandler handles activity log endpoints.
type ActivityLogHandler struct {
	activityLogService *services.ActivityLogService
	authService        *services.AuthService
}

// NewActivityLogHandler creates a new ActivityLogHandler.
func NewActivityLogHandler(activityLogService *services.ActivityLogService, authService *services.AuthService) *ActivityLogHandler {
	return &ActivityLogHandler{
		activityLogService: activityLogService,
		authService:        authService,
	}
}

// SyncLogs handles POST /api/v1/activity-logs/sync
// Accepts a batch of activity logs from the desktop client.
func (h *ActivityLogHandler) SyncLogs(c echo.Context) error {
	var req dto.SyncActivityLogsRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	if req.EmployeeID == "" || req.Token == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Employee ID and token are required",
		})
	}

	// Validate employee token
	claims, err := h.authService.ValidateToken(req.Token)
	if err != nil {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code:    http.StatusUnauthorized,
			Message: "Invalid or expired token",
		})
	}
	if claims.UserID == "" {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code:    http.StatusUnauthorized,
			Message: "Invalid token claims",
		})
	}

	resp, err := h.activityLogService.SyncLogs(c.Request().Context(), &req)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to sync logs",
			Detail:  err.Error(),
		})
	}

	return c.JSON(http.StatusOK, resp)
}

// ListLogs handles GET /api/v1/activity-logs
func (h *ActivityLogHandler) ListLogs(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))

	params := repository.ActivityLogListParams{
		EmployeeID: c.QueryParam("employeeId"),
		Search:     c.QueryParam("search"),
		Platform:   c.QueryParam("platform"),
		Page:       page,
		PerPage:    perPage,
	}

	// Parse optional start/end date
	if startStr := c.QueryParam("startDate"); startStr != "" {
		if t, err := time.Parse(time.RFC3339, startStr); err == nil {
			params.StartDate = &t
		}
	}
	if endStr := c.QueryParam("endDate"); endStr != "" {
		if t, err := time.Parse(time.RFC3339, endStr); err == nil {
			params.EndDate = &t
		}
	}

	// Parse optional foreground filter
	if fgStr := c.QueryParam("foreground"); fgStr != "" {
		fg := fgStr == "true"
		params.Foreground = &fg
	}

	result, err := h.activityLogService.List(c.Request().Context(), params)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list activity logs",
			Detail:  err.Error(),
		})
	}

	return c.JSON(http.StatusOK, result)
}
