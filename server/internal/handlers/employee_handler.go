package handlers

import (
	"net/http"
	"strconv"

	"github.com/labstack/echo/v4"
	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
)

// EmployeeHandler handles employee CRUD endpoints.
type EmployeeHandler struct {
	employeeService *services.EmployeeService
}

// NewEmployeeHandler creates a new EmployeeHandler.
func NewEmployeeHandler(employeeService *services.EmployeeService) *EmployeeHandler {
	return &EmployeeHandler{employeeService: employeeService}
}

// ListEmployees handles GET /api/v1/employees
func (h *EmployeeHandler) ListEmployees(c echo.Context) error {
	page, _ := strconv.Atoi(c.QueryParam("page"))
	perPage, _ := strconv.Atoi(c.QueryParam("perPage"))

	params := repository.EmployeeListParams{
		Search:     c.QueryParam("search"),
		Department: c.QueryParam("department"),
		Role:       c.QueryParam("role"),
		Status:     c.QueryParam("status"),
		Page:       page,
		PerPage:    perPage,
	}

	result, err := h.employeeService.List(c.Request().Context(), params)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to list employees",
			Detail:  err.Error(),
		})
	}

	return c.JSON(http.StatusOK, result)
}

// GetEmployee handles GET /api/v1/employees/:id
func (h *EmployeeHandler) GetEmployee(c echo.Context) error {
	id := c.Param("id")

	emp, err := h.employeeService.GetByID(c.Request().Context(), id)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to get employee",
			Detail:  err.Error(),
		})
	}
	if emp == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{
			Code:    http.StatusNotFound,
			Message: "Employee not found",
		})
	}

	return c.JSON(http.StatusOK, emp)
}

// CreateEmployee handles POST /api/v1/employees
func (h *EmployeeHandler) CreateEmployee(c echo.Context) error {
	var req dto.CreateEmployeeRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	if req.Name == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Name is required",
		})
	}

	emp, err := h.employeeService.Create(c.Request().Context(), &req)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to create employee",
			Detail:  err.Error(),
		})
	}

	return c.JSON(http.StatusCreated, emp)
}

// UpdateEmployee handles PUT /api/v1/employees/:id
func (h *EmployeeHandler) UpdateEmployee(c echo.Context) error {
	id := c.Param("id")

	var req dto.UpdateEmployeeRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	emp, err := h.employeeService.Update(c.Request().Context(), id, &req)
	if err != nil {
		code := http.StatusInternalServerError
		return c.JSON(code, dto.APIError{
			Code:    code,
			Message: "Failed to update employee",
			Detail:  err.Error(),
		})
	}
	if emp == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{
			Code:    http.StatusNotFound,
			Message: "Employee not found",
		})
	}

	return c.JSON(http.StatusOK, emp)
}

// DeleteEmployee handles DELETE /api/v1/employees/:id
func (h *EmployeeHandler) DeleteEmployee(c echo.Context) error {
	id := c.Param("id")

	if err := h.employeeService.Delete(c.Request().Context(), id); err != nil {
		code := http.StatusInternalServerError
		if err.Error() == "employee not found" {
			code = http.StatusNotFound
		}
		return c.JSON(code, dto.APIError{
			Code:    code,
			Message: err.Error(),
		})
	}

	return c.JSON(http.StatusOK, map[string]interface{}{
		"message": "employee deleted successfully",
	})
}

// GenerateSecret handles POST /api/v1/employees/:id/generate-secret
func (h *EmployeeHandler) GenerateSecret(c echo.Context) error {
	id := c.Param("id")

	resp, err := h.employeeService.GenerateSecret(c.Request().Context(), id)
	if err != nil {
		code := http.StatusInternalServerError
		if err.Error() == "employee not found" {
			code = http.StatusNotFound
		}
		return c.JSON(code, dto.APIError{
			Code:    code,
			Message: err.Error(),
		})
	}

	return c.JSON(http.StatusOK, resp)
}
