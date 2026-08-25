package handlers

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"net/http"
	"time"

	"github.com/alpha-ai-tracker/server/internal/config"
	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

const (
	authCookieName = "auth_token"
	authCookiePath = "/"
)

// AuthHandler handles authentication endpoints.
type AuthHandler struct {
	authService  *services.AuthService
	employeeRepo *repository.EmployeeRepo
	deviceRepo   *repository.DeviceRepo
	redisClient  services.RedisClientInterface
	jwtCfg       config.JWTConfig
}

// NewAuthHandler creates a new AuthHandler.
func NewAuthHandler(
	authService *services.AuthService,
	employeeRepo *repository.EmployeeRepo,
	deviceRepo *repository.DeviceRepo,
	redisClient services.RedisClientInterface,
	jwtCfg config.JWTConfig,
) *AuthHandler {
	return &AuthHandler{
		authService:  authService,
		employeeRepo: employeeRepo,
		deviceRepo:   deviceRepo,
		redisClient:  redisClient,
		jwtCfg:       jwtCfg,
	}
}

// Login handles POST /api/v1/auth/login
func (h *AuthHandler) Login(c echo.Context) error {
	var req dto.LoginRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	if req.Email == "" || req.Password == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Email and password are required",
		})
	}

	resp, err := h.authService.Login(c.Request().Context(), &req)
	if err != nil {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code:    http.StatusUnauthorized,
			Message: err.Error(),
		})
	}

	// Set httpOnly secure cookie
	cookie := &http.Cookie{
		Name:     authCookieName,
		Value:    resp.Token,
		Path:     authCookiePath,
		HttpOnly: true,
		Secure:   false, // Set to true in production with HTTPS
		SameSite: http.SameSiteLaxMode,
		Expires:  time.Now().Add(h.jwtCfg.AccessExpiry),
	}
	c.SetCookie(cookie)

	return c.JSON(http.StatusOK, dto.LoginResponse{
		User: resp.User,
	})
}

// Logout handles POST /api/v1/auth/logout
func (h *AuthHandler) Logout(c echo.Context) error {
	// Clear the auth cookie
	cookie := &http.Cookie{
		Name:     authCookieName,
		Value:    "",
		Path:     authCookiePath,
		HttpOnly: true,
		Secure:   false,
		SameSite: http.SameSiteLaxMode,
		MaxAge:   -1,
		Expires:  time.Unix(0, 0),
	}
	c.SetCookie(cookie)

	return c.JSON(http.StatusOK, map[string]interface{}{
		"message": "logged out successfully",
	})
}

// Me handles GET /api/v1/auth/me — returns current user (with role permissions) from cookie
func (h *AuthHandler) Me(c echo.Context) error {
	userID, ok := c.Get("user_id").(string)
	if !ok || userID == "" {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code:    http.StatusUnauthorized,
			Message: "Authentication required",
		})
	}

	user, err := h.authService.GetUserResponseByID(c.Request().Context(), userID)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to get user",
		})
	}
	if user == nil {
		return c.JSON(http.StatusNotFound, dto.APIError{
			Code:    http.StatusNotFound,
			Message: "User not found",
		})
	}

	return c.JSON(http.StatusOK, user)
}

// CheckAuth handles GET /api/v1/auth/check — lightweight auth status check (optional auth)
func (h *AuthHandler) CheckAuth(c echo.Context) error {
	userID, ok := c.Get("user_id").(string)
	if !ok || userID == "" {
		return c.JSON(http.StatusOK, dto.AuthCheckResponse{
			Authenticated: false,
		})
	}

	user, err := h.authService.GetUserResponseByID(c.Request().Context(), userID)
	if err != nil || user == nil {
		return c.JSON(http.StatusOK, dto.AuthCheckResponse{
			Authenticated: false,
		})
	}

	return c.JSON(http.StatusOK, dto.AuthCheckResponse{
		Authenticated: true,
		User:          user,
	})
}

// EmployeeLogin handles POST /api/v1/auth/employee-login
func (h *AuthHandler) EmployeeLogin(c echo.Context) error {
	var req dto.EmployeeLoginRequest
	if err := c.Bind(&req); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Invalid request body",
		})
	}

	if req.EmployeeID == "" || req.SecretKey == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{
			Code:    http.StatusBadRequest,
			Message: "Employee ID and secret key are required",
		})
	}

	// Check Redis is available
	if h.redisClient == nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Employee authentication is unavailable (Redis not connected)",
		})
	}

	// Validate secret from Redis
	valid, err := h.redisClient.ValidateSecret(c.Request().Context(), req.EmployeeID, req.SecretKey)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to validate secret",
			Detail:  err.Error(),
		})
	}
	if !valid {
		return c.JSON(http.StatusUnauthorized, dto.APIError{
			Code:    http.StatusUnauthorized,
			Message: "Invalid or expired secret key",
		})
	}

	// Look up employee
	emp, err := h.employeeRepo.GetByEmployeeID(c.Request().Context(), req.EmployeeID)
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

	// Generate JWT for employee session
	token, err := h.authService.GenerateEmployeeToken(emp)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{
			Code:    http.StatusInternalServerError,
			Message: "Failed to generate token",
			Detail:  err.Error(),
		})
	}

	// Update employee tracking status to tracked and set online
	updates := map[string]interface{}{
		"tracking_status": "tracked",
		"is_online":       true,
	}
	if _, err := h.employeeRepo.Update(c.Request().Context(), emp.ID, updates); err != nil {
		// Non-fatal — log but don't block login
		c.Logger().Errorf("failed to update employee tracking status: %v", err)
	}

	// Update in-memory struct for the response (avoid re-fetch from DB)
	emp.TrackingStatus = "tracked"
	emp.IsOnline = true

	resp := dto.EmployeeLoginResponse{
		Employee: dto.EmployeeResponse{
			ID:              emp.ID,
			EmployeeID:      emp.EmployeeID,
			Name:            emp.Name,
			Email:           emp.Email,
			Department:      emp.Department,
			DepartmentID:    emp.DepartmentID,
			Shift:           emp.Shift,
			TrackingEnabled: emp.TrackingEnabled,
			TrackingStatus:  emp.TrackingStatus,
			IsOnline:        emp.IsOnline,
			Avatar:          emp.Avatar,
			AvatarColor:     emp.AvatarColor,
			CreatedAt:       emp.CreatedAt,
			UpdatedAt:       emp.UpdatedAt,
		},
		Token: token,
	}

	// Issue long-lived device token if deviceRepo is available
	if h.deviceRepo != nil {
		tokenBytes := make([]byte, 32)
		if _, err := rand.Read(tokenBytes); err == nil {
			rawDeviceToken := "dev_tok_" + hex.EncodeToString(tokenBytes)
			hasher := sha256.New()
			hasher.Write([]byte(rawDeviceToken))
			tokenHash := hex.EncodeToString(hasher.Sum(nil))

			machineID := req.MachineID
			if machineID == "" {
				machineID = "default-" + emp.EmployeeID
			}
			platform := req.Platform
			if platform == "" {
				platform = "unknown"
			}
			clientVer := req.ClientVersion
			if clientVer == "" {
				clientVer = "1.0.0"
			}

			device, err := h.deviceRepo.UpsertDevice(
				c.Request().Context(),
				emp.EmployeeID,
				machineID,
				platform,
				clientVer,
				req.DeviceName,
				tokenHash,
				nil, // non-expiring by default (revocable)
			)
			if err == nil && device != nil {
				resp.DeviceToken = rawDeviceToken
				resp.DeviceID = device.ID
			}
		}
	}

	return c.JSON(http.StatusOK, resp)
}

// ListEmployeeDevices handles GET /api/v1/employees/:id/devices (web admin)
func (h *AuthHandler) ListEmployeeDevices(c echo.Context) error {
	employeeID := c.Param("id")
	if employeeID == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Employee ID is required"})
	}

	if h.deviceRepo == nil {
		return c.JSON(http.StatusOK, []interface{}{})
	}

	devices, err := h.deviceRepo.ListByEmployeeID(c.Request().Context(), employeeID)
	if err != nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Failed to list devices", Detail: err.Error()})
	}

	return c.JSON(http.StatusOK, devices)
}

// RevokeDevice handles POST /api/v1/devices/:id/revoke (web admin)
func (h *AuthHandler) RevokeDevice(c echo.Context) error {
	deviceID := c.Param("id")
	if deviceID == "" {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Device ID is required"})
	}

	if h.deviceRepo == nil {
		return c.JSON(http.StatusInternalServerError, dto.APIError{Code: http.StatusInternalServerError, Message: "Device repository unavailable"})
	}

	if err := h.deviceRepo.RevokeDevice(c.Request().Context(), deviceID); err != nil {
		return c.JSON(http.StatusBadRequest, dto.APIError{Code: http.StatusBadRequest, Message: "Failed to revoke device", Detail: err.Error()})
	}

	return c.JSON(http.StatusOK, map[string]string{"message": "Device access revoked successfully"})
}


