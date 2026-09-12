package services

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/dto"
	"github.com/alpha-ai-tracker/server/internal/repository"
)

var (
	ErrLiveViewForbidden = errors.New("live view permission required")
	ErrLiveViewNotFound  = errors.New("live view session not found")
	ErrLiveViewInactive  = errors.New("live view session is no longer active")
	ErrLiveViewInvalid   = errors.New("invalid live view request")
)

type LiveViewService struct {
	repo         *repository.LiveViewRepo
	employeeRepo *repository.EmployeeRepo
	rbacRepo     *repository.RBACRepo
	maxDuration  time.Duration
}

func NewLiveViewService(repo *repository.LiveViewRepo, employeeRepo *repository.EmployeeRepo, rbacRepo *repository.RBACRepo) *LiveViewService {
	return &LiveViewService{
		repo: repo, employeeRepo: employeeRepo, rbacRepo: rbacRepo,
		maxDuration: 30 * time.Minute,
	}
}

func (s *LiveViewService) canView(ctx context.Context, userID string) (bool, error) {
	keys, err := s.rbacRepo.PermissionKeysForUser(ctx, userID)
	if err != nil {
		return false, err
	}
	for _, key := range keys {
		if key == "live-stream" || key == "employee-journey/live-stream/view" {
			return true, nil
		}
	}
	return false, nil
}

func (s *LiveViewService) Create(ctx context.Context, userID string, req *dto.CreateLiveViewSessionRequest, correlationID string) (*dto.LiveViewSessionResponse, error) {
	allowed, err := s.canView(ctx, userID)
	if err != nil {
		return nil, fmt.Errorf("check live view permission: %w", err)
	}
	if !allowed {
		return nil, ErrLiveViewForbidden
	}
	req.EmployeeID = strings.TrimSpace(req.EmployeeID)
	req.Reason = strings.TrimSpace(req.Reason)
	if req.EmployeeID == "" || len(req.Reason) < 3 || len(req.Reason) > 1000 {
		return nil, fmt.Errorf("%w: employeeId and a reason between 3 and 1000 characters are required", ErrLiveViewInvalid)
	}
	if req.Duration <= 0 {
		req.Duration = 5
	}
	if req.Duration > int(s.maxDuration/time.Minute) {
		return nil, fmt.Errorf("%w: duration cannot exceed %d minutes", ErrLiveViewInvalid, int(s.maxDuration/time.Minute))
	}
	employee, err := s.employeeRepo.GetByEmployeeID(ctx, req.EmployeeID)
	if err != nil {
		return nil, fmt.Errorf("find target employee: %w", err)
	}
	if employee == nil {
		return nil, fmt.Errorf("%w: target employee not found", ErrLiveViewInvalid)
	}
	roomBytes := make([]byte, 18)
	if _, err := rand.Read(roomBytes); err != nil {
		return nil, fmt.Errorf("generate live view room: %w", err)
	}
	roomName := "live-" + hex.EncodeToString(roomBytes)
	expiresAt := time.Now().UTC().Add(time.Duration(req.Duration) * time.Minute)
	session, err := s.repo.Create(ctx, req.EmployeeID, userID, req.Reason, roomName, expiresAt)
	if err != nil {
		return nil, err
	}
	if err := s.repo.Audit(ctx, session.ID, userID, req.EmployeeID, "REQUEST", "SUCCESS", req.Reason, correlationID); err != nil {
		return nil, err
	}
	return session, nil
}

func (s *LiveViewService) List(ctx context.Context, userID string, page, perPage int) (*dto.LiveViewSessionListResponse, error) {
	allowed, err := s.canView(ctx, userID)
	if err != nil {
		return nil, fmt.Errorf("check live view permission: %w", err)
	}
	if !allowed {
		return nil, ErrLiveViewForbidden
	}
	return s.repo.List(ctx, page, perPage)
}

func (s *LiveViewService) Get(ctx context.Context, userID, id string) (*dto.LiveViewSessionResponse, error) {
	allowed, err := s.canView(ctx, userID)
	if err != nil {
		return nil, fmt.Errorf("check live view permission: %w", err)
	}
	if !allowed {
		return nil, ErrLiveViewForbidden
	}
	session, err := s.repo.Get(ctx, id)
	if err != nil {
		return nil, err
	}
	if session == nil {
		return nil, ErrLiveViewNotFound
	}
	return session, nil
}

func (s *LiveViewService) Stop(ctx context.Context, userID, id, reason, correlationID string) (*dto.LiveViewSessionResponse, error) {
	allowed, err := s.canView(ctx, userID)
	if err != nil {
		return nil, fmt.Errorf("check live view permission: %w", err)
	}
	if !allowed {
		return nil, ErrLiveViewForbidden
	}
	session, err := s.repo.Stop(ctx, id, strings.TrimSpace(reason), "ENDED")
	if err != nil {
		return nil, err
	}
	if session == nil {
		return nil, ErrLiveViewInactive
	}
	if err := s.repo.Audit(ctx, session.ID, userID, session.EmployeeID, "STOP", "SUCCESS", reason, correlationID); err != nil {
		return nil, err
	}
	return session, nil
}

func (s *LiveViewService) Expire(ctx context.Context) (int64, error) {
	return s.repo.Expire(ctx, time.Now().UTC())
}
