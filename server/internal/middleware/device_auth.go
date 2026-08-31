package middleware

import (
	"crypto/sha256"
	"encoding/hex"
	"net/http"
	"strings"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/services"
	"github.com/labstack/echo/v4"
)

// DeviceAuth creates middleware that authenticates device tokens (or fallback employee JWTs)
// and attaches employee_id and device_id to Echo context.
func DeviceAuth(deviceRepo *repository.DeviceRepo, authService *services.AuthService) echo.MiddlewareFunc {
	return func(next echo.HandlerFunc) echo.HandlerFunc {
		return func(c echo.Context) error {
			authHeader := c.Request().Header.Get("Authorization")
			var token string

			if authHeader != "" {
				parts := strings.SplitN(authHeader, " ", 2)
				if len(parts) == 2 && (strings.EqualFold(parts[0], "Device") || strings.EqualFold(parts[0], "Bearer")) {
					token = strings.TrimSpace(parts[1])
				}
			}

			// Fallback: check query parameter or request header directly
			if token == "" {
				token = c.QueryParam("token")
			}

			if token == "" {
				return c.JSON(http.StatusUnauthorized, dto.APIError{
					Code:    http.StatusUnauthorized,
					Message: "Missing Authorization header or device token",
				})
			}

			// 1. Attempt device token lookup by SHA-256 hash
			hasher := sha256.New()
			hasher.Write([]byte(token))
			tokenHash := hex.EncodeToString(hasher.Sum(nil))

			device, err := deviceRepo.GetByTokenHash(c.Request().Context(), tokenHash)
			if err == nil && device != nil {
				// Touch last_seen timestamp in background
				go func(devID string) {
					_ = deviceRepo.TouchLastSeen(c.Request().Context(), devID)
				}(device.ID)

				c.Set("employee_id", device.EmployeeID)
				c.Set("device_id", device.ID)
				return next(c)
			}

			// 2. Fallback to JWT validation for backward compatibility
			if authService != nil {
				claims, err := authService.ValidateToken(token)
				if err == nil && claims != nil && claims.UserID != "" &&
					claims.Issuer == "alpha-ai-tracker-employee" {
					c.Set("employee_id", claims.UserID)
					return next(c)
				}
			}

			return c.JSON(http.StatusUnauthorized, dto.APIError{
				Code:    http.StatusUnauthorized,
				Message: "Invalid or revoked authorization credential",
			})
		}
	}
}
