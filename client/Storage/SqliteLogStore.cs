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

    // ────────────────────────────────────────
    // Device Hardware Info
    // ────────────────────────────────────────

    public async Task StoreDeviceHardwareInfoAsync(IReadOnlyList<DeviceHardwareInfo> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.InsertDeviceHardwareInfoSql;
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pMac = cmd.Parameters.Add("$mac_address", SqliteType.Text);
        var pHost = cmd.Parameters.Add("$hostname", SqliteType.Text);
        var pOsName = cmd.Parameters.Add("$os_name", SqliteType.Text);
        var pOsVer = cmd.Parameters.Add("$os_version", SqliteType.Text);
        var pCpuModel = cmd.Parameters.Add("$cpu_model", SqliteType.Text);
        var pCpuCores = cmd.Parameters.Add("$cpu_cores", SqliteType.Integer);
        var pRamMb = cmd.Parameters.Add("$ram_total_mb", SqliteType.Integer);
        var pGpuModel = cmd.Parameters.Add("$gpu_model", SqliteType.Text);
        var pGpuVram = cmd.Parameters.Add("$gpu_vram_mb", SqliteType.Integer);
        var pCollectedAt = cmd.Parameters.Add("$collected_at", SqliteType.Text);

        await using var tx = await _connection.BeginTransactionAsync(ct);
        ((DbCommand)cmd).Transaction = tx;
        foreach (var e in entries)
        {
            pId.Value = e.Id;
            pMac.Value = e.MacAddress;
            pHost.Value = e.Hostname;
            pOsName.Value = e.OsName;
            pOsVer.Value = e.OsVersion;
            pCpuModel.Value = e.CpuModel;
            pCpuCores.Value = e.CpuCores;
            pRamMb.Value = e.RamTotalMb;
            pGpuModel.Value = e.GpuModel;
            pGpuVram.Value = e.GpuVramMb;
            pCollectedAt.Value = e.CollectedAt.ToString("O");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<DeviceHardwareInfo>> GetUnsentDeviceHardwareInfoAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<DeviceHardwareInfo>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM device_hardware_info WHERE is_synced = 0 ORDER BY collected_at ASC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var results = new List<DeviceHardwareInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(MapDeviceHardwareReader(reader));
        return results;
    }

    public async Task MarkDeviceHardwareInfoSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (_connection == null || ids.Count == 0) return;
        await using var tx = await _connection.BeginTransactionAsync(ct);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE device_hardware_info SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now') WHERE id = $id";
        var p = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids) { p.Value = id; await cmd.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    // ────────────────────────────────────────
    // Installed Applications
    // ────────────────────────────────────────

    public async Task StoreInstalledApplicationsAsync(IReadOnlyList<InstalledApplication> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.InsertInstalledApplicationSql;
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pName = cmd.Parameters.Add("$app_name", SqliteType.Text);
        var pVer = cmd.Parameters.Add("$app_version", SqliteType.Text);
        var pPub = cmd.Parameters.Add("$publisher", SqliteType.Text);
        var pPath = cmd.Parameters.Add("$install_path", SqliteType.Text);
        var pDate = cmd.Parameters.Add("$install_date", SqliteType.Text);
        var pUninst = cmd.Parameters.Add("$uninstall_string", SqliteType.Text);
        var pChange = cmd.Parameters.Add("$change_type", SqliteType.Text);
        var pDetected = cmd.Parameters.Add("$detected_at", SqliteType.Text);

        await using var tx = await _connection.BeginTransactionAsync(ct);
        ((DbCommand)cmd).Transaction = tx;
        foreach (var e in entries)
        {
            pId.Value = e.Id;
            pName.Value = e.AppName;
            pVer.Value = e.AppVersion;
            pPub.Value = e.Publisher;
            pPath.Value = e.InstallPath;
            pDate.Value = (object?)e.InstallDate?.ToString("O") ?? DBNull.Value;
            pUninst.Value = e.UninstallString;
            pChange.Value = e.ChangeType;
            pDetected.Value = e.DetectedAt.ToString("O");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<InstalledApplication>> GetUnsentInstalledApplicationsAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<InstalledApplication>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM installed_applications WHERE is_synced = 0 ORDER BY detected_at ASC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var results = new List<InstalledApplication>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(MapInstalledAppReader(reader));
        return results;
    }

    public async Task MarkInstalledApplicationsSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (_connection == null || ids.Count == 0) return;
        await using var tx = await _connection.BeginTransactionAsync(ct);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE installed_applications SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now') WHERE id = $id";
        var p = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids) { p.Value = id; await cmd.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    // ────────────────────────────────────────
    // Network Info
    // ────────────────────────────────────────

    public async Task StoreNetworkInfoAsync(IReadOnlyList<NetworkInfo> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.InsertNetworkInfoSql;
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pPubIp = cmd.Parameters.Add("$public_ip", SqliteType.Text);
        var pPrivIp = cmd.Parameters.Add("$private_ip", SqliteType.Text);
        var pIfName = cmd.Parameters.Add("$network_interface_name", SqliteType.Text);
        var pCollected = cmd.Parameters.Add("$collected_at", SqliteType.Text);

        await using var tx = await _connection.BeginTransactionAsync(ct);
        ((DbCommand)cmd).Transaction = tx;
        foreach (var e in entries)
        {
            pId.Value = e.Id;
            pPubIp.Value = e.PublicIp;
            pPrivIp.Value = e.PrivateIp;
            pIfName.Value = e.NetworkInterfaceName;
            pCollected.Value = e.CollectedAt.ToString("O");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<NetworkInfo>> GetUnsentNetworkInfoAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<NetworkInfo>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM network_info WHERE is_synced = 0 ORDER BY collected_at ASC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var results = new List<NetworkInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(MapNetworkInfoReader(reader));
        return results;
    }

    public async Task MarkNetworkInfoSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (_connection == null || ids.Count == 0) return;
        await using var tx = await _connection.BeginTransactionAsync(ct);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE network_info SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now') WHERE id = $id";
        var p = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids) { p.Value = id; await cmd.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    public async Task<NetworkInfo?> GetLastNetworkInfoAsync(CancellationToken ct)
    {
        if (_connection == null) return null;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.GetLastNetworkInfoSql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new NetworkInfo
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                PublicIp = reader.GetString(reader.GetOrdinal("public_ip")),
                PrivateIp = reader.GetString(reader.GetOrdinal("private_ip")),
                NetworkInterfaceName = reader.GetString(reader.GetOrdinal("network_interface_name")),
                CollectedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("collected_at"))),
            };
        }
        return null;
    }

    // ────────────────────────────────────────
    // Session Events
    // ────────────────────────────────────────

    public async Task StoreSessionEventsAsync(IReadOnlyList<SessionEvent> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.InsertSessionEventSql;
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pType = cmd.Parameters.Add("$event_type", SqliteType.Text);
        var pUser = cmd.Parameters.Add("$os_username", SqliteType.Text);
        var pEventAt = cmd.Parameters.Add("$event_at", SqliteType.Text);

        await using var tx = await _connection.BeginTransactionAsync(ct);
        ((DbCommand)cmd).Transaction = tx;
        foreach (var e in entries)
        {
            pId.Value = e.Id;
            pType.Value = e.EventType;
            pUser.Value = e.OsUsername;
            pEventAt.Value = e.EventAt.ToString("O");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<SessionEvent>> GetUnsentSessionEventsAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<SessionEvent>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM session_events WHERE is_synced = 0 ORDER BY event_at ASC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var results = new List<SessionEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(MapSessionEventReader(reader));
        return results;
    }

    public async Task MarkSessionEventsSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (_connection == null || ids.Count == 0) return;
        await using var tx = await _connection.BeginTransactionAsync(ct);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE session_events SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now') WHERE id = $id";
        var p = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids) { p.Value = id; await cmd.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    // ────────────────────────────────────────
    // App Sessions
    // ────────────────────────────────────────

    public async Task StoreAppSessionsAsync(IReadOnlyList<AppSession> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.InsertAppSessionSql;
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pProc = cmd.Parameters.Add("$process_name", SqliteType.Text);
        var pDisp = cmd.Parameters.Add("$app_display_name", SqliteType.Text);
        var pStart = cmd.Parameters.Add("$started_at", SqliteType.Text);
        var pEnd = cmd.Parameters.Add("$ended_at", SqliteType.Text);
        var pMac = cmd.Parameters.Add("$machine_id", SqliteType.Text);
        var pEmpId = cmd.Parameters.Add("$employee_id", SqliteType.Text);
        var pEmpName = cmd.Parameters.Add("$employee_name", SqliteType.Text);
        var pSessId = cmd.Parameters.Add("$session_id", SqliteType.Text);
        var pPlat = cmd.Parameters.Add("$platform", SqliteType.Text);

        await using var tx = await _connection.BeginTransactionAsync(ct);
        ((DbCommand)cmd).Transaction = tx;
        foreach (var e in entries)
        {
            pId.Value = e.Id;
            pProc.Value = e.ProcessName;
            pDisp.Value = e.AppDisplayName;
            pStart.Value = e.StartedAt.ToString("O");
            pEnd.Value = e.EndedAt?.ToString("O") ?? (object)DBNull.Value;
            pMac.Value = e.MachineId;
            pEmpId.Value = (object?)e.EmployeeId ?? DBNull.Value;
            pEmpName.Value = (object?)e.EmployeeName ?? DBNull.Value;
            pSessId.Value = e.SessionId;
            pPlat.Value = e.Platform;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AppSession>> GetUnsentAppSessionsAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<AppSession>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM app_sessions WHERE is_synced = 0 ORDER BY started_at ASC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var results = new List<AppSession>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(MapAppSessionReader(reader));
        return results;
    }

    public async Task MarkAppSessionsSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (_connection == null || ids.Count == 0) return;
        await using var tx = await _connection.BeginTransactionAsync(ct);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE app_sessions SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now') WHERE id = $id";
        var p = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids) { p.Value = id; await cmd.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    // ────────────────────────────────────────
    // App Items (generic child of app_sessions)
    // ────────────────────────────────────────

    public async Task StoreAppItemsAsync(IReadOnlyList<AppItem> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.InsertAppItemSql;
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pAppSess = cmd.Parameters.Add("$app_session_id", SqliteType.Text);
        var pParent = cmd.Parameters.Add("$parent_item_id", SqliteType.Text);
        var pType = cmd.Parameters.Add("$item_type", SqliteType.Text);
        var pTitle = cmd.Parameters.Add("$title", SqliteType.Text);
        var pIdent = cmd.Parameters.Add("$identifier", SqliteType.Text);
        var pOpened = cmd.Parameters.Add("$opened_at", SqliteType.Text);
        var pClosed = cmd.Parameters.Add("$closed_at", SqliteType.Text);

        await using var tx = await _connection.BeginTransactionAsync(ct);
        ((DbCommand)cmd).Transaction = tx;
        foreach (var e in entries)
        {
            pId.Value = e.Id;
            pAppSess.Value = e.AppSessionId;
            pParent.Value = (object?)e.ParentItemId ?? DBNull.Value;
            pType.Value = e.ItemType;
            pTitle.Value = e.Title;
            pIdent.Value = e.Identifier;
            pOpened.Value = e.OpenedAt.ToString("O");
            pClosed.Value = e.ClosedAt?.ToString("O") ?? (object)DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<AppItem>> GetUnsentAppItemsAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<AppItem>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM app_items WHERE is_synced = 0 ORDER BY opened_at ASC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var results = new List<AppItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(MapAppItemReader(reader));
        return results;
    }

    public async Task MarkAppItemsSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (_connection == null || ids.Count == 0) return;
        await using var tx = await _connection.BeginTransactionAsync(ct);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE app_items SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now') WHERE id = $id";
        var p = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids) { p.Value = id; await cmd.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    // ────────────────────────────────────────
    // Storage Devices (relational child of device_hardware_info)
    // ────────────────────────────────────────

    public async Task StoreStorageDevicesAsync(IReadOnlyList<StorageDevice> entries, CancellationToken ct)
    {
        if (_connection == null || entries.Count == 0) return;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.InsertStorageDeviceSql;
        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pHwId = cmd.Parameters.Add("$device_hardware_id", SqliteType.Text);
        var pType = cmd.Parameters.Add("$device_type", SqliteType.Text);
        var pModel = cmd.Parameters.Add("$model", SqliteType.Text);
        var pCap = cmd.Parameters.Add("$capacity_mb", SqliteType.Integer);

        await using var tx = await _connection.BeginTransactionAsync(ct);
        ((DbCommand)cmd).Transaction = tx;
        foreach (var e in entries)
        {
            pId.Value = e.Id;
            pHwId.Value = e.DeviceHardwareId;
            pType.Value = e.DeviceType;
            pModel.Value = e.Model;
            pCap.Value = e.CapacityMb;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<StorageDevice>> GetUnsentStorageDevicesAsync(int limit, CancellationToken ct)
    {
        if (_connection == null) return Array.Empty<StorageDevice>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM storage_devices WHERE is_synced = 0 ORDER BY created_at ASC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        var results = new List<StorageDevice>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(MapStorageDeviceReader(reader));
        return results;
    }

    public async Task MarkStorageDevicesSentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (_connection == null || ids.Count == 0) return;
        await using var tx = await _connection.BeginTransactionAsync(ct);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE storage_devices SET is_synced = 1, synced_at = strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now') WHERE id = $id";
        var p = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids) { p.Value = id; await cmd.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    // ────────────────────────────────────────
    // Status & Employee Info
    // ────────────────────────────────────────

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

        // Dedup: clean up old entries older than 24 hours to prevent unbounded growth
        var cleanupCmd = _connection.CreateCommand();
        cleanupCmd.CommandText = "DELETE FROM permission_status WHERE checked_at < strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now', '-1 day')";
        _ = await cleanupCmd.ExecuteNonQueryAsync(ct);

        await using var tx = await _connection.BeginTransactionAsync(ct);
        var cmd = _connection.CreateCommand();
        cmd.CommandText = DatabaseSchema.InsertPermissionSql;
        ((DbCommand)cmd).Transaction = tx;

        var checkId = Guid.NewGuid().ToString("N");
        var platform = "Linux";
        if (OperatingSystem.IsWindows()) platform = "Windows";
        else if (OperatingSystem.IsMacOS()) platform = "macOS";

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

    public async Task SaveEmployeeInfoAsync(EmployeeInfo employee, CancellationToken ct)
    {
        if (_connection == null) return;

        var clearCmd = _connection.CreateCommand();
        clearCmd.CommandText = "DELETE FROM employee_info";
        await clearCmd.ExecuteNonQueryAsync(ct);

        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO employee_info (id, employee_id, name, email, role, department, shift, avatar, avatar_color, token, logged_in_at)
            VALUES ($id, $employee_id, $name, $email, $role, $department, $shift, $avatar, $avatar_color, $token, strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
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

    // ────────────────────────────────────────
    // Readers
    // ────────────────────────────────────────

    private static DeviceHardwareInfo MapDeviceHardwareReader(SqliteDataReader r)
    {
        return new DeviceHardwareInfo
        {
            Id = r.GetString(r.GetOrdinal("id")),
            MacAddress = r.GetString(r.GetOrdinal("mac_address")),
            Hostname = r.GetString(r.GetOrdinal("hostname")),
            OsName = r.GetString(r.GetOrdinal("os_name")),
            OsVersion = r.GetString(r.GetOrdinal("os_version")),
            CpuModel = r.GetString(r.GetOrdinal("cpu_model")),
            CpuCores = r.GetInt32(r.GetOrdinal("cpu_cores")),
            RamTotalMb = r.GetInt64(r.GetOrdinal("ram_total_mb")),
            GpuModel = r.GetString(r.GetOrdinal("gpu_model")),
            GpuVramMb = r.GetInt64(r.GetOrdinal("gpu_vram_mb")),
            CollectedAt = DateTime.Parse(r.GetString(r.GetOrdinal("collected_at"))),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static InstalledApplication MapInstalledAppReader(SqliteDataReader r)
    {
        return new InstalledApplication
        {
            Id = r.GetString(r.GetOrdinal("id")),
            AppName = r.GetString(r.GetOrdinal("app_name")),
            AppVersion = r.GetString(r.GetOrdinal("app_version")),
            Publisher = r.GetString(r.GetOrdinal("publisher")),
            InstallPath = r.GetString(r.GetOrdinal("install_path")),
            InstallDate = r.IsDBNull(r.GetOrdinal("install_date")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("install_date"))),
            UninstallString = r.GetString(r.GetOrdinal("uninstall_string")),
            ChangeType = r.GetString(r.GetOrdinal("change_type")),
            DetectedAt = DateTime.Parse(r.GetString(r.GetOrdinal("detected_at"))),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static NetworkInfo MapNetworkInfoReader(SqliteDataReader r)
    {
        return new NetworkInfo
        {
            Id = r.GetString(r.GetOrdinal("id")),
            PublicIp = r.GetString(r.GetOrdinal("public_ip")),
            PrivateIp = r.GetString(r.GetOrdinal("private_ip")),
            NetworkInterfaceName = r.GetString(r.GetOrdinal("network_interface_name")),
            CollectedAt = DateTime.Parse(r.GetString(r.GetOrdinal("collected_at"))),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static SessionEvent MapSessionEventReader(SqliteDataReader r)
    {
        return new SessionEvent
        {
            Id = r.GetString(r.GetOrdinal("id")),
            EventType = r.GetString(r.GetOrdinal("event_type")),
            OsUsername = r.GetString(r.GetOrdinal("os_username")),
            EventAt = DateTime.Parse(r.GetString(r.GetOrdinal("event_at"))),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static AppSession MapAppSessionReader(SqliteDataReader r)
    {
        return new AppSession
        {
            Id = r.GetString(r.GetOrdinal("id")),
            ProcessName = r.GetString(r.GetOrdinal("process_name")),
            AppDisplayName = r.GetString(r.GetOrdinal("app_display_name")),
            StartedAt = DateTime.Parse(r.GetString(r.GetOrdinal("started_at"))),
            EndedAt = r.IsDBNull(r.GetOrdinal("ended_at")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("ended_at"))),
            MachineId = r.GetString(r.GetOrdinal("machine_id")),
            EmployeeId = r.IsDBNull(r.GetOrdinal("employee_id")) ? null : r.GetString(r.GetOrdinal("employee_id")),
            EmployeeName = r.IsDBNull(r.GetOrdinal("employee_name")) ? null : r.GetString(r.GetOrdinal("employee_name")),
            SessionId = r.GetString(r.GetOrdinal("session_id")),
            Platform = r.GetString(r.GetOrdinal("platform")),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }    private static StorageDevice MapStorageDeviceReader(SqliteDataReader r)
    {
        return new StorageDevice
        {
            Id = r.GetString(r.GetOrdinal("id")),
            DeviceHardwareId = r.GetString(r.GetOrdinal("device_hardware_id")),
            DeviceType = r.GetString(r.GetOrdinal("device_type")),
            Model = r.GetString(r.GetOrdinal("model")),
            CapacityMb = r.GetInt64(r.GetOrdinal("capacity_mb")),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }

    private static AppItem MapAppItemReader(SqliteDataReader r)
    {
        return new AppItem
        {
            Id = r.GetString(r.GetOrdinal("id")),
            AppSessionId = r.GetString(r.GetOrdinal("app_session_id")),
            ParentItemId = r.IsDBNull(r.GetOrdinal("parent_item_id")) ? null : r.GetString(r.GetOrdinal("parent_item_id")),
            ItemType = r.GetString(r.GetOrdinal("item_type")),
            Title = r.GetString(r.GetOrdinal("title")),
            Identifier = r.GetString(r.GetOrdinal("identifier")),
            OpenedAt = DateTime.Parse(r.GetString(r.GetOrdinal("opened_at"))),
            ClosedAt = r.IsDBNull(r.GetOrdinal("closed_at")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("closed_at"))),
            IsSynced = r.GetInt32(r.GetOrdinal("is_synced")) == 1,
            SyncedAt = r.IsDBNull(r.GetOrdinal("synced_at")) ? null : r.GetString(r.GetOrdinal("synced_at")),
            CreatedAt = r.IsDBNull(r.GetOrdinal("created_at")) ? string.Empty : r.GetString(r.GetOrdinal("created_at")),
        };
    }
}
