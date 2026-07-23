using System.Data.Common;
using Microsoft.Data.Sqlite;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Storage;

public class SqliteLogStore : ILogStore, IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public SqliteLogStore(string dbPath, string? encryptionKey = null)
    {
        _dbPath = dbPath;
        var cs = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";

        // Encryption (e.g. sqlcipher) not supported with default e_sqlite3.
        // To enable, replace SQLitePCLRaw.provider.e_sqlite3 with
        // SQLitePCLRaw.provider.e_sqlcipher and uncomment:
        // if (!string.IsNullOrEmpty(encryptionKey)) cs += $";Password={encryptionKey}";

        _connectionString = cs;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync(ct);

        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.CreateTableSql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task StoreAsync(IReadOnlyList<ActivityLog> logs, CancellationToken ct)
    {
        if (_connection == null || logs.Count == 0) return;

        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.InsertSql;

        var idParam = cmd.Parameters.Add("$id", SqliteType.Text);
        var machineIdParam = cmd.Parameters.Add("$machine_id", SqliteType.Text);
        var timestampParam = cmd.Parameters.Add("$timestamp", SqliteType.Text);
        var processNameParam = cmd.Parameters.Add("$process_name", SqliteType.Text);
        var windowTitleParam = cmd.Parameters.Add("$window_title", SqliteType.Text);
        var processIdParam = cmd.Parameters.Add("$process_id", SqliteType.Integer);
        var cpuParam = cmd.Parameters.Add("$cpu_percent", SqliteType.Real);
        var memoryBytesParam = cmd.Parameters.Add("$memory_bytes", SqliteType.Integer);
        var isForegroundParam = cmd.Parameters.Add("$is_foreground", SqliteType.Integer);
        var userNameParam = cmd.Parameters.Add("$user_name", SqliteType.Text);
        var platformParam = cmd.Parameters.Add("$platform", SqliteType.Text);
        var sessionIdParam = cmd.Parameters.Add("$session_id", SqliteType.Text);
        var employeeIdParam = cmd.Parameters.Add("$employee_id", SqliteType.Text);
        var employeeNameParam = cmd.Parameters.Add("$employee_name", SqliteType.Text);

        await using var tx = await _connection.BeginTransactionAsync(ct);
        ((DbCommand)cmd).Transaction = tx;

        foreach (var log in logs)
        {
            idParam.Value = log.Id;
            machineIdParam.Value = log.MachineId;
            timestampParam.Value = log.Timestamp.ToString("O");
            processNameParam.Value = log.ProcessName;
            windowTitleParam.Value = (object?)log.WindowTitle ?? DBNull.Value;
            processIdParam.Value = log.ProcessId;
            cpuParam.Value = log.CpuPercent;
            memoryBytesParam.Value = log.MemoryBytes;
            isForegroundParam.Value = log.IsForeground ? 1 : 0;
            userNameParam.Value = log.UserName;
            platformParam.Value = log.Platform;
            sessionIdParam.Value = (object?)log.SessionId ?? DBNull.Value;
            employeeIdParam.Value = (object?)log.EmployeeId ?? DBNull.Value;
            employeeNameParam.Value = (object?)log.EmployeeName ?? DBNull.Value;

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ActivityLog>> GetUnsentAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<ActivityLog>();

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM activity_logs WHERE synced_at IS NULL ORDER BY timestamp ASC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var logs = new List<ActivityLog>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            logs.Add(MapReader(reader));
        }

        return logs;
    }

    public async Task MarkSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (_connection == null || ids.Count == 0) return;

        await using var tx = await _connection.BeginTransactionAsync(ct);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE activity_logs SET synced_at = datetime('now') WHERE id = $id";

        var param = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids)
        {
            param.Value = id;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<long> GetCountAsync(CancellationToken ct)
    {
        if (_connection == null) return 0;

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM activity_logs";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0;
    }

    public async Task CleanupAsync(TimeSpan olderThan, CancellationToken ct)
    {
        if (_connection == null) return;

        var cutoff = DateTime.UtcNow - olderThan;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM activity_logs WHERE timestamp < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CleanupSyncedAsync(TimeSpan olderThan, CancellationToken ct)
    {
        if (_connection == null) return;

        var cutoff = DateTime.UtcNow - olderThan;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM activity_logs WHERE synced_at IS NOT NULL AND timestamp < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        var deleted = await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetStatusAsync(string key, string value, CancellationToken ct)
    {
        if (_connection == null) return;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.UpsertStatusSql;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetStatusAsync(string key, CancellationToken ct)
    {
        if (_connection == null) return null;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_status WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task SetPermissionStatusAsync(IReadOnlyDictionary<string, bool> permissions, string sessionType, CancellationToken ct)
    {
        if (_connection == null) return;

        await using var tx = await _connection.BeginTransactionAsync(ct);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.InsertPermissionSql;
        ((DbCommand)cmd).Transaction = tx;

        var checkId = Guid.NewGuid().ToString("N");
        var platform = "Linux";
        if (OperatingSystem.IsWindows()) platform = "Windows";
        else if (OperatingSystem.IsMacOS()) platform = "macOS";

        // Get current employee info
        var empInfo = await GetEmployeeInfoAsync(ct);
        var empId = empInfo?.EmployeeId;
        var empName = empInfo?.Name;

        foreach (var kvp in permissions)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$check_id", $"{checkId}_{kvp.Key}");
            cmd.Parameters.AddWithValue("$session_id", SessionInfo.SessionId);
            cmd.Parameters.AddWithValue("$session_type", sessionType);
            cmd.Parameters.AddWithValue("$platform", platform);
            cmd.Parameters.AddWithValue("$checked_at", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$method", kvp.Key);
            cmd.Parameters.AddWithValue("$works", kvp.Value ? 1 : 0);
            cmd.Parameters.AddWithValue("$details", "");
            cmd.Parameters.AddWithValue("$employee_id", (object?)empId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$employee_name", (object?)empName ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        await SetStatusAsync("permission_check_id", checkId, ct);
        await SetStatusAsync("session_id", SessionInfo.SessionId, ct);
        await SetStatusAsync("session_type", sessionType, ct);
        await SetStatusAsync("last_permission_check", DateTime.UtcNow.ToString("O"), ct);
    }

    // ────────────────────────────────
    // Employee Info
    // ────────────────────────────────

    public async Task SaveEmployeeInfoAsync(EmployeeInfo employee, CancellationToken ct)
    {
        if (_connection == null) return;

        // Clear old info first (only one employee at a time)
        var clearCmd = _connection.CreateCommand();
        clearCmd.CommandText = "DELETE FROM employee_info";
        await clearCmd.ExecuteNonQueryAsync(ct);

        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO employee_info (id, employee_id, name, email, role, department, shift, avatar, avatar_color, token, logged_in_at)
            VALUES ($id, $employee_id, $name, $email, $role, $department, $shift, $avatar, $avatar_color, $token, datetime('now'))
        ";
        cmd.Parameters.AddWithValue("$id", employee.Id);
        cmd.Parameters.AddWithValue("$employee_id", employee.EmployeeId);
        cmd.Parameters.AddWithValue("$name", employee.Name);
        cmd.Parameters.AddWithValue("$email", employee.Email);
        cmd.Parameters.AddWithValue("$role", employee.Role);
        cmd.Parameters.AddWithValue("$department", employee.Department);
        cmd.Parameters.AddWithValue("$shift", (object?)employee.Shift ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$avatar", (object?)employee.Avatar ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$avatar_color", (object?)employee.AvatarColor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$token", (object?)employee.Token ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        // Also set in app_status for quick access
        await SetStatusAsync("employee_id", employee.EmployeeId, ct);
        await SetStatusAsync("employee_name", employee.Name, ct);
        await SetStatusAsync("is_logged_in", "true", ct);
    }

    public async Task<EmployeeInfo?> GetEmployeeInfoAsync(CancellationToken ct)
    {
        if (_connection == null) return null;

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, employee_id, name, email, role, department, shift, avatar, avatar_color, token, logged_in_at FROM employee_info LIMIT 1";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new EmployeeInfo
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                EmployeeId = reader.GetString(reader.GetOrdinal("employee_id")),
                Name = reader.GetString(reader.GetOrdinal("name")),
                Email = reader.GetString(reader.GetOrdinal("email")),
                Role = reader.GetString(reader.GetOrdinal("role")),
                Department = reader.GetString(reader.GetOrdinal("department")),
                Shift = reader.IsDBNull(reader.GetOrdinal("shift")) ? string.Empty : reader.GetString(reader.GetOrdinal("shift")),
                Avatar = reader.IsDBNull(reader.GetOrdinal("avatar")) ? null : reader.GetString(reader.GetOrdinal("avatar")),
                AvatarColor = reader.IsDBNull(reader.GetOrdinal("avatar_color")) ? null : reader.GetString(reader.GetOrdinal("avatar_color")),
                Token = reader.IsDBNull(reader.GetOrdinal("token")) ? null : reader.GetString(reader.GetOrdinal("token")),
                LoggedInAt = reader.IsDBNull(reader.GetOrdinal("logged_in_at")) ? null : reader.GetString(reader.GetOrdinal("logged_in_at")),
            };
        }

        return null;
    }

    public async Task ClearEmployeeInfoAsync(CancellationToken ct)
    {
        if (_connection == null) return;

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM employee_info";
        await cmd.ExecuteNonQueryAsync(ct);

        await SetStatusAsync("is_logged_in", "false", ct);
        await SetStatusAsync("employee_id", "", ct);
        await SetStatusAsync("employee_name", "", ct);
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
    }

    // ────────────────────────────────
    // Shell Commands
    // ────────────────────────────────

    public async Task StoreShellCommandsAsync(IReadOnlyList<ShellCommand> commands, CancellationToken ct)
    {
        if (_connection == null || commands.Count == 0) return;

        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.InsertShellCommandSql;

        var idParam = cmd.Parameters.Add("$id", SqliteType.Text);
        var machineIdParam = cmd.Parameters.Add("$machine_id", SqliteType.Text);
        var timestampParam = cmd.Parameters.Add("$timestamp", SqliteType.Text);
        var shellNameParam = cmd.Parameters.Add("$shell_name", SqliteType.Text);
        var shellPidParam = cmd.Parameters.Add("$shell_pid", SqliteType.Text);
        var commandParam = cmd.Parameters.Add("$command", SqliteType.Text);
        var workDirParam = cmd.Parameters.Add("$working_directory", SqliteType.Text);
        var exitCodeParam = cmd.Parameters.Add("$exit_code", SqliteType.Text);
        var userNameParam = cmd.Parameters.Add("$user_name", SqliteType.Text);
        var platformParam = cmd.Parameters.Add("$platform", SqliteType.Text);
        var sessionIdParam = cmd.Parameters.Add("$session_id", SqliteType.Text);
        var employeeIdParam = cmd.Parameters.Add("$employee_id", SqliteType.Text);
        var employeeNameParam = cmd.Parameters.Add("$employee_name", SqliteType.Text);

        await using var tx = await _connection.BeginTransactionAsync(ct);
        ((System.Data.Common.DbCommand)cmd).Transaction = tx;

        foreach (var sc in commands)
        {
            idParam.Value = sc.Id;
            machineIdParam.Value = sc.MachineId;
            timestampParam.Value = sc.Timestamp.ToString("O");
            shellNameParam.Value = sc.ShellName;
            shellPidParam.Value = (object?)sc.ShellPid ?? DBNull.Value;
            commandParam.Value = sc.Command;
            workDirParam.Value = (object?)sc.WorkingDirectory ?? DBNull.Value;
            exitCodeParam.Value = (object?)sc.ExitCode ?? DBNull.Value;
            userNameParam.Value = sc.UserName;
            platformParam.Value = sc.Platform;
            sessionIdParam.Value = (object?)sc.SessionId ?? DBNull.Value;
            employeeIdParam.Value = (object?)sc.EmployeeId ?? DBNull.Value;
            employeeNameParam.Value = (object?)sc.EmployeeName ?? DBNull.Value;

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ShellCommand>> GetUnsentShellCommandsAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<ShellCommand>();

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM shell_commands WHERE synced_at IS NULL ORDER BY timestamp ASC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);

        var commands = new List<ShellCommand>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            commands.Add(MapShellReader(reader));
        }

        return commands;
    }

    public async Task MarkShellCommandsSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (_connection == null || ids.Count == 0) return;

        await using var tx = await _connection.BeginTransactionAsync(ct);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE shell_commands SET synced_at = datetime('now') WHERE id = $id";

        var param = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids)
        {
            param.Value = id;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task CleanupShellCommandsSyncedAsync(TimeSpan olderThan, CancellationToken ct)
    {
        if (_connection == null) return;

        var cutoff = DateTime.UtcNow - olderThan;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM shell_commands WHERE synced_at IS NOT NULL AND timestamp < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> GetShellCommandCountAsync(CancellationToken ct)
    {
        if (_connection == null) return 0;

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM shell_commands";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0;
    }

    private static ActivityLog MapReader(SqliteDataReader reader)
    {
        return new ActivityLog
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            MachineId = reader.GetString(reader.GetOrdinal("machine_id")),
            Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
            ProcessName = reader.GetString(reader.GetOrdinal("process_name")),
            WindowTitle = reader.IsDBNull(reader.GetOrdinal("window_title"))
                ? null : reader.GetString(reader.GetOrdinal("window_title")),
            ProcessId = reader.GetInt32(reader.GetOrdinal("process_id")),
            CpuPercent = reader.GetDouble(reader.GetOrdinal("cpu_percent")),
            MemoryBytes = reader.GetInt64(reader.GetOrdinal("memory_bytes")),
            IsForeground = reader.GetInt32(reader.GetOrdinal("is_foreground")) == 1,
            UserName = reader.IsDBNull(reader.GetOrdinal("user_name"))
                ? string.Empty : reader.GetString(reader.GetOrdinal("user_name")),
            Platform = reader.GetString(reader.GetOrdinal("platform")),
            SessionId = reader.IsDBNull(reader.GetOrdinal("session_id"))
                ? null : reader.GetString(reader.GetOrdinal("session_id")),
            EmployeeId = reader.IsDBNull(reader.GetOrdinal("employee_id"))
                ? null : reader.GetString(reader.GetOrdinal("employee_id")),
            EmployeeName = reader.IsDBNull(reader.GetOrdinal("employee_name"))
                ? null : reader.GetString(reader.GetOrdinal("employee_name"))
        };
    }

    private static ShellCommand MapShellReader(SqliteDataReader reader)
    {
        return new ShellCommand
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            MachineId = reader.GetString(reader.GetOrdinal("machine_id")),
            Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
            ShellName = reader.GetString(reader.GetOrdinal("shell_name")),
            ShellPid = reader.IsDBNull(reader.GetOrdinal("shell_pid"))
                ? null : reader.GetString(reader.GetOrdinal("shell_pid")),
            Command = reader.GetString(reader.GetOrdinal("command")),
            WorkingDirectory = reader.IsDBNull(reader.GetOrdinal("working_directory"))
                ? null : reader.GetString(reader.GetOrdinal("working_directory")),
            ExitCode = reader.IsDBNull(reader.GetOrdinal("exit_code"))
                ? null : reader.GetString(reader.GetOrdinal("exit_code")),
            UserName = reader.IsDBNull(reader.GetOrdinal("user_name"))
                ? string.Empty : reader.GetString(reader.GetOrdinal("user_name")),
            Platform = reader.GetString(reader.GetOrdinal("platform")),
            SessionId = reader.IsDBNull(reader.GetOrdinal("session_id"))
                ? null : reader.GetString(reader.GetOrdinal("session_id")),
            EmployeeId = reader.IsDBNull(reader.GetOrdinal("employee_id"))
                ? null : reader.GetString(reader.GetOrdinal("employee_id")),
            EmployeeName = reader.IsDBNull(reader.GetOrdinal("employee_name"))
                ? null : reader.GetString(reader.GetOrdinal("employee_name"))
        };
    }
}
