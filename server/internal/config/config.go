package config

import (
	"fmt"
	"os"
	"strconv"
	"time"

	"github.com/joho/godotenv"
)

// Config holds all configuration for the server.
type Config struct {
	Server   ServerConfig
	Database DatabaseConfig
	JWT      JWTConfig
	Admin    AdminConfig
	CORS     CORSConfig
	LogLevel string
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
	Secret        string
	AccessExpiry  time.Duration
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
		JWT: JWTConfig{
			Secret:       getEnv("JWT_SECRET", ""),
			AccessExpiry: getEnvDuration("JWT_ACCESS_EXPIRY", 24*time.Hour),
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
