package handlers

import (
	"log"
	"net/http"
	"strconv"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

// NewSchemaHandler handles ingestion and listing endpoints for Phase 1 & 2 tables.
type NewSchemaHandler struct {
	service     *services.NewSchemaService
	authService *services.AuthService
}

func NewNewSchemaHandler(service *services.NewSchemaService, authService *services.AuthService) *NewSchemaHandler {
	return &NewSchemaHandler{service: service, authService: authService}
}

// Helper to extract server-authenticated employee ID from Echo context
func getAuthenticatedEmployeeID(c echo.Context) (string, error) {
	employeeID, ok := c.Get("employee_id").(string)
	if !ok || employeeID == "" {
		return "", c.JSON(http.StatusUnauthorized, dto.APIError{Code: http.StatusUnauthorized, Message: "Unauthorized employee context"})
	}
	return employeeID, nil
}

// ────────────────────────────────
// Phase 1: Device Hardware Info
// ────────────────────────────────

func (h *NewSchemaHandler) SyncDeviceHardware(c echo.Context) error {
	var req dto.SyncDeviceHardwareRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID

	resp, err := h.service.SyncDeviceHardware(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncDeviceHardware error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

// ────────────────────────────────
// Phase 1: Installed Applications
// ────────────────────────────────

func (h *NewSchemaHandler) SyncInstalledApps(c echo.Context) error {
	var req dto.SyncInstalledAppsRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID

	resp, err := h.service.SyncInstalledApps(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncInstalledApps error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

// ────────────────────────────────
// Phase 1: Installed Packages
// ────────────────────────────────

func (h *NewSchemaHandler) SyncInstalledPackages(c echo.Context) error {
	var req dto.SyncInstalledPackagesRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID

	resp, err := h.service.SyncInstalledPackages(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncInstalledPackages error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

// ────────────────────────────────
// Phase 1: Network Info
// ────────────────────────────────

func (h *NewSchemaHandler) SyncNetworkInfo(c echo.Context) error {
	var req dto.SyncNetworkInfoRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID

	resp, err := h.service.SyncNetworkInfo(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncNetworkInfo error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

// ────────────────────────────────
// Phase 1: Session Events
// ────────────────────────────────

func (h *NewSchemaHandler) SyncSessionEvents(c echo.Context) error {
	var req dto.SyncSessionEventsRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID

	resp, err := h.service.SyncSessionEvents(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncSessionEvents error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

// ────────────────────────────────
// Phase 2: App Sessions
// ────────────────────────────────

func (h *NewSchemaHandler) SyncAppSessions(c echo.Context) error {
	var req dto.SyncAppSessionsRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID

	resp, err := h.service.SyncAppSessions(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncAppSessions error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

// ────────────────────────────────
// App Items (replaces old separate child tables)
// ────────────────────────────────

func (h *NewSchemaHandler) SyncAppItems(c echo.Context) error {
	var req dto.SyncAppItemsRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID

	resp, err := h.service.SyncAppItems(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncAppItems error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

// ────────────────────────────────
// List App Sessions (web dashboard)
// ────────────────────────────────

func (h *NewSchemaHandler) ListAppSessions(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))

	params := repository.AppSessionListParams{
		EmployeeID: c.QueryParam("employeeId"),
		Search:     c.QueryParam("search"),
		Platform:   c.QueryParam("platform"),
		DateFrom:   parseTimeParam(c.QueryParam("dateFrom")),
		DateTo:     parseTimeParam(c.QueryParam("dateTo")),
		Page:       page,
		PerPage:    perPage,
	}

	result, err := h.service.ListAppSessions(c.Request().Context(), params)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list app sessions",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, result)
}

// ────────────────────────────────
// List App Items (browser URLs, file paths, etc.)
// ────────────────────────────────

func (h *NewSchemaHandler) ListAppItems(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))

	params := repository.AppItemListParams{
		EmployeeID:   c.QueryParam("employeeId"),
		AppSessionID: c.QueryParam("appSessionId"),
		ItemType:     c.QueryParam("itemType"),
		Search:       c.QueryParam("search"),
		DateFrom:     parseTimeParam(c.QueryParam("dateFrom")),
		DateTo:       parseTimeParam(c.QueryParam("dateTo")),
		Page:         page,
		PerPage:      perPage,
	}

	result, err := h.service.ListAppItems(c.Request().Context(), params)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list app items",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, result)
}

// parseTimeParam parses an optional RFC3339 (or date-only) query param into a
// time.Time. Returns the zero value when empty/invalid (meaning "no bound").
func parseTimeParam(v string) time.Time {
	if v == "" {
		return time.Time{}
	}
	for _, layout := range []string{time.RFC3339, "2006-01-02"} {
		if t, err := time.Parse(layout, v); err == nil {
			return t
		}
	}
	return time.Time{}
}

// ────────────────────────────────
// Phase 3: App Status
// ────────────────────────────────

func (h *NewSchemaHandler) SyncAppStatus(c echo.Context) error {
	var req dto.SyncAppStatusRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID

	resp, err := h.service.SyncAppStatus(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncAppStatus error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

// ────────────────────────────────
// Phase 3: Hardware Devices
// ────────────────────────────────

func (h *NewSchemaHandler) SyncHardwareDevices(c echo.Context) error {
	var req dto.SyncHardwareDevicesRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID

	resp, err := h.service.SyncHardwareDevices(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncHardwareDevices error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

// ────────────────────────────────
// Phase 3: Permission Status
// ────────────────────────────────

func (h *NewSchemaHandler) SyncPermissionStatus(c echo.Context) error {
	var req dto.SyncPermissionStatusRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID

	resp, err := h.service.SyncPermissionStatus(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncPermissionStatus error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

// ────────────────────────────────
// Phase 3: Storage Devices
// ────────────────────────────────

func (h *NewSchemaHandler) SyncStorageDevices(c echo.Context) error {
	var req dto.SyncStorageDevicesRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Invalid request body"})
	}
	empID, errResp := getAuthenticatedEmployeeID(c)
	if errResp != nil {
		return errResp
	}
	req.EmployeeID = empID

	resp, err := h.service.SyncStorageDevices(c.Request().Context(), &req)
	if err != nil {
		log.Printf("[new_schema] SyncStorageDevices error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to sync", Detail: err.Error()})
	}
	return c.JSON(http.StatusOK, resp)
}

// ────────────────────────────────
// Employee Detail (web dashboard — GET /employees/:id/detail)
// ────────────────────────────────

func (h *NewSchemaHandler) GetEmployeeDetail(c echo.Context) error {
	detail, err := h.service.GetEmployeeDetail(c.Request().Context(), c.Param("id"))
	if err != nil {
		if err.Error() == "employee not found" {
			return c.JSON(http.StatusNotFound, dto.APIError{Code: http.StatusNotFound, Message: "Employee not found"})
		}
		log.Printf("[new_schema] GetEmployeeDetail error: %v", err)
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to load employee detail",
			Detail:  err.Error(),
		})
	}
	return c.JSON(http.StatusOK, detail)
}
