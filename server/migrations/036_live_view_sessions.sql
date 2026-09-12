-- Live screen viewing foundation: authorization/audit lifecycle only.
-- Media is intentionally not stored in PostgreSQL.
CREATE TABLE IF NOT EXISTS live_view_sessions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    employee_id VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    requested_by_user_id UUID NOT NULL REFERENCES users(id),
    status VARCHAR(20) NOT NULL DEFAULT 'REQUESTED'
        CHECK (status IN ('REQUESTED', 'APPROVED', 'STARTING', 'ACTIVE', 'STOPPING', 'ENDED', 'DENIED', 'REVOKED', 'EXPIRED', 'FAILED')),
    reason TEXT NOT NULL CHECK (length(btrim(reason)) BETWEEN 3 AND 1000),
    room_name VARCHAR(160) NOT NULL UNIQUE,
    client_device_id UUID,
    requested_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    approved_at TIMESTAMPTZ,
    started_at TIMESTAMPTZ,
    ended_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ NOT NULL,
    end_reason VARCHAR(100),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CHECK (expires_at > requested_at),
    CHECK (ended_at IS NULL OR ended_at >= requested_at)
);

CREATE INDEX IF NOT EXISTS idx_live_view_sessions_employee_time
    ON live_view_sessions (employee_id, requested_at DESC);
CREATE INDEX IF NOT EXISTS idx_live_view_sessions_active_expiry
    ON live_view_sessions (expires_at)
    WHERE status IN ('REQUESTED', 'APPROVED', 'STARTING', 'ACTIVE', 'STOPPING');

CREATE TABLE IF NOT EXISTS live_view_audit_events (
    id BIGSERIAL PRIMARY KEY,
    session_id UUID REFERENCES live_view_sessions(id),
    actor_user_id UUID REFERENCES users(id),
    employee_id VARCHAR(20) REFERENCES employees(employee_id),
    action VARCHAR(40) NOT NULL,
    outcome VARCHAR(20) NOT NULL CHECK (outcome IN ('SUCCESS', 'DENIED', 'FAILED')),
    reason TEXT,
    correlation_id VARCHAR(100),
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_live_view_audit_session_time
    ON live_view_audit_events (session_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_live_view_audit_employee_time
    ON live_view_audit_events (employee_id, created_at DESC);
