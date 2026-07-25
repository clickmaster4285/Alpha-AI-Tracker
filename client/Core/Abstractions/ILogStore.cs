using client.Core.Models;

namespace client.Core.Abstractions;

public interface ILogStore
{
    Task InitializeAsync(CancellationToken ct);

    // ── Device & System Info ──

    Task StoreDeviceHardwareInfoAsync(IReadOnlyList<DeviceHardwareInfo> entries, CancellationToken ct);
    Task<IReadOnlyList<DeviceHardwareInfo>> GetUnsentDeviceHardwareInfoAsync(int limit, CancellationToken ct);
    Task MarkDeviceHardwareInfoSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    Task StoreInstalledApplicationsAsync(IReadOnlyList<InstalledApplication> entries, CancellationToken ct);
    Task<IReadOnlyList<InstalledApplication>> GetUnsentInstalledApplicationsAsync(int limit, CancellationToken ct);
    Task MarkInstalledApplicationsSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    Task StoreNetworkInfoAsync(IReadOnlyList<NetworkInfo> entries, CancellationToken ct);
    Task<IReadOnlyList<NetworkInfo>> GetUnsentNetworkInfoAsync(int limit, CancellationToken ct);
    Task MarkNetworkInfoSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    Task StoreSessionEventsAsync(IReadOnlyList<SessionEvent> entries, CancellationToken ct);
    Task<IReadOnlyList<SessionEvent>> GetUnsentSessionEventsAsync(int limit, CancellationToken ct);
    Task MarkSessionEventsSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    // ── Storage Devices (relational child of device_hardware_info) ──

    Task StoreStorageDevicesAsync(IReadOnlyList<StorageDevice> entries, CancellationToken ct);
    Task<IReadOnlyList<StorageDevice>> GetUnsentStorageDevicesAsync(int limit, CancellationToken ct);
    Task MarkStorageDevicesSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    // ── Application Logs ──

    Task StoreAppSessionsAsync(IReadOnlyList<AppSession> entries, CancellationToken ct);
    Task<IReadOnlyList<AppSession>> GetUnsentAppSessionsAsync(int limit, CancellationToken ct);
    Task MarkAppSessionsSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    // ── Generic App Items (child of app_sessions) ──

    Task StoreAppItemsAsync(IReadOnlyList<AppItem> entries, CancellationToken ct);
    Task<IReadOnlyList<AppItem>> GetUnsentAppItemsAsync(int limit, CancellationToken ct);
    Task MarkAppItemsSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    // ── Network dedup helper ──

    Task<NetworkInfo?> GetLastNetworkInfoAsync(CancellationToken ct);

    // ── Status & Employee Info ──

    Task SetStatusAsync(string key, string value, CancellationToken ct);
    Task<string?> GetStatusAsync(string key, CancellationToken ct);
    Task SetPermissionStatusAsync(IReadOnlyDictionary<string, bool> permissions, string sessionType, CancellationToken ct);
    Task SaveEmployeeInfoAsync(EmployeeInfo employee, CancellationToken ct);
    Task<EmployeeInfo?> GetEmployeeInfoAsync(CancellationToken ct);
    Task ClearEmployeeInfoAsync(CancellationToken ct);
}
