-- 007_shell_commands.sql
-- Add shell_commands table for client shell command sync

CREATE TABLE IF NOT EXISTS shell_commands (
    id                TEXT PRIMARY KEY,
    employee_id       VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    machine_id        TEXT NOT NULL DEFAULT '',
    timestamp         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    shell_name        TEXT NOT NULL DEFAULT '',
    shell_pid         TEXT NOT NULL DEFAULT '',
    command           TEXT NOT NULL DEFAULT '',
    working_directory TEXT NOT NULL DEFAULT '',
    exit_code         TEXT NOT NULL DEFAULT '',
    user_name         TEXT NOT NULL DEFAULT '',
    platform          TEXT NOT NULL DEFAULT '',
    session_id        TEXT NOT NULL DEFAULT '',
    synced_at         TIMESTAMPTZ,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at        TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_shell_commands_employee
    ON shell_commands(employee_id, timestamp DESC);

CREATE INDEX IF NOT EXISTS idx_shell_commands_unsent
    ON shell_commands(synced_at)
    WHERE synced_at IS NULL;
