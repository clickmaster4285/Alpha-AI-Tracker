namespace client.Storage;

internal static class DatabaseSchema
{
    internal const string CreateTableSql = @"
        CREATE TABLE IF NOT EXISTS activity_logs (
            id              TEXT PRIMARY KEY,
            machine_id      TEXT NOT NULL,
            timestamp       TEXT NOT NULL,
            process_name    TEXT NOT NULL,
            window_title    TEXT,
            process_id      INTEGER NOT NULL,
            cpu_percent     REAL DEFAULT 0,
            memory_bytes    INTEGER DEFAULT 0,
            is_foreground   INTEGER DEFAULT 0,
            user_name       TEXT,
            platform        TEXT NOT NULL,
            session_id      TEXT,
            employee_id     TEXT,
            employee_name   TEXT,
            synced_at       TEXT,
            created_at      TEXT DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_logs_unsent
            ON activity_logs(synced_at, timestamp);

        CREATE INDEX IF NOT EXISTS idx_logs_timestamp
            ON activity_logs(timestamp DESC);

        CREATE INDEX IF NOT EXISTS idx_logs_machine
            ON activity_logs(machine_id, timestamp DESC);

        CREATE TABLE IF NOT EXISTS app_status (
            key             TEXT PRIMARY KEY,
            value           TEXT NOT NULL,
            updated_at      TEXT DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS permission_status (
            check_id        TEXT PRIMARY KEY,
            session_id      TEXT NOT NULL,
            session_type    TEXT NOT NULL,
            platform        TEXT NOT NULL,
            checked_at      TEXT NOT NULL,
            method          TEXT NOT NULL,
            works           INTEGER NOT NULL DEFAULT 0,
            details         TEXT,
            employee_id     TEXT,
            employee_name   TEXT
        );

        CREATE TABLE IF NOT EXISTS employee_info (
            id              TEXT PRIMARY KEY,
            employee_id     TEXT NOT NULL,
            name            TEXT NOT NULL,
            email           TEXT NOT NULL,
            role            TEXT NOT NULL,
            department      TEXT NOT NULL,
            shift           TEXT,
            avatar          TEXT,
            avatar_color    TEXT,
            token           TEXT,
            logged_in_at    TEXT DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS shell_commands (
            id              TEXT PRIMARY KEY,
            machine_id      TEXT NOT NULL,
            timestamp       TEXT NOT NULL,
            shell_name      TEXT NOT NULL,
            shell_pid       TEXT,
            command         TEXT NOT NULL,
            working_directory TEXT,
            exit_code       TEXT,
            user_name       TEXT,
            platform        TEXT NOT NULL,
            session_id      TEXT,
            employee_id     TEXT,
            employee_name   TEXT,
            synced_at       TEXT,
            created_at      TEXT DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_shell_unsent
            ON shell_commands(synced_at, timestamp);

        CREATE INDEX IF NOT EXISTS idx_shell_timestamp
            ON shell_commands(timestamp DESC);
    ";

    internal const string InsertShellCommandSql = @"
        INSERT OR IGNORE INTO shell_commands
            (id, machine_id, timestamp, shell_name, shell_pid,
             command, working_directory, exit_code,
             user_name, platform, session_id, employee_id, employee_name)
        VALUES
            ($id, $machine_id, $timestamp, $shell_name, $shell_pid,
             $command, $working_directory, $exit_code,
             $user_name, $platform, $session_id, $employee_id, $employee_name)
    ";

    internal const string InsertSql = @"
        INSERT OR IGNORE INTO activity_logs
            (id, machine_id, timestamp, process_name, window_title,
             process_id, cpu_percent, memory_bytes, is_foreground,
             user_name, platform, session_id, employee_id, employee_name)
        VALUES
            ($id, $machine_id, $timestamp, $process_name, $window_title,
             $process_id, $cpu_percent, $memory_bytes, $is_foreground,
             $user_name, $platform, $session_id, $employee_id, $employee_name)
    ";

    internal const string UpsertStatusSql = @"
        INSERT INTO app_status (key, value, updated_at)
        VALUES ($key, $value, datetime('now'))
        ON CONFLICT(key) DO UPDATE SET
            value = excluded.value,
            updated_at = excluded.updated_at
    ";

    internal const string InsertPermissionSql = @"
        INSERT INTO permission_status
            (check_id, session_id, session_type, platform, checked_at, method, works, details, employee_id, employee_name)
        VALUES
            ($check_id, $session_id, $session_type, $platform, $checked_at, $method, $works, $details, $employee_id, $employee_name)
    ";

}
