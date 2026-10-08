package handlers

import (
	"log"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/jackc/pgx/v5"
	"github.com/labstack/echo/v4"
)

type DlpHandler struct {
	svc *services.DlpService
}

func NewDlpHandler(svc *services.DlpService) *DlpHandler {
	return &DlpHandler{svc: svc}
}

// ListActiveRules — DeviceAuth GET /dlp-rules/active
func (h *DlpHandler) ListActiveRules(c echo.Context) error {
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	rules, err := h.svc.ListActiveRules(c.Request().Context(), empID)
	if err != nil {
		log.Printf("[dlp] ListActiveRules: %v", err)
		return errorResponse(c, http.StatusInternalServerError, "Failed to list active rules", err)
	}
	if rules == nil {
		rules = []models.DlpRule{}
	}
	return c.JSON(http.StatusOK, map[string]interface{}{"rules": rules, "total": len(rules)})
}

// SyncAlerts — DeviceAuth POST /dlp-alerts/sync
func (h *DlpHandler) SyncAlerts(c echo.Context) error {
	var req dto.SyncDlpAlertsRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	var deviceID *string
	if v, ok := c.Get("device_id").(string); ok && v != "" {
		deviceID = &v
	}
	synced, err := h.svc.SyncAlerts(c.Request().Context(), empID, deviceID, req.Entries)
	if err != nil {
		log.Printf("[dlp] SyncAlerts: %v", err)
		return errorResponse(c, http.StatusInternalServerError, "Failed to sync alerts", err)
	}
	return c.JSON(http.StatusOK, dto.SyncBatchResponse{Synced: synced, Message: "ok"})
}

// ListRules — JWT GET /dlp-rules
func (h *DlpHandler) ListRules(c echo.Context) error {
	rules, err := h.svc.ListRules(c.Request().Context())
	if err != nil {
		log.Printf("[dlp] ListRules: %v", err)
		return errorResponse(c, http.StatusInternalServerError, "Failed to list rules", err)
	}
	if rules == nil {
		rules = []models.DlpRule{}
	}
	return c.JSON(http.StatusOK, map[string]interface{}{"data": rules, "total": len(rules)})
}

// CreateRule — JWT POST /dlp-rules
func (h *DlpHandler) CreateRule(c echo.Context) error {
	var req dto.CreateDlpRuleRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	created, err := h.svc.CreateRule(c.Request().Context(), req)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to create rule", err)
	}
	return c.JSON(http.StatusCreated, created)
}

// UpdateRule — JWT PUT /dlp-rules/:id
func (h *DlpHandler) UpdateRule(c echo.Context) error {
	id := c.Param("id")
	if id == "" {
		return errorResponse(c, http.StatusBadRequest, "id is required", nil)
	}
	var req dto.UpdateDlpRuleRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	updated, err := h.svc.UpdateRule(c.Request().Context(), id, req)
	if err != nil {
		if err == pgx.ErrNoRows {
			return errorResponse(c, http.StatusNotFound, "Rule not found", err)
		}
		return errorResponse(c, http.StatusBadRequest, "Failed to update rule", err)
	}
	return c.JSON(http.StatusOK, updated)
}

// DeleteRule — JWT DELETE /dlp-rules/:id
func (h *DlpHandler) DeleteRule(c echo.Context) error {
	id := c.Param("id")
	if err := h.svc.DeleteRule(c.Request().Context(), id); err != nil {
		if err == pgx.ErrNoRows {
			return errorResponse(c, http.StatusNotFound, "Rule not found", err)
		}
		return errorResponse(c, http.StatusInternalServerError, "Failed to delete rule", err)
	}
	return c.JSON(http.StatusOK, map[string]string{"message": "rule deleted"})
}

// ListAlerts — JWT GET /dlp-alerts
func (h *DlpHandler) ListAlerts(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))
	params := repository.DlpAlertListParams{
		Status:     strings.ToLower(strings.TrimSpace(c.QueryParam("status"))),
		Severity:   strings.ToLower(strings.TrimSpace(c.QueryParam("severity"))),
		Trigger:    strings.ToLower(strings.TrimSpace(c.QueryParam("trigger"))),
		EmployeeID: strings.TrimSpace(c.QueryParam("employeeId")),
		Search:     strings.TrimSpace(c.QueryParam("q")),
		Page:       page,
		PerPage:    perPage,
	}
	if v := c.QueryParam("dateFrom"); v != "" {
		if t, err := parseFlexibleTime(v); err == nil {
			params.DateFrom = &t
		}
	}
	if v := c.QueryParam("dateTo"); v != "" {
		if t, err := parseFlexibleTime(v); err == nil {
			// inclusive end-of-day for date-only
			if len(v) == 10 {
				end := t.Add(24*time.Hour - time.Nanosecond)
				params.DateTo = &end
			} else {
				params.DateTo = &t
			}
		}
	}
	result, err := h.svc.ListAlerts(c.Request().Context(), params)
	if err != nil {
		return errorResponse(c, http.StatusBadRequest, "Failed to list alerts", err)
	}
	return c.JSON(http.StatusOK, result)
}

// PatchAlert — JWT PATCH /dlp-alerts/:id
func (h *DlpHandler) PatchAlert(c echo.Context) error {
	id := c.Param("id")
	var req dto.PatchDlpAlertRequest
	if err := c.Bind(&req); err != nil {
		return errorResponse(c, http.StatusBadRequest, "Invalid request body", err)
	}
	updated, err := h.svc.PatchAlert(c.Request().Context(), id, req)
	if err != nil {
		if err == pgx.ErrNoRows {
			return errorResponse(c, http.StatusNotFound, "Alert not found", err)
		}
		return errorResponse(c, http.StatusBadRequest, "Failed to update alert", err)
	}
	return c.JSON(http.StatusOK, updated)
}

func parseFlexibleTime(v string) (time.Time, error) {
	if t, err := time.Parse(time.RFC3339, v); err == nil {
		return t, nil
	}
	return time.ParseInLocation("2006-01-02", v, time.Local)
}
