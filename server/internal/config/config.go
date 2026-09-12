package config

import (
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/joho/godotenv"
)

// Config holds all configuration for the server.
type Config struct {
	Server   ServerConfig
	Database DatabaseConfig
	Redis    RedisConfig
	JWT      JWTConfig
	Admin    AdminConfig
	CORS     CORSConfig
	LogLevel string

	// LinkStaleDays is the staleness threshold (in days) after which an
	// employee↔app/package junction link is marked is_active = false.
	LinkStaleDays int

	// DefaultShiftTimezone is the IANA timezone applied to shifts that still
	// carry the migration default UTC (and used as the create fallback when the
	// client omits timezone). Empty = leave UTC unchanged.
	DefaultShiftTimezone string
}

type ServerConfig struct {
	Host string
	Port int
}

type DatabaseConfig struct {
	Host            string
	Port            int
	User            string
	Password        string
	Name            string
	SSLMode         string
	MaxOpenConns    int
	MaxIdleConns    int
	ConnMaxLifetime time.Duration
}

type JWTConfig struct {
	Secret string
	// AccessExpiry is the WEB ADMIN access-token TTL (auth_token cookie).
	// Short-lived by design; re-minted via the rotating refresh token.
	AccessExpiry time.Duration
	// RefreshExpiry is the WEB ADMIN refresh-token lifetime (refresh_token cookie,
	// rotated on every successful refresh).
	RefreshExpiry time.Duration
	// EmployeeAccessExpiry is the DESKTOP CLIENT session JWT TTL. Employees send it
	// in sync request bodies (no cookie), so it stays long-lived.
	EmployeeAccessExpiry time.Duration
}

type RedisConfig struct {
	Host     string
	Port     int
	Password string
	DB       int
}

type AdminConfig struct {
	Email    string
	Password string
	Name     string
}

type CORSConfig struct {
	AllowedOrigins []string
}

func (c *Config) ServerAddr() string {
	return fmt.Sprintf("%s:%d", c.Server.Host, c.Server.Port)
}

func (c *DatabaseConfig) DSN() string {
	return fmt.Sprintf(
		"postgres://%s:%s@%s:%d/%s?sslmode=%s",
		c.User, c.Password, c.Host, c.Port, c.Name, c.SSLMode,
	)
}

// Load reads configuration from environment variables (and .env file if present).
func Load() (*Config, error) {
	// Load .env file if it exists (don't fail if it doesn't)
	_ = godotenv.Load()

	cfg := &Config{
		Server: ServerConfig{
			Host: getEnv("SERVER_HOST", "0.0.0.0"),
			Port: getEnvInt("SERVER_PORT", 8080),
		},
		Database: DatabaseConfig{
			Host:            getEnv("DB_HOST", "localhost"),
			Port:            getEnvInt("DB_PORT", 5432),
			User:            getEnv("DB_USER", "alpha_ai"),
			Password:        getEnv("DB_PASSWORD", ""),
			Name:            getEnv("DB_NAME", "alpha_ai_tracker"),
			SSLMode:         getEnv("DB_SSLMODE", "disable"),
			MaxOpenConns:    getEnvInt("DB_MAX_OPEN_CONNS", 25),
			MaxIdleConns:    getEnvInt("DB_MAX_IDLE_CONNS", 10),
			ConnMaxLifetime: getEnvDuration("DB_CONN_MAX_LIFETIME", 5*time.Minute),
		},
		Redis: RedisConfig{
			Host:     getEnv("REDIS_HOST", "localhost"),
			Port:     getEnvInt("REDIS_PORT", 6379),
			Password: getEnv("REDIS_PASSWORD", ""),
			DB:       getEnvInt("REDIS_DB", 0),
		},
		JWT: JWTConfig{
			Secret:               getEnv("JWT_SECRET", ""),
			AccessExpiry:         getEnvDuration("JWT_ACCESS_EXPIRY", 15*time.Minute),
			RefreshExpiry:        getEnvDuration("JWT_REFRESH_EXPIRY", 30*24*time.Hour),
			EmployeeAccessExpiry: getEnvDuration("JWT_EMPLOYEE_ACCESS_EXPIRY", 24*time.Hour),
		},
		Admin: AdminConfig{
			Email:    getEnv("ADMIN_EMAIL", "admin@alphai.com"),
			Password: getEnv("ADMIN_PASSWORD", "AlphaAI@2024!"),
			Name:     getEnv("ADMIN_NAME", "Company Admin"),
		},
		CORS: CORSConfig{
			AllowedOrigins: getEnvSlice("CORS_ALLOWED_ORIGINS", []string{"http://localhost:3000"}),
		},
		LogLevel: getEnv("LOG_LEVEL", "info"),

		LinkStaleDays: getEnvInt("LINK_STALE_DAYS", 7),

		DefaultShiftTimezone: strings.TrimSpace(getEnv("DEFAULT_SHIFT_TIMEZONE", "")),
	}

	if cfg.Database.Password == "" {
		return nil, fmt.Errorf("DB_PASSWORD is required")
	}
	if cfg.JWT.Secret == "" {
		return nil, fmt.Errorf("JWT_SECRET is required")
	}

	return cfg, nil
}

func getEnv(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func getEnvInt(key string, fallback int) int {
	if v := os.Getenv(key); v != "" {
		if i, err := strconv.Atoi(v); err == nil {
			return i
		}
	}
	return fallback
}

func getEnvDuration(key string, fallback time.Duration) time.Duration {
	if v := os.Getenv(key); v != "" {
		if d, err := time.ParseDuration(v); err == nil {
			return d
		}
	}
	return fallback
}

func getEnvSlice(key string, fallback []string) []string {
	if v := os.Getenv(key); v != "" {
		parts := splitAndTrim(v, ",")
		if len(parts) > 0 {
			return parts
		}
	}
	return fallback
}

func splitAndTrim(s, sep string) []string {
	var result []string
	for _, p := range split(s, sep) {
		p = trimSpace(p)
		if p != "" {
			result = append(result, p)
		}
	}
	return result
}

// Simple helpers to avoid importing strings package in config
func split(s, sep string) []string {
	var result []string
	start := 0
	for i := 0; i < len(s)-len(sep)+1; i++ {
		if s[i:i+len(sep)] == sep {
			result = append(result, s[start:i])
			start = i + len(sep)
		}
	}
	result = append(result, s[start:])
	return result
}

func trimSpace(s string) string {
	start, end := 0, len(s)
	for start < end && (s[start] == ' ' || s[start] == '\t' || s[start] == '\n' || s[start] == '\r') {
		start++
	}
	for end > start && (s[end-1] == ' ' || s[end-1] == '\t' || s[end-1] == '\n' || s[end-1] == '\r') {
		end--
	}
	return s[start:end]
}
