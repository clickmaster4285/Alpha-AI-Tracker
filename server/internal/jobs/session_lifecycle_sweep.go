package jobs

import (
	"context"
	"log"
	"os"
	"strconv"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

// SessionLifecycleSweep flips app_sessions through the 3-state lifecycle:
//
//	ACTIVE  → STALE   when last_sync_at < NOW() - staleAfter (no heartbeat)
//	STALE   → CLOSED  when last_sync_at < NOW() - closeAfter (terminal)
//
// Only CLOSED is final. A live client re-uploading any row with
// ended_at=NULL promotes STALE/CLOSED back to ACTIVE in the upsert
// (see new_schema_repo.BulkInsertAppSessions), so a network outage
// never destroys information that may still exist on the client.
//
// Defaults: staleAfter=10min, closeAfter=24h. Override via env:
//	SESSION_STALE_AFTER_MINUTES
//	SESSION_CLOSE_AFTER_HOURS
type SessionLifecycleSweep struct {
	pool       *pgxpool.Pool
	staleAfter time.Duration
	closeAfter time.Duration
	interval   time.Duration
}

func NewSessionLifecycleSweep(pool *pgxpool.Pool) *SessionLifecycleSweep {
	staleMin := 10
	if v := os.Getenv("SESSION_STALE_AFTER_MINUTES"); v != "" {
		if parsed, err := strconv.Atoi(v); err == nil && parsed > 0 {
			staleMin = parsed
		}
	}
	closeHr := 24
	if v := os.Getenv("SESSION_CLOSE_AFTER_HOURS"); v != "" {
		if parsed, err := strconv.Atoi(v); err == nil && parsed > 0 {
			closeHr = parsed
		}
	}
	return &SessionLifecycleSweep{
		pool:       pool,
		staleAfter: time.Duration(staleMin) * time.Minute,
		closeAfter: time.Duration(closeHr) * time.Hour,
		// Every minute — the sweep itself is two indexed UPDATEs and
		// runs in milliseconds; the cadence is the resolution at which
		// a session flips to STALE on the dashboard.
		interval: time.Minute,
	}
}

// Start launches the background loop until ctx is cancelled.
func (s *SessionLifecycleSweep) Start(ctx context.Context) {
	log.Printf("[session-lifecycle] starting (stale_after=%v, close_after=%v, interval=%v)",
		s.staleAfter, s.closeAfter, s.interval)

	// Run once shortly after start so the dashboard reflects the new
	// status column within ~30s of boot, not after the first interval.
	select {
	case <-time.After(30 * time.Second):
		s.RunOnce(ctx)
	case <-ctx.Done():
		return
	}

	ticker := time.NewTicker(s.interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			log.Printf("[session-lifecycle] stopping")
			return
		case <-ticker.C:
			s.RunOnce(ctx)
		}
	}
}

// RunOnce performs one transition pass. Safe to call concurrently
// with the upsert path — the status CASE in BulkInsertAppSessions
// only ever promotes rows forward, never backwards (a STALE row
// stays STALE until the sweeper promotes it to CLOSED, unless the
// client reconnects and flips it to ACTIVE).
func (s *SessionLifecycleSweep) RunOnce(ctx context.Context) {
	if s.pool == nil {
		return
	}
	start := time.Now()

	// 1. ACTIVE → STALE.
	staleTag, err := s.pool.Exec(ctx, `
		UPDATE app_sessions
		   SET status = 'STALE'
		 WHERE deleted_at IS NULL
		   AND status = 'ACTIVE'
		   AND last_sync_at IS NOT NULL
		   AND last_sync_at < NOW() - make_interval(secs => $1)
	`, int64(s.staleAfter.Seconds()))
	if err != nil {
		log.Printf("[session-lifecycle] ACTIVE→STALE error: %v", err)
		return
	}

	// 2. STALE → CLOSED. When transitioning to CLOSED we also
	// populate ended_at (frozen at last_activity_at so the
	// duration reflects real usage, not the sweep moment).
	closeTag, err := s.pool.Exec(ctx, `
		UPDATE app_sessions
		   SET status   = 'CLOSED',
		       ended_at = COALESCE(last_activity_at, last_sync_at, started_at)
		 WHERE deleted_at IS NULL
		   AND status = 'STALE'
		   AND last_sync_at IS NOT NULL
		   AND last_sync_at < NOW() - make_interval(secs => $1)
	`, int64(s.closeAfter.Seconds()))
	if err != nil {
		log.Printf("[session-lifecycle] STALE→CLOSED error: %v", err)
		return
	}

	if staleTag.RowsAffected() > 0 || closeTag.RowsAffected() > 0 {
		log.Printf("[session-lifecycle] sweep in %v: %d ACTIVE→STALE, %d STALE→CLOSED",
			time.Since(start), staleTag.RowsAffected(), closeTag.RowsAffected())
	}
}