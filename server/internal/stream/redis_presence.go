package stream

import (
	"context"
	"encoding/json"
	"log"
	"time"

	goredis "github.com/redis/go-redis/v9"
)

// PresenceStore mirrors hub snapshot fields into Redis so multi-node
// ListEmployees can see wanted/streaming across replicas.
// PeerConnections and JPEG mailboxes stay process-local (sticky LB required).
type PresenceStore struct {
	rdb    *goredis.Client
	prefix string
}

// NewPresenceStore returns nil-safe store; rdb may be nil (no-op).
func NewPresenceStore(rdb *goredis.Client, prefix string) *PresenceStore {
	if prefix == "" {
		prefix = "live_stream:"
	}
	return &PresenceStore{rdb: rdb, prefix: prefix}
}

func (p *PresenceStore) enabled() bool {
	return p != nil && p.rdb != nil
}

func (p *PresenceStore) key(empID string) string {
	return p.prefix + "emp:" + empID
}

// PubSubChannel is the Redis channel for cross-node control hints.
func (p *PresenceStore) PubSubChannel() string {
	if p == nil {
		return "live_stream:events"
	}
	return p.prefix + "events"
}

// PresencePayload is stored as JSON per employee.
type PresencePayload struct {
	Wanted          bool      `json:"wanted"`
	Streaming       bool      `json:"streaming"`
	StreamAvailable bool      `json:"streamAvailable"`
	ClientConnected bool      `json:"clientConnected"`
	WatcherCount    int       `json:"watcherCount"`
	SelectedMonitor int       `json:"selectedMonitor"`
	UpdatedAt       time.Time `json:"updatedAt"`
}

// Put writes a snapshot. No-op if Redis is unavailable.
func (p *PresenceStore) Put(ctx context.Context, empID string, snap EmployeeSnapshot) {
	if !p.enabled() || empID == "" {
		return
	}
	payload := PresencePayload{
		Wanted:          snap.Wanted,
		Streaming:       snap.Streaming,
		StreamAvailable: snap.StreamAvailable,
		ClientConnected: snap.ClientConnected,
		WatcherCount:    snap.WatcherCount,
		SelectedMonitor: snap.SelectedMonitor,
		UpdatedAt:       time.Now().UTC(),
	}
	b, err := json.Marshal(payload)
	if err != nil {
		return
	}
	if err := p.rdb.Set(ctx, p.key(empID), b, 5*time.Minute).Err(); err != nil {
		log.Printf("[live-stream] redis presence set employee=%s: %v", empID, err)
	}
}

// Get reads remote presence (ok=false if missing).
func (p *PresenceStore) Get(ctx context.Context, empID string) (PresencePayload, bool) {
	if !p.enabled() {
		return PresencePayload{}, false
	}
	b, err := p.rdb.Get(ctx, p.key(empID)).Bytes()
	if err != nil {
		return PresencePayload{}, false
	}
	var out PresencePayload
	if json.Unmarshal(b, &out) != nil {
		return PresencePayload{}, false
	}
	return out, true
}

// PublishControl notifies other nodes (e.g. start/stop hints). Best-effort.
func (p *PresenceStore) PublishControl(ctx context.Context, empID, typ string) {
	if !p.enabled() {
		return
	}
	msg, _ := json.Marshal(map[string]string{
		"employeeId": empID,
		"type":       typ,
	})
	if err := p.rdb.Publish(ctx, p.PubSubChannel(), msg).Err(); err != nil {
		log.Printf("[live-stream] redis publish: %v", err)
	}
}

// Delete removes presence on idle reap / disconnect cleanup.
func (p *PresenceStore) Delete(ctx context.Context, empID string) {
	if !p.enabled() {
		return
	}
	_ = p.rdb.Del(ctx, p.key(empID)).Err()
}
