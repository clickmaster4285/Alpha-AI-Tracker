-- Migration 044: Data Loss Prevention rules + alerts
-- Admin CRUD for dlp_rules; client agent syncs dlp_alerts (DeviceAuth).

CREATE TABLE IF NOT EXISTS dlp_rules (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name            TEXT NOT NULL,
    "trigger"       TEXT NOT NULL CHECK ("trigger" IN ('usb', 'file_transfer', 'cloud_upload')),
    pattern         TEXT NOT NULL DEFAULT '',
    action          TEXT NOT NULL DEFAULT 'alert_only'
                    CHECK (action IN ('alert_only', 'block', 'alert_and_block')),
    severity        TEXT NOT NULL DEFAULT 'medium'
                    CHECK (severity IN ('critical', 'high', 'medium', 'low')),
    enabled         BOOLEAN NOT NULL DEFAULT TRUE,
    apply_to_all    BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at      TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_dlp_rules_enabled
    ON dlp_rules (enabled)
    WHERE deleted_at IS NULL;

CREATE TABLE IF NOT EXISTS dlp_rule_departments (
    rule_id         UUID NOT NULL REFERENCES dlp_rules(id) ON DELETE CASCADE,
    department_id   INT NOT NULL REFERENCES departments(id),
    PRIMARY KEY (rule_id, department_id)
);

CREATE TABLE IF NOT EXISTS dlp_alerts (
    id              TEXT PRIMARY KEY,
    employee_id     VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    device_id       UUID,
    rule_id         UUID REFERENCES dlp_rules(id) ON DELETE SET NULL,
    "trigger"       TEXT NOT NULL CHECK ("trigger" IN ('usb', 'file_transfer', 'cloud_upload')),
    severity        TEXT NOT NULL DEFAULT 'medium'
                    CHECK (severity IN ('critical', 'high', 'medium', 'low')),
    status          TEXT NOT NULL DEFAULT 'open'
                    CHECK (status IN ('open', 'investigating', 'resolved', 'false_positive')),
    file_or_url     TEXT NOT NULL DEFAULT '',
    detail_json     JSONB,
    assigned_to     UUID REFERENCES users(id) ON DELETE SET NULL,
    notes           TEXT NOT NULL DEFAULT '',
    event_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at      TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_dlp_alerts_status_event
    ON dlp_alerts (status, event_at DESC)
    WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_dlp_alerts_employee_event
    ON dlp_alerts (employee_id, event_at DESC)
    WHERE deleted_at IS NULL;

CREATE INDEX IF NOT EXISTS idx_dlp_alerts_rule
    ON dlp_alerts (rule_id)
    WHERE deleted_at IS NULL;
