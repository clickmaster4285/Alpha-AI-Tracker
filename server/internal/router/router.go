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
	departmentHandler *handlers.DepartmentHandler,
	newSchemaHandler *handlers.NewSchemaHandler,
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
	a := e.Group("/api/v1/auth")
	a.POST("/login", authHandler.Login)
	a.POST("/employee-login", authHandler.EmployeeLogin)

	// Phase 1 sync endpoints — authenticated by employee token in body (not cookie)
	e.POST("/api/v1/device-hardware/sync", newSchemaHandler.SyncDeviceHardware)
	e.POST("/api/v1/installed-apps/sync", newSchemaHandler.SyncInstalledApps)
	e.POST("/api/v1/installed-packages/sync", newSchemaHandler.SyncInstalledPackages)
	e.POST("/api/v1/network-info/sync", newSchemaHandler.SyncNetworkInfo)
	e.POST("/api/v1/session-events/sync", newSchemaHandler.SyncSessionEvents)

	// Phase 2 sync endpoints
	e.POST("/api/v1/app-sessions/sync", newSchemaHandler.SyncAppSessions)
	e.POST("/api/v1/app-items/sync", newSchemaHandler.SyncAppItems)

	// ─────────────────────────────
	// Semi-Protected Routes (optional auth)
	// ─────────────────────────────
	semiProtected := e.Group("/api/v1")
	semiProtected.Use(appMiddleware.OptionalAuth(authService))

	semiProtected.GET("/auth/check", authHandler.CheckAuth)

	// ─────────────────────────────
	// Protected Routes (auth required)
	// ─────────────────────────────
	protected := e.Group("/api/v1")
	protected.Use(appMiddleware.JWTAuth(authService))

	// Auth
	protected.GET("/auth/me", authHandler.Me)
	protected.POST("/auth/logout", authHandler.Logout)

	// Users
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

	// App Sessions listing (protected — web admin access, replaces old activity-logs)
	protected.GET("/app-sessions", newSchemaHandler.ListAppSessions)

	// App Items listing (protected — web admin access, shows browser URLs, file paths, etc.)
	protected.GET("/app-items", newSchemaHandler.ListAppItems)

	// Departments
	depts := protected.Group("/departments")
	depts.GET("", departmentHandler.ListDepartments)
	depts.POST("", departmentHandler.CreateDepartment)
	depts.PUT("/:id", departmentHandler.UpdateDepartment)
	depts.DELETE("/:id", departmentHandler.DeleteDepartment)
}
