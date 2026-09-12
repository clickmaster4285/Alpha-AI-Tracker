package jobs

import (
	"context"
	"log"
	"time"

	"github.com/alpha-ai-tracker/server/internal/services"
)

type LiveViewExpiry struct {
	service *services.LiveViewService
}

func NewLiveViewExpiry(service *services.LiveViewService) *LiveViewExpiry {
	return &LiveViewExpiry{service: service}
}

func (j *LiveViewExpiry) Start(ctx context.Context) {
	ticker := time.NewTicker(15 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			expired, err := j.service.Expire(ctx)
			if err != nil {
				log.Printf("[live-view] expiry sweep failed: %v", err)
			} else if expired > 0 {
				log.Printf("[live-view] expired %d session(s)", expired)
			}
		}
	}
}
