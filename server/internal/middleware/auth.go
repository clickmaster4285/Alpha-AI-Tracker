package middleware

import (
	"net/http"
	"strings"

	"github.com/labstack/echo/v4"
	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/services"
)

const (
	authCookieName = "auth_token"
)

// JWTAuth returns an Echo middleware that validates JWT from cookie or Authorization header.
func JWTAuth(authService *services.AuthService) echo.MiddlewareFunc {
	return func(next echo.HandlerFunc) echo.HandlerFunc {
		return func(c echo.Context) error {
			tokenString := ""

			// Try cookie first
			cookie, err := c.Cookie(authCookieName)
			if err == nil && cookie.Value != "" {
				tokenString = cookie.Value
			}

			// Fallback to Authorization header (for API clients)
			if tokenString == "" {
				authHeader := c.Request().Header.Get("Authorization")
				if strings.HasPrefix(authHeader, "Bearer ") {
					tokenString = strings.TrimPrefix(authHeader, "Bearer ")
				}
			}

			if tokenString == "" {
				return c.JSON(http.StatusUnauthorized, dto.APIError{
					Code:    http.StatusUnauthorized,
					Message: "Authentication required",
				})
			}

			claims, err := authService.ValidateToken(tokenString)
			if err != nil {
				return c.JSON(http.StatusUnauthorized, dto.APIError{
					Code:    http.StatusUnauthorized,
					Message: "Invalid or expired token",
				})
			}

			// Set user info in context
			c.Set("user_id", claims.UserID)
			c.Set("user_email", claims.Email)
			c.Set("user_role", claims.Role)

			return next(c)
		}
	}
}

// OptionalAuth is like JWTAuth but doesn't fail if no token is provided.
// Useful for endpoints where auth is optional.
func OptionalAuth(authService *services.AuthService) echo.MiddlewareFunc {
	return func(next echo.HandlerFunc) echo.HandlerFunc {
		return func(c echo.Context) error {
			tokenString := ""

			cookie, err := c.Cookie(authCookieName)
			if err == nil && cookie.Value != "" {
				tokenString = cookie.Value
			}

			if tokenString == "" {
				authHeader := c.Request().Header.Get("Authorization")
				if strings.HasPrefix(authHeader, "Bearer ") {
					tokenString = strings.TrimPrefix(authHeader, "Bearer ")
				}
			}

			if tokenString != "" {
				claims, err := authService.ValidateToken(tokenString)
				if err == nil {
					c.Set("user_id", claims.UserID)
					c.Set("user_email", claims.Email)
					c.Set("user_role", claims.Role)
				}
			}

			return next(c)
		}
	}
}
