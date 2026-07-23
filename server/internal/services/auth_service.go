package services

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"fmt"
	"io"
	"log"
	"time"

	"github.com/golang-jwt/jwt/v5"
	"github.com/alpha-ai-tracker/server/internal/config"
	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"golang.org/x/crypto/bcrypt"
)

// AuthService handles authentication and company admin initialization.
type AuthService struct {
	repo      *repository.UserRepo
	jwtConfig config.JWTConfig
	adminCfg  config.AdminConfig
}

// NewAuthService creates a new AuthService.
func NewAuthService(repo *repository.UserRepo, jwtCfg config.JWTConfig, adminCfg config.AdminConfig) *AuthService {
	return &AuthService{
		repo:      repo,
		jwtConfig: jwtCfg,
		adminCfg:  adminCfg,
	}
}

// Claims represents JWT claims.
type Claims struct {
	UserID string `json:"userId"`
	jwt.RegisteredClaims
}

// deriveKey derives a 32-byte AES key from the JWT secret using SHA-256.
func deriveKey(secret string) []byte {
	hash := sha256.Sum256([]byte(secret))
	return hash[:]
}

// encryptToken encrypts a signed JWT string using AES-GCM.
func encryptToken(signedToken string, secret string) (string, error) {
	key := deriveKey(secret)

	block, err := aes.NewCipher(key)
	if err != nil {
		return "", fmt.Errorf("create cipher: %w", err)
	}

	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return "", fmt.Errorf("create GCM: %w", err)
	}

	nonce := make([]byte, gcm.NonceSize())
	if _, err := io.ReadFull(rand.Reader, nonce); err != nil {
		return "", fmt.Errorf("generate nonce: %w", err)
	}

	ciphertext := gcm.Seal(nonce, nonce, []byte(signedToken), nil)
	return base64.URLEncoding.EncodeToString(ciphertext), nil
}

// decryptToken decrypts an AES-GCM encrypted token back to a signed JWT string.
func decryptToken(encrypted string, secret string) (string, error) {
	key := deriveKey(secret)

	data, err := base64.URLEncoding.DecodeString(encrypted)
	if err != nil {
		return "", fmt.Errorf("base64 decode: %w", err)
	}

	block, err := aes.NewCipher(key)
	if err != nil {
		return "", fmt.Errorf("create cipher: %w", err)
	}

	gcm, err := cipher.NewGCM(block)
	if err != nil {
		return "", fmt.Errorf("create GCM: %w", err)
	}

	nonceSize := gcm.NonceSize()
	if len(data) < nonceSize {
		return "", fmt.Errorf("ciphertext too short")
	}

	nonce, ciphertext := data[:nonceSize], data[nonceSize:]
	plaintext, err := gcm.Open(nil, nonce, ciphertext, nil)
	if err != nil {
		return "", fmt.Errorf("decrypt: %w", err)
	}

	return string(plaintext), nil
}

// EnsureCompanyAdmin checks if a company admin exists, and if not, creates one.
func (s *AuthService) EnsureCompanyAdmin(ctx context.Context) error {
	count, err := s.repo.CountCompanyAdmins(ctx)
	if err != nil {
		return fmt.Errorf("check company admins: %w", err)
	}

	if count > 0 {
		log.Printf("[auth] company admin exists, skipping initialization")
		return nil
	}

	log.Printf("[auth] no company admin found — auto-initializing with credentials from .env")

	hashedPassword, err := bcrypt.GenerateFromPassword([]byte(s.adminCfg.Password), bcrypt.DefaultCost)
	if err != nil {
		return fmt.Errorf("hash admin password: %w", err)
	}

	admin := &models.User{
		Name:            s.adminCfg.Name,
		Email:           s.adminCfg.Email,
		PasswordHash:    string(hashedPassword),
		Role:            "company_admin",
		Department:      "Executive",
		Shift:           "Day",
		TrackingEnabled: false,
		TrackingStatus:  "untracked",
		IsOnline:        false,
		IsCompanyAdmin:  true,
	}

	created, err := s.repo.Create(ctx, admin)
	if err != nil {
		return fmt.Errorf("create company admin: %w", err)
	}

	log.Printf("[auth] company admin created: email=%s employeeId=%s", created.Email, created.EmployeeID)
	return nil
}

// Login authenticates a user and returns JWT token + user info.
func (s *AuthService) Login(ctx context.Context, req *dto.LoginRequest) (*dto.LoginResponse, error) {
	user, err := s.repo.GetByEmail(ctx, req.Email)
	if err != nil {
		return nil, fmt.Errorf("find user: %w", err)
	}
	if user == nil {
		return nil, fmt.Errorf("invalid email or password")
	}

	if err := bcrypt.CompareHashAndPassword([]byte(user.PasswordHash), []byte(req.Password)); err != nil {
		return nil, fmt.Errorf("invalid email or password")
	}

	token, err := s.generateToken(user)
	if err != nil {
		return nil, fmt.Errorf("generate token: %w", err)
	}

	return &dto.LoginResponse{
		User:  userToResponse(user),
		Token: token,
	}, nil
}

// ValidateToken decrypts and validates a JWT token, returning the claims.
func (s *AuthService) ValidateToken(tokenString string) (*Claims, error) {
	signedToken, err := decryptToken(tokenString, s.jwtConfig.Secret)
	if err != nil {
		return nil, fmt.Errorf("decrypt token: %w", err)
	}

	token, err := jwt.ParseWithClaims(signedToken, &Claims{}, func(token *jwt.Token) (interface{}, error) {
		if _, ok := token.Method.(*jwt.SigningMethodHMAC); !ok {
			return nil, fmt.Errorf("unexpected signing method: %v", token.Header["alg"])
		}
		return []byte(s.jwtConfig.Secret), nil
	})
	if err != nil {
		return nil, fmt.Errorf("parse token: %w", err)
	}

	claims, ok := token.Claims.(*Claims)
	if !ok || !token.Valid {
		return nil, fmt.Errorf("invalid token")
	}

	return claims, nil
}

// GetUserByID returns a full user model (for middleware use).
func (s *AuthService) GetUserByID(ctx context.Context, id string) (*models.User, error) {
	return s.repo.GetByID(ctx, id)
}

// GenerateEmployeeToken generates a JWT token for an employee desktop client session.
func (s *AuthService) GenerateEmployeeToken(emp *models.Employee) (string, error) {
	now := time.Now()
	claims := &Claims{
		UserID: emp.ID,
		RegisteredClaims: jwt.RegisteredClaims{
			ExpiresAt: jwt.NewNumericDate(now.Add(s.jwtConfig.AccessExpiry)),
			IssuedAt:  jwt.NewNumericDate(now),
			NotBefore: jwt.NewNumericDate(now),
			Issuer:    "alpha-ai-tracker-employee",
			Subject:   emp.ID,
		},
	}

	token := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	signedToken, err := token.SignedString([]byte(s.jwtConfig.Secret))
	if err != nil {
		return "", fmt.Errorf("sign token: %w", err)
	}

	return encryptToken(signedToken, s.jwtConfig.Secret)
}

func (s *AuthService) generateToken(user *models.User) (string, error) {
	now := time.Now()
	claims := &Claims{
		UserID: user.ID,
		RegisteredClaims: jwt.RegisteredClaims{
			ExpiresAt: jwt.NewNumericDate(now.Add(s.jwtConfig.AccessExpiry)),
			IssuedAt:  jwt.NewNumericDate(now),
			NotBefore: jwt.NewNumericDate(now),
			Issuer:    "alpha-ai-tracker",
			Subject:   user.ID,
		},
	}

	token := jwt.NewWithClaims(jwt.SigningMethodHS256, claims)
	signedToken, err := token.SignedString([]byte(s.jwtConfig.Secret))
	if err != nil {
		return "", fmt.Errorf("sign token: %w", err)
	}

	return encryptToken(signedToken, s.jwtConfig.Secret)
}
