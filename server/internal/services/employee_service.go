package services

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"fmt"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/models"
	goredis "github.com/alpha-ai-tracker/server/internal/redis"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

// EmployeeService handles business logic for employee operations.
type EmployeeService struct {
	repo        *repository.EmployeeRepo
	redisClient *goredis.Client
}

// NewEmployeeService creates a new EmployeeService.
func NewEmployeeService(repo *repository.EmployeeRepo, redisClient *goredis.Client) *EmployeeService {
	return &EmployeeService{repo: repo, redisClient: redisClient}
}

// List returns a paginated list of employees.
func (s *EmployeeService) List(ctx context.Context, params repository.EmployeeListParams) (*dto.EmployeeListResponse, error) {
	result, err := s.repo.List(ctx, params)
	if err != nil {
		return nil, fmt.Errorf("list employees: %w", err)
	}

	emps := make([]dto.EmployeeResponse, len(result.Employees))
	for i, e := range result.Employees {
		emps[i] = employeeToResponse(&e)
	}

	return &dto.EmployeeListResponse{
		Data:       emps,
		Total:      result.Total,
		Page:       result.Page,
		PerPage:    result.PerPage,
		TotalPages: result.TotalPages,
	}, nil
}

// GetByID returns a single employee by ID.
func (s *EmployeeService) GetByID(ctx context.Context, id string) (*dto.EmployeeResponse, error) {
	emp, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return nil, fmt.Errorf("get employee: %w", err)
	}
	if emp == nil {
		return nil, nil
	}
	resp := employeeToResponse(emp)
	return &resp, nil
}

// Create creates a new employee.
func (s *EmployeeService) Create(ctx context.Context, req *dto.CreateEmployeeRequest) (*dto.EmployeeResponse, error) {
	// Check for duplicate email
	if req.Email != "" {
		existing, err := s.repo.GetByEmail(ctx, req.Email)
		if err != nil {
			return nil, fmt.Errorf("check email uniqueness: %w", err)
		}
		if existing != nil {
			return nil, fmt.Errorf("email already exists: %s", req.Email)
		}
	}

	employeeID, err := s.repo.GenerateEmployeeID(ctx)
	if err != nil {
		return nil, fmt.Errorf("generate employee id: %w", err)
	}

	shift := req.Shift
	if shift == "" {
		shift = "Day"
	}
	deptID := req.DepartmentID
	if deptID == 0 {
		deptID = 1 // Default to Engineering (ID 1)
	}
	departmentStr := req.Department
	if departmentStr == "" {
		departmentStr = "Engineering"
	}

	// Validate department_id exists
	// (deptID defaults to 1 which is Engineering — always seeded)

	emp := &models.Employee{
		EmployeeID:      employeeID,
		Name:            req.Name,
		Email:           req.Email,
		Department:      departmentStr,
		DepartmentID:    deptID,
		Shift:           shift,
		TrackingEnabled: true,
		TrackingStatus:  "untracked",
		IsOnline:        false,
	}

	created, err := s.repo.Create(ctx, emp)
	if err != nil {
		return nil, fmt.Errorf("create employee: %w", err)
	}

	resp := employeeToResponse(created)
	return &resp, nil
}

// Update partial updates an employee.
func (s *EmployeeService) Update(ctx context.Context, id string, req *dto.UpdateEmployeeRequest) (*dto.EmployeeResponse, error) {
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
		updates["email"] = *req.Email
	}
	if req.Department != nil {
		updates["department"] = *req.Department
	}
	if req.DepartmentID != nil {
		updates["department_id"] = *req.DepartmentID
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
		return nil, fmt.Errorf("update employee: %w", err)
	}
	if updated == nil {
		return nil, nil
	}

	resp := employeeToResponse(updated)
	return &resp, nil
}

// Delete removes an employee by ID.
func (s *EmployeeService) Delete(ctx context.Context, id string) error {
	emp, err := s.repo.GetByID(ctx, id)
	if err != nil {
		return err
	}
	if emp == nil {
		return fmt.Errorf("employee not found")
	}
	return s.repo.Delete(ctx, id)
}

// GenerateSecret generates a one-time login secret for an employee and stores it in Redis.
func (s *EmployeeService) GenerateSecret(ctx context.Context, employeeID string) (*dto.GenerateSecretResponse, error) {
	// Verify employee exists
	emp, err := s.repo.GetByID(ctx, employeeID)
	if err != nil {
		return nil, fmt.Errorf("get employee: %w", err)
	}
	if emp == nil {
		return nil, fmt.Errorf("employee not found")
	}

	// Generate random 6-byte hex secret (12 chars)
	secretBytes := make([]byte, 6)
	if _, err := rand.Read(secretBytes); err != nil {
		return nil, fmt.Errorf("generate secret: %w", err)
	}
	secret := hex.EncodeToString(secretBytes)

	// Store in Redis with 5 min TTL
	if s.redisClient == nil {
		return nil, fmt.Errorf("Redis is not available — employee secrets require a running Redis server")
	}
	if err := s.redisClient.StoreSecret(ctx, emp.EmployeeID, secret); err != nil {
		return nil, fmt.Errorf("store secret in redis: %w", err)
	}

	return &dto.GenerateSecretResponse{
		Secret:    secret,
		ExpiresIn: int(goredis.SecretTTL.Seconds()),
	}, nil
}

func employeeToResponse(e *models.Employee) dto.EmployeeResponse {
	return dto.EmployeeResponse{
		ID:              e.ID,
		EmployeeID:      e.EmployeeID,
		Name:            e.Name,
		Email:           e.Email,
		Department:      e.Department,
		DepartmentID:    e.DepartmentID,
		Shift:           e.Shift,
		TrackingEnabled: e.TrackingEnabled,
		TrackingStatus:  e.TrackingStatus,
		IsOnline:        e.IsOnline,
		Avatar:          e.Avatar,
		AvatarColor:     e.AvatarColor,
		CreatedAt:       e.CreatedAt,
		UpdatedAt:       e.UpdatedAt,
		DeletedAt:       e.DeletedAt,
	}
}
