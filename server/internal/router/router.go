package router

import (
	"github.com/labstack/echo/v4"
	"github.com/labstack/echo/v4/middleware"
	"github.com/alpha-ai-tracker/server/internal/config"
	"github.com/alpha-ai-tracker/server/internal/handlers"
	appMiddleware "github.com/alpha-ai-tracker/server/internal/middleware"
	"github.com/alpha-ai-tracker/server/internal/services"
)

// Setup configures all routes and middleware on the Echo instance.
func Setup(
	e *echo.Echo,
	cfg *config.Config,
	authService *services.AuthService,
	authHandler *handlers.AuthHandler,
	userHandler *handlers.UserHandler,
	employeeHandler *handlers.EmployeeHandler,
) {
	// ─────────────────────────────
	// Global Middleware
	// ─────────────────────────────
	e.Use(middleware.LoggerWithConfig(middleware.LoggerConfig{
		Format: `{"time":"${time_rfc3339}","method":"${method}","uri":"${uri}","status":${status},"latency":"${latency_human}"}` + "\n",
	}))
	e.Use(middleware.Recover())

	// CORS — allow frontend origin with credentials
	e.Use(middleware.CORSWithConfig(middleware.CORSConfig{
		AllowOrigins:     cfg.CORS.AllowedOrigins,
		AllowMethods:     []string{echo.GET, echo.POST, echo.PUT, echo.DELETE, echo.OPTIONS},
		AllowHeaders:     []string{"Content-Type", "Authorization", "X-Requested-With"},
		AllowCredentials: true,
		MaxAge:           300,
	}))

	// ─────────────────────────────
	// Health Check
	// ─────────────────────────────
	e.GET("/api/v1/health", func(c echo.Context) error {
		return c.JSON(200, map[string]string{
			"status":    "ok",
			"timestamp": c.RealIP(),
		})
	})

	// ─────────────────────────────
	// Public Routes (no auth required)
	// ─────────────────────────────
	auth := e.Group("/api/v1/auth")
	auth.POST("/login", authHandler.Login)
	auth.POST("/employee-login", authHandler.EmployeeLogin) // employee desktop client login

	// ─────────────────────────────
	// Semi-Protected Routes (optional auth)
	// ─────────────────────────────
	semiProtected := e.Group("/api/v1")
	semiProtected.Use(appMiddleware.OptionalAuth(authService))

	// Auth check — returns {authenticated: false} gracefully when no cookie
	semiProtected.GET("/auth/check", authHandler.CheckAuth)

	// ─────────────────────────────
	// Protected Routes (auth required)
	// ─────────────────────────────
	protected := e.Group("/api/v1")
	protected.Use(appMiddleware.JWTAuth(authService))

	// Auth
	protected.GET("/auth/me", authHandler.Me)
	protected.POST("/auth/logout", authHandler.Logout)

	// Users (admin users only)
	users := protected.Group("/users")
	users.GET("", userHandler.ListUsers)
	users.GET("/:id", userHandler.GetUser)
	users.POST("", userHandler.CreateUser)
	users.PUT("/:id", userHandler.UpdateUser)
	users.DELETE("/:id", userHandler.DeleteUser)

	// Employees
	employees := protected.Group("/employees")
	employees.GET("", employeeHandler.ListEmployees)
	employees.GET("/:id", employeeHandler.GetEmployee)
	employees.POST("", employeeHandler.CreateEmployee)
	employees.PUT("/:id", employeeHandler.UpdateEmployee)
	employees.DELETE("/:id", employeeHandler.DeleteEmployee)
	employees.POST("/:id/generate-secret", employeeHandler.GenerateSecret)

	// Departments
	protected.GET("/departments", userHandler.GetDepartments)
}
