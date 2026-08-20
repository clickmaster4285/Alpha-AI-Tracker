package main

import (
	"context"
	"log"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	"github.com/labstack/echo/v4"
	"github.com/alpha-ai-tracker/server/internal/config"
	"github.com/alpha-ai-tracker/server/internal/database"
	"github.com/alpha-ai-tracker/server/internal/handlers"
	"github.com/alpha-ai-tracker/server/internal/jobs"
	goredis "github.com/alpha-ai-tracker/server/internal/redis"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"github.com/alpha-ai-tracker/server/internal/router"
	"github.com/alpha-ai-tracker/server/internal/services"
)

func main() {
	log.SetFlags(log.LstdFlags | log.Lshortfile)
	log.Println("[server] Alpha AI Tracker — Starting...")

	// ────────────────
	// Load Config
	// ────────────────
	cfg, err := config.Load()
	if err != nil {
		log.Fatalf("[server] failed to load config: %v", err)
	}
	log.Printf("[server] configured for %s", cfg.ServerAddr())

	// ────────────────
	// Database
	// ────────────────
	pool, err := database.NewPool(cfg.Database)
	if err != nil {
		log.Fatalf("[server] database connection failed: %v", err)
	}
	defer pool.Close()

	// Run migrations
	migrationsDir := findMigrationsDir()
	if err := database.RunMigrations(pool, migrationsDir); err != nil {
		log.Fatalf("[server] migration failed: %v", err)
	}

	// ────────────────
	// Redis
	// ────────────────
	redisAddr := cfg.Redis.Host + ":" + itoa(cfg.Redis.Port)
	redisClient, err := goredis.NewClient(redisAddr, cfg.Redis.Password, cfg.Redis.DB)
	if err != nil {
		log.Printf("[server] WARNING: Redis connection failed: %v — employee secrets will not work", err)
		redisClient = nil
	}
	if redisClient != nil {
		defer redisClient.Close()
		log.Printf("[server] connected to Redis at %s", redisAddr)
	}

	// ────────────────
	// Dependencies (DI)
	// ────────────────
	userRepo := repository.NewUserRepo(pool)
	employeeRepo := repository.NewEmployeeRepo(pool)
	deviceRepo := repository.NewDeviceRepo(pool)
	departmentRepo := repository.NewDepartmentRepo(pool)
	newSchemaRepo := repository.NewNewSchemaRepo(pool)
	monitoringRepo := repository.NewMonitoringRepo(pool)

	authService := services.NewAuthService(userRepo, cfg.JWT, cfg.Admin)
	userService := services.NewUserService(userRepo)
	employeeService := services.NewEmployeeService(employeeRepo, redisClient)
	departmentService := services.NewDepartmentService(departmentRepo, employeeRepo)
	newSchemaService := services.NewNewSchemaService(newSchemaRepo, employeeRepo)
	monitoringService := services.NewMonitoringService(monitoringRepo)

	// Cast Redis client to interface
	var redisInterface services.RedisClientInterface
	if redisClient != nil {
		redisInterface = redisClient
	}

	authHandler := handlers.NewAuthHandler(authService, employeeRepo, deviceRepo, redisInterface, cfg.JWT)
	userHandler := handlers.NewUserHandler(userService)
	employeeHandler := handlers.NewEmployeeHandler(employeeService)
	departmentHandler := handlers.NewDepartmentHandler(departmentService)
	newSchemaHandler := handlers.NewNewSchemaHandler(newSchemaService, authService)
	monitoringHandler := handlers.NewMonitoringHandler(monitoringService)

	// ────────────────
	// Auto-initialize Company Admin
	// ────────────────
	ctx := context.Background()
	if err := authService.EnsureCompanyAdmin(ctx); err != nil {
		log.Fatalf("[server] failed to ensure company admin: %v", err)
	}

	// ────────────────
	// Background jobs
	// ────────────────
	sweepCtx, sweepCancel := context.WithCancel(context.Background())
	defer sweepCancel()
	stalenessSweep := jobs.NewStalenessSweep(pool, cfg.LinkStaleDays)
	stalenessSweep.Start(sweepCtx)
	log.Printf("[server] staleness sweep started (stale window: %d days)", cfg.LinkStaleDays)

	retentionWorker := jobs.NewRetentionWorker(pool)
	go retentionWorker.Start(sweepCtx)

	// ────────────────
	// Setup Echo
	// ────────────────
	e := echo.New()
	e.HideBanner = true
	e.HidePort = true

	router.Setup(e, cfg, authService, deviceRepo, authHandler, userHandler, employeeHandler, departmentHandler, newSchemaHandler, monitoringHandler)

	// ────────────────
	// Graceful Shutdown
	// ────────────────
	go func() {
		log.Printf("[server] listening on %s", cfg.ServerAddr())
		if err := e.Start(cfg.ServerAddr()); err != nil {
			log.Printf("[server] server stopped: %v", err)
		}
	}()

	quit := make(chan os.Signal, 1)
	signal.Notify(quit, syscall.SIGINT, syscall.SIGTERM)
	<-quit

	log.Println("[server] shutting down gracefully...")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	if err := e.Shutdown(shutdownCtx); err != nil {
		log.Fatalf("[server] forced shutdown: %v", err)
	}
	log.Println("[server] stopped")
}

// findMigrationsDir locates the migrations directory relative to the binary.
func findMigrationsDir() string {
	candidates := []string{
		"migrations",
		"../../migrations",
		"../migrations",
		filepath.Join("server", "migrations"),
	}

	if exe, err := os.Executable(); err == nil {
		candidates = append(candidates, filepath.Join(filepath.Dir(exe), "migrations"))
		candidates = append(candidates, filepath.Join(filepath.Dir(exe), "..", "..", "migrations"))
	}

	for _, dir := range candidates {
		absDir, err := filepath.Abs(dir)
		if err != nil {
			continue
		}
		if info, err := os.Stat(absDir); err == nil && info.IsDir() {
			return absDir
		}
	}

	log.Println("[server] WARNING: migrations directory not found, using 'migrations'")
	return "migrations"
}

func itoa(i int) string {
	if i == 0 {
		return "0"
	}
	var buf [20]byte
	pos := len(buf)
	neg := i < 0
	if neg {
		i = -i
	}
	for i > 0 {
		pos--
		buf[pos] = byte('0' + i%10)
		i /= 10
	}
	if neg {
		pos--
		buf[pos] = '-'
	}
	return string(buf[pos:])
}
