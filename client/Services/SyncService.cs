using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Dedicated sync engine — runs on its OWN background loop so the collection loop in
/// LogCollectorService never blocks on the network. Drains unsent SQLite rows in adaptive
/// chunks bounded by BOTH row count (ALPHA_SYNC_MAX_ROWS) and serialized payload bytes
/// (ALPHA_SYNC_MAX_BYTES), gzips request bodies (server side: middleware.Decompress),
/// pauses politely between chunks (ALPHA_SYNC_CHUNK_DELAY_MS) so a backlog never spikes
/// CPU or network, and backs off exponentially on failure.
///
/// Large-backlog behavior (e.g. 50,000+ queued rows after a long offline period): instead
/// of the old inline sync that fetched a fixed 500 rows/table per 5-minute cycle (≈8 hours
/// to drain 50k, while BLOCKING collection), this service loops per table — fetch → send →
/// mark → short pause → next chunk — until drained or the per-pass time budget
/// (ALPHA_SYNC_MAX_DURATION_SEC) is spent, then the next pass continues. The client stays
/// fully responsive the whole time.
///
/// Idempotency is preserved: the server upserts by client GUID (ON CONFLICT (id) …), so a
/// chunk that fails after a partial send is retried safely next pass.
/// </summary>
public class SyncService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly ILogger<SyncService> _logger;
    private readonly HttpClient _httpClient;

    // Wake-up signal for an IMMEDIATE sync — released by RequestImmediateSync()
    // right after a successful login (or session restore), so all unsent tables
    // (device_hardware_info, employee profile data, installed apps/packages,
    // network, storage, hardware devices, permissions, …) land on the server
    // instantly instead of waiting for the next idle poll.
    private readonly SemaphoreSlim _syncSignal = new(0, 1);

    private string? _employeeId;
    private string? _employeeName;
    private string? _token;

    // Exponential backoff on failed passes: 5s → 10s → 20s → … → SyncBackoffMaxSec.
    private TimeSpan _backoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LoginPollDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Requests an immediate drain pass. Called by the login flow the moment employee
    /// credentials are persisted — the next pass starts right away instead of waiting
    /// out the idle interval. Safe to call before the service loop is running and from
    /// any thread (the semaphore is 0/1, so a second request while one is pending is a
    /// no-op — one pass covers both).
    /// </summary>
    public void RequestImmediateSync()
    {
        try
        {
            if (_syncSignal.CurrentCount == 0)
                _syncSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown race — nothing to signal.
        }
        catch (SemaphoreFullException)
        {
            // A pass is already pending — one drain covers it.
        }
    }

    // Built once — the per-table closures capture `this` (fields are read at call time,
    // so employee identity and config stay live across passes).
    private readonly List<Func<Stopwatch, TimeSpan, CancellationToken, Task<bool>>> _drainPass;

    public SyncService(AppConfig config, ILogStore store, ILogger<SyncService> logger, HttpClient httpClient)
    {
        _config = config;
        _store = store;
        _logger = logger;
        _httpClient = httpClient;
        _drainPass = BuildDrainPass();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "SyncService starting (interval={Interval}s, maxRows={MaxRows}, maxBytes={MaxBytes}, compression={Compression})",
            _config.SyncIntervalSec, _config.SyncMaxRows, _config.SyncMaxBytes, _config.SyncCompression);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RefreshEmployeeInfoAsync(stoppingToken))
                {
                    // Not logged in yet — poll until employee credentials persist (Login flow).
                    await Task.Delay(LoginPollDelay, stoppingToken);
                    continue;
                }

                var passSw = Stopwatch.StartNew();
                var passBudget = TimeSpan.FromSeconds(Math.Max(10, _config.SyncMaxDurationSec));

                bool failed = false;
                foreach (var drain in _drainPass)
                {
                    if (passSw.Elapsed >= passBudget) break;
                    if (await drain(passSw, passBudget, stoppingToken))
                        failed = true;
                }

                // Retention cleanup (2026-08-11): after a clean pass, prune rows the server
                // already has and that are no longer needed locally — app_items/app_sessions
                // older than ALPHA_SYNC_RETENTION_HOURS, uninstalled inventory cycles, and
                // superseded network rows. Never runs while a pass had failures (so nothing
                // unsent is ever deleted).
                if (!failed)
                {
                    var cutoff = DateTime.UtcNow.AddHours(-_config.SyncRetentionHours);
                    var deleted = await _store.DeleteSyncedDataOlderThanAsync(cutoff, stoppingToken);
                    if (deleted.Total > 0)
                    {
                        _logger.LogDebug(
                            "Retention cleanup deleted {Total} rows (items={Items}, sessions={Sessions}, apps={Apps}, packages={Packages}, network={Network})",
                            deleted.Total, deleted.AppItems, deleted.AppSessions,
                            deleted.InstalledApps, deleted.InstalledPackages, deleted.NetworkRows);
                    }
                }

                if (failed)
                {
                    _backoff = TimeSpan.FromSeconds(
                        Math.Min(_config.SyncBackoffMaxSec, Math.Max(5, _backoff.TotalSeconds * 2)));
                    _logger.LogDebug("Sync pass had failures — backing off {Backoff}s", _backoff.TotalSeconds);
                }
                else
                {
                    _backoff = TimeSpan.FromSeconds(5);
                }

                // Between passes: grow the wait on failure (backoff), otherwise the idle interval.
                // The wait is interruptible — RequestImmediateSync() (login) releases the
                // semaphore and this returns at once for an instant drain pass.
                var wait = failed ? _backoff : TimeSpan.FromSeconds(Math.Max(1, _config.SyncIntervalSec));
                await _syncSignal.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sync pass failed unexpectedly");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        _logger.LogInformation("SyncService stopped");
    }

    /// <summary>
    /// The per-pass table order — small/inventory tables first, then sessions, then items
    /// (parents before children, smallest payloads first so the UI-facing data lands early).
    /// </summary>
    private List<Func<Stopwatch, TimeSpan, CancellationToken, Task<bool>>> BuildDrainPass()
    {
        var tasks = new List<Func<Stopwatch, TimeSpan, CancellationToken, Task<bool>>>();

        tasks.Add((sw, budget, ct) => DrainTableAsync<DeviceHardwareInfo>(
            "/api/v1/device-hardware/sync",
            (limit, token) => _store.GetUnsentDeviceHardwareInfoAsync(limit, token),
            e => new
            {
                id = e.Id,
                macAddress = e.MacAddress,
                hostname = e.Hostname,
                osName = e.OsName,
                osVersion = e.OsVersion,
                cpuModel = e.CpuModel,
                cpuCores = e.CpuCores,
                ramTotalMb = e.RamTotalMb,
                gpuModel = e.GpuModel,
                gpuVramMb = e.GpuVramMb,
                storageDevices = e.StorageDevices,
                collectedAt = e.CollectedAt.ToString("O"),
            },
            (ids, token) => _store.MarkDeviceHardwareInfoSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainTableAsync<NetworkInfo>(
            "/api/v1/network-info/sync",
            (limit, token) => _store.GetUnsentNetworkInfoAsync(limit, token),
            e => new
            {
                id = e.Id,
                publicIp = e.PublicIp,
                privateIp = e.PrivateIp,
                networkInterfaceName = e.NetworkInterfaceName,
                collectedAt = e.CollectedAt.ToString("O"),
            },
            (ids, token) => _store.MarkNetworkInfoSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        // Storage devices (children of device_hardware_info) — sent to server, never deleted client-side.
        tasks.Add((sw, budget, ct) => DrainTableAsync<StorageDevice>(
            "/api/v1/storage-devices/sync",
            (limit, token) => _store.GetUnsentStorageDevicesAsync(limit, token),
            e => new
            {
                id = e.Id,
                deviceHardwareId = e.DeviceHardwareId,
                deviceType = e.DeviceType,
                model = e.Model,
                capacityMb = e.CapacityMb,
            },
            (ids, token) => _store.MarkStorageDevicesSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        // Hardware devices (USB/peripheral hotplug) — sent to server, never deleted client-side.
        tasks.Add((sw, budget, ct) => DrainTableAsync<HardwareDevice>(
            "/api/v1/hardware-devices/sync",
            (limit, token) => _store.GetUnsentHardwareDevicesAsync(limit, token),
            e => new
            {
                id = e.Id,
                deviceClass = e.DeviceClass,
                vendor = e.Vendor,
                product = e.Product,
                serial = e.Serial,
                busPath = e.BusPath,
                deviceNode = e.DeviceNode,
                pluggedAt = e.PluggedAt.ToString("O"),
                unpluggedAt = e.UnpluggedAt?.ToString("O"),
            },
            (ids, token) => _store.MarkHardwareDevicesSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainTableAsync<SessionEvent>(
            "/api/v1/session-events/sync",
            (limit, token) => _store.GetUnsentSessionEventsAsync(limit, token),
            e => new
            {
                id = e.Id,
                eventType = e.EventType,
                osUsername = e.OsUsername,
                eventAt = e.EventAt.ToString("O"),
            },
            (ids, token) => _store.MarkSessionEventsSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainTableAsync<InstalledApplication>(
            "/api/v1/installed-apps/sync",
            (limit, token) => _store.GetUnsentInstalledApplicationsAsync(limit, token),
            e => new
            {
                id = e.Id,
                appName = e.AppName,
                appVersion = e.AppVersion,
                publisher = e.Publisher,
                installPath = e.InstallPath,
                installDate = e.InstallDate?.ToString("O"),
                uninstallString = e.UninstallString,
                changeType = e.ChangeType,
                detectedAt = e.DetectedAt.ToString("O"),
                binaryName = e.BinaryName,
                isBrowser = e.IsBrowser,
                desktopId = e.DesktopId,
                categories = e.Categories,
            },
            (ids, token) => _store.MarkInstalledApplicationsSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainTableAsync<InstalledPackage>(
            "/api/v1/installed-packages/sync",
            (limit, token) => _store.GetUnsentInstalledPackagesAsync(limit, token),
            e => new
            {
                id = e.Id,
                packageName = e.PackageName,
                version = e.Version,
                category = e.Category,
                sourceManager = e.SourceManager,
                installPath = e.InstallPath,
                publisher = e.Publisher,
                description = e.Description,
                detectedAt = e.DetectedAt.ToString("O"),
            },
            (ids, token) => _store.MarkInstalledPackagesSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainTableAsync<AppSession>(
            "/api/v1/app-sessions/sync",
            (limit, token) => _store.GetUnsentAppSessionsAsync(limit, token),
            e => new
            {
                id = e.Id,
                processName = e.ProcessName,
                appDisplayName = e.AppDisplayName,
                startedAt = e.StartedAt.ToString("O"),
                endedAt = e.EndedAt?.ToString("O"),
                machineId = e.MachineId,
                employeeId = _employeeId,
                employeeName = _employeeName,
                sessionId = e.SessionId,
                platform = e.Platform,
                installedAppId = e.InstalledAppId,
                installedPackageId = e.InstalledPackageId,
                processId = e.ProcessId,
                parentProcessId = e.ParentProcessId,
                groupedBy = e.GroupedBy,
                cgroupScope = e.CgroupScope,
                contextLabel = e.ContextLabel,
            },
            (ids, token) => _store.MarkAppSessionsSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        tasks.Add((sw, budget, ct) => DrainTableAsync<AppItem>(
            "/api/v1/app-items/sync",
            (limit, token) => _store.GetUnsentAppItemsAsync(limit, token),
            e => new
            {
                id = e.Id,
                appSessionId = e.AppSessionId,
                parentItemId = e.ParentItemId,
                itemType = e.ItemType,
                title = e.Title,
                identifier = e.Identifier,
                url = e.Url,
                domain = e.Domain,
                openedAt = e.OpenedAt.ToString("O"),
                closedAt = e.ClosedAt?.ToString("O"),
                processId = e.ProcessId,
                objectType = e.ObjectType,
                action = e.Action,
                journeyId = e.JourneyId,
                sequence = e.Sequence,
                previousPath = e.PreviousPath,
                currentPath = e.CurrentPath,
                windowId = e.WindowId,
                tabId = e.TabId,
                metadataJson = e.MetadataJson,
            },
            (ids, token) => _store.MarkAppItemsSentAsync(ids, token),
            e => e.Id,
            sw, budget, ct));

        // App status (key/value) — changed rows are re-sent every roundtrip, never deleted client-side.
        tasks.Add((sw, budget, ct) => DrainTableAsync<AppStatus>(
            "/api/v1/app-status/sync",
            (limit, token) => _store.GetUnsentAppStatusAsync(limit, token),
            e => new
            {
                key = e.Key,
                value = e.Value,
                updatedAt = e.UpdatedAt,
            },
            (ids, token) => _store.MarkAppStatusSentAsync(ids, token),
            e => e.Key,
            sw, budget, ct));

        // Permission status — one row per permission method (deduped 2026-08-11); sent to server, never deleted client-side.
        tasks.Add((sw, budget, ct) => DrainTableAsync<PermissionStatus>(
            "/api/v1/permission-status/sync",
            (limit, token) => _store.GetUnsentPermissionStatusAsync(limit, token),
            e => new
            {
                checkId = e.CheckId,
                sessionId = e.SessionId,
                sessionType = e.SessionType,
                platform = e.Platform,
                checkedAt = e.CheckedAt,
                method = e.Method,
                works = e.Works,
                details = e.Details,
            },
            (ids, token) => _store.MarkPermissionStatusSentAsync(ids, token),
            e => e.CheckId,
            sw, budget, ct));

        return tasks;
    }

    /// <summary>
    /// Drains ONE table: fetches a row-capped chunk, splits it into byte-bounded slices,
    /// sends each slice, marks it sent, pauses politely, and repeats until the table is
    /// drained or the pass budget expires. Returns true if any send failed (triggers
    /// exponential backoff — the next pass resumes where the failed chunk left off).
    /// </summary>
    private async Task<bool> DrainTableAsync<T>(
        string endpoint,
        Func<int, CancellationToken, Task<IReadOnlyList<T>>> fetchFn,
        Func<T, object> mapFn,
        Func<IReadOnlyList<string>, CancellationToken, Task> markSentFn,
        Func<T, string?> idOf,
        Stopwatch passSw,
        TimeSpan passBudget,
        CancellationToken ct)
    {
        var synced = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (passSw.Elapsed >= passBudget) break;

                var entries = await fetchFn(_config.SyncMaxRows, ct);
                if (entries.Count == 0) break;

                var remaining = entries;
                while (remaining.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (passSw.Elapsed >= passBudget) break;

                    // Byte-bound the slice: serialize, and if it exceeds the cap, halve until
                    // it fits (rare — only fires when individual rows are very large).
                    var slice = remaining;
                    byte[] json;
                    while (true)
                    {
                        var payload = new
                        {
                            employeeId = _employeeId,
                            token = _token,
                            entries = slice.Select(mapFn).ToList()
                        };
                        json = JsonSerializer.SerializeToUtf8Bytes(payload);
                        if (json.Length <= _config.SyncMaxBytes || slice.Count <= 1) break;
                        slice = slice.Take(slice.Count / 2).ToList();
                    }

                    if (!await SendAsync(endpoint, json, ct))
                        return true; // failed — stop this table, back off, resume next pass

                    var ids = slice.Select(idOf).Where(id => !string.IsNullOrEmpty(id)).Cast<string>().ToList();
                    if (ids.Count > 0)
                        await markSentFn(ids, ct);
                    synced += ids.Count;

                    remaining = slice.Count < remaining.Count
                        ? remaining.Skip(slice.Count).ToList()
                        : Array.Empty<T>();

                    if (_config.SyncChunkDelayMs > 0)
                        await Task.Delay(_config.SyncChunkDelayMs, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error draining {Endpoint}", endpoint);
            return true;
        }

        if (synced > 0)
            _logger.LogDebug("Synced {Count} rows to {Endpoint}", synced, endpoint);
        return false;
    }

    /// <summary>
    /// POSTs one serialized slice. Request bodies are gzip-compressed when enabled (tiny
    /// payloads are skipped — compression overhead isn't worth it below ~512 bytes).
    /// Per-request timeout is bound via a linked CTS (the shared HttpClient.Timeout is the
    /// hard ceiling). 2xx = every row in this slice is durable server-side (idempotent
    /// upserts by client GUID), so the rows can be marked sent.
    /// </summary>
    private async Task<bool> SendAsync(string endpoint, byte[] json, CancellationToken ct)
    {
        var serverUrl = _config.ServerUrl ?? "http://localhost:8080";
        try
        {
            HttpContent content;
            if (_config.SyncCompression && json.Length > 512)
            {
                await using var ms = new MemoryStream();
                await using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    await gz.WriteAsync(json, ct);
                content = new ByteArrayContent(ms.ToArray());
                content.Headers.ContentEncoding.Add("gzip");
            }
            else
            {
                content = new ByteArrayContent(json);
            }
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var response = await _httpClient.PostAsync($"{serverUrl}{endpoint}", content, cts.Token);

            if (response.IsSuccessStatusCode)
                return true;

            if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
            {
                _logger.LogWarning("Auth failed (status {Status}) for {Endpoint}", (int)response.StatusCode, endpoint);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Sync failed for {Endpoint} (status {Status}): {Body}",
                    endpoint, (int)response.StatusCode, body);
            }
            return false;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Sync failed for {Endpoint} (server unreachable)", endpoint);
            return false;
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Sync timed out for {Endpoint}", endpoint);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync {Endpoint}", endpoint);
            return false;
        }
    }

    private async Task<bool> RefreshEmployeeInfoAsync(CancellationToken ct)
    {
        try
        {
            var info = await _store.GetEmployeeInfoAsync(ct);
            if (info == null || string.IsNullOrEmpty(info.Token))
            {
                _employeeId = _employeeName = _token = null;
                return false;
            }
            _employeeId = info.EmployeeId;
            _employeeName = info.Name;
            _token = info.Token;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
