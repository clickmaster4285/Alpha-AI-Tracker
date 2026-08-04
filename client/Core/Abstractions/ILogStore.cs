using client.Core;
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

    Task StoreInstalledPackagesAsync(IReadOnlyList<InstalledPackage> entries, CancellationToken ct);
    Task<IReadOnlyList<InstalledPackage>> GetUnsentInstalledPackagesAsync(int limit, CancellationToken ct);
    Task MarkInstalledPackagesSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

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

    // ── Installed App/Package Lookup (binary name → display name mapping) ──

    /// <summary>Look up an installed app by its executable binary name (e.g., \"code\" → Visual Studio Code)</summary>
    Task<InstalledApplication?> GetInstalledAppByBinaryNameAsync(string binaryName, CancellationToken ct);

    /// <summary>Fuzzy lookup by binary_name or app_name using LIKE (e.g., \"chrome\" matches \"google-chrome-stable\").
    /// Returns null if no fuzzy match found. Used to prevent duplicate entries when runtime process name
    /// differs from the .desktop binary name (e.g., process=\"chrome\", binary_name=\"google-chrome-stable\").</summary>
    Task<InstalledApplication?> GetInstalledAppByBinaryNameFuzzyAsync(string processName, CancellationToken ct);

    /// <summary>Look up an installed package by its package name</summary>
    Task<InstalledPackage?> GetInstalledPackageByNameAsync(string packageName, CancellationToken ct);

    /// <summary>Get all installed app binary names for fast in-memory filtering</summary>
    Task<HashSet<string>> GetAllInstalledAppBinaryNamesAsync(CancellationToken ct);

    /// <summary>Get all installed package names for fast in-memory filtering</summary>
    Task<HashSet<string>> GetAllInstalledPackageNamesAsync(CancellationToken ct);

    /// <summary>Store a single auto-detected installed app (called when a running process is not yet in the DB).
    /// Returns the actual stored ID (may differ from entry.Id if app_name already existed via ON CONFLICT).</summary>
    Task<string> StoreInstalledAppAsync(InstalledApplication entry, CancellationToken ct);

    /// <summary>Delete a non-GUI installed_applications entry that was incorrectly auto-registered.
    /// Used by Phase 0a cleanup to remove shell/system entries (sh, snap) from the DB.</summary>
    Task DeleteInstalledAppAsync(string id, CancellationToken ct);

    /// <summary>Store a single auto-detected installed package (called when a running process is not yet in the DB).
    /// Returns the actual stored ID.</summary>
    Task<string> StoreInstalledPackageAsync(InstalledPackage entry, CancellationToken ct);

    // ── Application Logs ──

    Task StoreAppSessionsAsync(IReadOnlyList<AppSession> entries, CancellationToken ct);
    Task<IReadOnlyList<AppSession>> GetUnsentAppSessionsAsync(int limit, CancellationToken ct);
    Task MarkAppSessionsSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    // ── Generic App Items (child of app_sessions) ──

    Task StoreAppItemsAsync(IReadOnlyList<AppItem> entries, CancellationToken ct);
    Task<IReadOnlyList<AppItem>> GetUnsentAppItemsAsync(int limit, CancellationToken ct);
    Task MarkAppItemsSentAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>Update an app item's parent_item_id (for post-creation parent-child linking)</summary>
    Task UpdateAppItemParentAsync(string itemId, string parentItemId, CancellationToken ct);

    /// <summary>Open sessions with PID for hierarchy resolution (ended_at IS NULL).</summary>
    Task<IReadOnlyList<OpenSessionRecord>> GetOpenSessionRecordsAsync(CancellationToken ct);

    /// <summary>
    /// ALL open sessions regardless of process_id (for crash recovery).
    /// Unlike GetOpenSessionRecordsAsync, this does NOT filter by process_id IS NOT NULL,
    /// so it catches sessions that never had a PID assigned.
    /// </summary>
    Task<IReadOnlyList<OpenSessionRecord>> GetAllOpenSessionRecordsAsync(CancellationToken ct);

    /// <summary>Batch-close all app_items for a set of session IDs (for crash recovery).</summary>
    Task CloseAppItemsBySessionIdsAsync(IReadOnlyList<string> sessionIds, DateTime closedAt, CancellationToken ct);

    /// <summary>
    /// Atomically close a set of sessions AND their still-open app_items in ONE transaction.
    /// Acquires the connection gate once, so it is safe to call without nesting gated public methods.
    /// </summary>
    Task CloseSessionsAndAppItemsAsync(IReadOnlyList<AppSession> closeSessions, DateTime closedAt, CancellationToken ct);

    /// <summary>Find an open context child item by type+identifier under a session.</summary>
    Task<AppItem?> GetOpenAppItemAsync(string appSessionId, string itemType, string identifier, CancellationToken ct);

    /// <summary>Find an open journey event by journey_id + object_type + action + current_path.</summary>
    Task<AppItem?> GetOpenJourneyEventAsync(string journeyId, string objectType, string action, string currentPath, CancellationToken ct);

    /// <summary>Get the next sequence number for a journey (MAX(sequence) + 1).</summary>
    Task<int> GetNextSequenceAsync(string journeyId, CancellationToken ct);

    /// <summary>Update title/identifier on an existing open app item (URL/path change).</summary>
    Task UpdateAppItemContextAsync(string itemId, string title, string identifier, CancellationToken ct);

    /// <summary>Begin a transaction for atomic multi-write operations.</summary>
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct);

    /// <summary>Whether any storage device rows exist for the latest hardware record.</summary>
    Task<bool> HasStorageDevicesAsync(CancellationToken ct);

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
