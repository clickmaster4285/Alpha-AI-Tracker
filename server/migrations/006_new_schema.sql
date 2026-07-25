-- 006_new_schema.sql
-- Add new Phase 1 & Phase 2 tables, drop activity_logs

-- ─────────────────────────────────────
-- PHASE 1: DEVICE & SYSTEM INFO TABLES
-- ─────────────────────────────────────

CREATE TABLE IF NOT EXISTS device_hardware_info (
    id               TEXT PRIMARY KEY,
    employee_id      VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    device_id        TEXT NOT NULL DEFAULT '',
    mac_address      TEXT NOT NULL DEFAULT '',
    hostname         TEXT NOT NULL DEFAULT '',
    os_name          TEXT NOT NULL DEFAULT '',
    os_version       TEXT NOT NULL DEFAULT '',
    cpu_model        TEXT NOT NULL DEFAULT '',
    cpu_cores        INTEGER NOT NULL DEFAULT 0,
    ram_total_mb     BIGINT NOT NULL DEFAULT 0,
    storage_devices  JSONB NOT NULL DEFAULT '[]',
    gpu_model        TEXT NOT NULL DEFAULT '',
    gpu_vram_mb      BIGINT NOT NULL DEFAULT 0,
    collected_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at        TIMESTAMPTZ,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at       TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_device_hw_employee
    ON device_hardware_info(employee_id, collected_at DESC);

CREATE TABLE IF NOT EXISTS installed_applications (
    id               TEXT PRIMARY KEY,
    employee_id      VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    app_name         TEXT NOT NULL,
    app_version      TEXT NOT NULL DEFAULT '',
    publisher        TEXT NOT NULL DEFAULT '',
    install_path     TEXT NOT NULL DEFAULT '',
    install_date     TIMESTAMPTZ,
    uninstall_string TEXT NOT NULL DEFAULT '',
    change_type      TEXT NOT NULL DEFAULT 'seen',
    detected_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at        TIMESTAMPTZ,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at       TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_installed_apps_employee
    ON installed_applications(employee_id, detected_at DESC);

CREATE INDEX IF NOT EXISTS idx_installed_apps_name
    ON installed_applications(app_name);

CREATE TABLE IF NOT EXISTS network_info (
    id                    TEXT PRIMARY KEY,
    employee_id           VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    public_ip             TEXT NOT NULL DEFAULT '',
    private_ip            TEXT NOT NULL DEFAULT '',
    mac_address           TEXT NOT NULL DEFAULT '',
    network_interface_name TEXT NOT NULL DEFAULT '',
    collected_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at             TIMESTAMPTZ,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at            TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_network_info_employee
    ON network_info(employee_id, collected_at DESC);

CREATE TABLE IF NOT EXISTS session_events (
    id               TEXT PRIMARY KEY,
    employee_id      VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    event_type       TEXT NOT NULL,
    os_username      TEXT NOT NULL DEFAULT '',
    event_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at        TIMESTAMPTZ,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at       TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_session_events_employee
    ON session_events(employee_id, event_at DESC);

-- ─────────────────────────────────────
-- PHASE 2: RELATIONAL APPLICATION LOGS
-- ─────────────────────────────────────

CREATE TABLE IF NOT EXISTS app_sessions (
    id               TEXT PRIMARY KEY,
    employee_id      VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    process_name     TEXT NOT NULL,
    app_display_name TEXT NOT NULL DEFAULT '',
    started_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ended_at         TIMESTAMPTZ,
    machine_id       TEXT NOT NULL DEFAULT '',
    session_id       TEXT NOT NULL DEFAULT '',
    platform         TEXT NOT NULL DEFAULT '',
    synced_at        TIMESTAMPTZ,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at       TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_app_sessions_employee
    ON app_sessions(employee_id, started_at DESC);

CREATE INDEX IF NOT EXISTS idx_app_sessions_timestamp
    ON app_sessions(started_at DESC);

CREATE TABLE IF NOT EXISTS browser_contexts (
    id                   TEXT PRIMARY KEY,
    employee_id          VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    app_session_id       TEXT NOT NULL REFERENCES app_sessions(id),
    browser_profile_name TEXT NOT NULL DEFAULT '',
    tab_id               TEXT NOT NULL DEFAULT '',
    opened_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    closed_at            TIMESTAMPTZ,
    synced_at            TIMESTAMPTZ,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at           TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_browser_ctx_employee
    ON browser_contexts(employee_id, opened_at DESC);

CREATE INDEX IF NOT EXISTS idx_browser_ctx_app_session
    ON browser_contexts(app_session_id);

CREATE TABLE IF NOT EXISTS urls (
    id              TEXT PRIMARY KEY,
    employee_id     VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    url             TEXT NOT NULL,
    domain          TEXT NOT NULL DEFAULT '',
    first_seen_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at       TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at      TIMESTAMPTZ,
    UNIQUE(employee_id, url)
);

CREATE INDEX IF NOT EXISTS idx_urls_employee
    ON urls(employee_id, first_seen_at DESC);

CREATE TABLE IF NOT EXISTS url_visits (
    id                 TEXT PRIMARY KEY,
    employee_id        VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    browser_context_id TEXT NOT NULL REFERENCES browser_contexts(id),
    url_id             TEXT NOT NULL REFERENCES urls(id),
    path_and_query     TEXT NOT NULL DEFAULT '',
    page_title         TEXT NOT NULL DEFAULT '',
    visited_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    synced_at          TIMESTAMPTZ,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at         TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_url_visits_employee
    ON url_visits(employee_id, visited_at DESC);

CREATE INDEX IF NOT EXISTS idx_url_visits_browser_ctx
    ON url_visits(browser_context_id);

CREATE TABLE IF NOT EXISTS file_explorer_contexts (
    id              TEXT PRIMARY KEY,
    employee_id     VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    app_session_id  TEXT NOT NULL REFERENCES app_sessions(id),
    folder_path     TEXT NOT NULL DEFAULT '',
    opened_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    closed_at       TIMESTAMPTZ,
    synced_at       TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at      TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_file_explorer_employee
    ON file_explorer_contexts(employee_id, opened_at DESC);

CREATE INDEX IF NOT EXISTS idx_file_explorer_app_session
    ON file_explorer_contexts(app_session_id);

-- ─────────────────────────────────────
-- DROP ACTIVITY_LOGS TABLE
-- ─────────────────────────────────────
-- This is a destructive change. No down-migration exists.
-- All data in activity_logs will be lost.
DROP TABLE IF EXISTS activity_logs CASCADE;
