-- 003_activity_logs.sql
-- Activity logs synced from desktop clients

CREATE TABLE IF NOT EXISTS activity_logs (
  id              TEXT NOT NULL,
  employee_id     VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
  machine_id      TEXT NOT NULL DEFAULT '',
  timestamp       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  process_name    TEXT NOT NULL DEFAULT '',
  window_title    TEXT,
  process_id      INTEGER NOT NULL DEFAULT 0,
  cpu_percent     REAL NOT NULL DEFAULT 0,
  memory_bytes    BIGINT NOT NULL DEFAULT 0,
  is_foreground   BOOLEAN NOT NULL DEFAULT false,
  user_name       TEXT NOT NULL DEFAULT '',
  platform        TEXT NOT NULL DEFAULT '',
  session_id      TEXT,
  employee_name   TEXT,
  synced_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (id, employee_id)
);

-- Indexes for queries
CREATE INDEX IF NOT EXISTS idx_activity_logs_employee
  ON activity_logs(employee_id, timestamp DESC);

CREATE INDEX IF NOT EXISTS idx_activity_logs_timestamp
  ON activity_logs(timestamp DESC);

CREATE INDEX IF NOT EXISTS idx_activity_logs_machine
  ON activity_logs(machine_id, timestamp DESC);

CREATE INDEX IF NOT EXISTS idx_activity_logs_foreground
  ON activity_logs(employee_id, is_foreground, timestamp DESC);
