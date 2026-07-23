package services

import (
	"context"
	"fmt"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/alpha-ai-tracker/server/internal/repository"
	"golang.org/x/crypto/bcrypt"
)

// UserService handles business logic for user operations.
type UserService struct {
	repo *repository.UserRepo
}

// NewUserService creates a new UserService.
func NewUserService(repo *repository.UserRepo) *UserService {
	return &UserService{repo: repo}
}

// List returns a paginated list of users (as response DTOs).
func (s *UserService) List(ctx context.Context, params repository.ListParams) (*dto.UserListResponse, error) {
	result, err := s.repo.List(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("list users: %w", err)
	}

	users := make([]dto.UserResponse, len(result.Users))
	for i, u := range result.Users {
		users[i] = userToResponse(&u)
	}

	return &dto.UserListResponse{
		Data:       users,
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}

// GetByID returns a single user by ID.
func (s *UserService) GetByID(ctx context.Context, id string) (*dto.UserResponse, error) {
	user, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return nil, fmt.Errorf("get user: %w", err)
	}
	if user == nil {
		return nil, nil
	}
	resp := userToResponse(user)
	return &resp, nil
}

// Create creates a new user with bcrypt-hashed password.
func (s *UserService) Create(ctx context.Context, req *dto.CreateUserRequest) (*dto.UserResponse, error) {
	// Check email uniqueness
	unique, err := s.repo.IsUniqueEmail(ctx, req.Email, "")
	if err != nil {
		return nil, err
	}
	if !unique {
		return nil, fmt.Errorf("email already exists")
	}

	// Default password if not provided
	password := req.Password
	if password == "" {
		password = "employee@123" // default password
	}

	hashedPassword, err := bcrypt.GenerateFromPassword([]byte(password), bcrypt.DefaultCost)
	if err != nil {
		return nil, fmt.Errorf("hash password: %w", err)
	}

	role := req.Role
	if role == "" {
		role = "employee"
	}

	shift := req.Shift
	if shift == "" {
		shift = "Day"
	}

	department := req.Department
	if department == "" {
		department = "Engineering"
	}

	trackingEnabled := true
	if req.TrackingEnabled != nil {
		trackingEnabled = *req.TrackingEnabled
	}

	user := &models.User{
		Name:            req.Name,
		Email:           req.Email,
		PasswordHash:    string(hashedPassword),
		Role:            role,
		Department:      department,
		Shift:           shift,
		TrackingEnabled: trackingEnabled,
		TrackingStatus:  "untracked",
		IsOnline:        false,
		IsCompanyAdmin:  false,
	}

	created, err := s.repo.Create(ctx, user)
	if err != nil {
		return nil, fmt.Errorf("create user: %w", err)
	}

	resp := userToResponse(created)
	return &resp, nil
}

// Update partial updates a user.
func (s *UserService) Update(ctx context.Context, id string, req *dto.UpdateUserRequest) (*dto.UserResponse, error) {
	// Check user exists
	existing, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return nil, err
	}
	if existing == nil {
		return nil, nil
	}

	updates := make(map[string]interface{})

	if req.Name != nil {
		updates["name"] = *req.Name
	}
	if req.Email != nil {
		// Check email uniqueness
		unique, err := s.repo.IsUniqueEmail(ctx, *req.Email, id)
		if err != nil {
			return nil, err
		}
		if !unique {
			return nil, fmt.Errorf("email already exists")
		}
		updates["email"] = *req.Email
	}
	if req.Department != nil {
		updates["department"] = *req.Department
	}
	if req.Role != nil {
		updates["role"] = *req.Role
	}
	if req.Shift != nil {
		updates["shift"] = *req.Shift
	}
	if req.TrackingEnabled != nil {
		updates["tracking_enabled"] = *req.TrackingEnabled
	}
	if req.TrackingStatus != nil {
		updates["tracking_status"] = *req.TrackingStatus
	}
	if req.IsOnline != nil {
		updates["is_online"] = *req.IsOnline
	}

	updated, err := s.repo.Update(ctx, id, updates)
	if err != nil {
		return nil, fmt.Errorf("update user: %w", err)
	}
	if updated == nil {
		return nil, nil
	}

	resp := userToResponse(updated)
	return &resp, nil
}

// Delete removes a user by ID. Prevents deleting the company admin.
func (s *UserService) Delete(ctx context.Context, id string) error {
	user, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return err
	}
	if user == nil {
		return fmt.Errorf("user not found")
	}
	if user.IsCompanyAdmin {
		return fmt.Errorf("cannot delete company admin")
	}
	return s.repo.Delete(ctx, id)
}

func userToResponse(u *models.User) dto.UserResponse {
	return dto.UserResponse{
		ID:              u.ID,
		EmployeeID:      u.EmployeeID,
		Name:            u.Name,
		Email:           u.Email,
		Role:            u.Role,
		Department:      u.Department,
		Shift:           u.Shift,
		TrackingEnabled: u.TrackingEnabled,
		TrackingStatus:  u.TrackingStatus,
		IsOnline:        u.IsOnline,
		Avatar:          u.Avatar,
		AvatarColor:     u.AvatarColor,
		CreatedAt:       u.CreatedAt,
		UpdatedAt:       u.UpdatedAt,
		DeletedAt:       u.DeletedAt,
	}
}
