using System.Text.Json;

namespace client.Core.Models;

public class DeviceHardwareInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MacAddress { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string OsName { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string CpuModel { get; set; } = string.Empty;
    public int CpuCores { get; set; }
    public long RamTotalMb { get; set; }

    public string GpuModel { get; set; } = string.Empty;
    public long GpuVramMb { get; set; }
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

public class InstalledApplication
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AppName { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string InstallPath { get; set; } = string.Empty;
    public DateTime? InstallDate { get; set; }
    public string UninstallString { get; set; } = string.Empty;
    /// <summary>"installed" | "uninstalled" | "seen"</summary>
    public string ChangeType { get; set; } = "seen";
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

public class NetworkInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PublicIp { get; set; } = string.Empty;
    public string PrivateIp { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string NetworkInterfaceName { get; set; } = string.Empty;
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

public class StorageDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>FK → device_hardware_info.id</summary>
    public string DeviceHardwareId { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public long CapacityMb { get; set; }
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

public class SessionEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>"login" | "logout" | "lock" | "unlock"</summary>
    public string EventType { get; set; } = string.Empty;
    public string OsUsername { get; set; } = string.Empty;
    public DateTime EventAt { get; set; } = DateTime.UtcNow;
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}
