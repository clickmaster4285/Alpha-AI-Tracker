using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace client.Services;

public class LogCollectorService : BackgroundService
{
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    private readonly AppConfig _config;
    private readonly IActivityCollector _collector;
    private readonly ILogStore _store;
    private readonly ILogger<LogCollectorService> _logger;
    private readonly HttpClient _httpClient;
    private readonly IInstalledAppDetector _appDetector;
    private int _cycleCount;
    private string? _currentEmployeeId;
    private string? _currentEmployeeName;
    private string? _currentToken;
    private bool _trackingEnabled;
    private bool _previousTrackingState;
    private readonly object _trackingLock = new();
    private DateTime _lastHardwareCollection = DateTime.MinValue;
    private DateTime _lastNetworkCollection = DateTime.MinValue;
    private DateTime _lastInstalledAppScan = DateTime.MinValue;
    private string? _lastNetworkPublicIp;
    private string? _lastNetworkPrivateIp;
    private string? _lastHardwareFingerprint;

    // Session ended_at tracking: key = processName|displayName|machineId|sessionId → AppSession.Id
    private readonly Dictionary<string, string> _previousSessionKeys = new();

    // System processes that are never application sessions
    private static readonly HashSet<string> NonAppProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "gnome-shell", "Xwayland", "pipewire-pulse", "wireplumber",
        "chrome_crashpad_handler", "cat", "tracker-extract", "tracker-store",
        "evolution-calendar-factory", "evolution-addressbook-factory", "goa-daemon",
        "gsd-power", "gsd-wacom", "gsd-color", "gsd-print-notifications",
        "gsd-keyboard", "gsd-media-keys", "gsd-mouse", "gsd-sound",
        "gsd-xsettings", "gsd-datetime", "gsd-housekeeping",
        "ibus-engine-libpinyin", "ibus-extension-gtk3",
    };

    public LogCollectorService(
        AppConfig config,
        IActivityCollector collector,
        ILogStore store,
        ILogger<LogCollectorService> logger,
        HttpClient httpClient,
        IInstalledAppDetector appDetector)
    {
        _config = config;
        _collector = collector;
        _store = store;
        _logger = logger;
        _httpClient = httpClient;
        _appDetector = appDetector;
    }

    public void StartTracking()
    {
        lock (_trackingLock)
        {
            _previousTrackingState = _trackingEnabled;
            _trackingEnabled = true;
            _cycleCount = 0;
            _logger.LogInformation("Tracking started");
        }

        // Record login session event
        _ = RecordSessionEventAsync("login", stoppingToken: default);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                _logger.LogInformation("Windows execution state set to prevent sleep");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to set Windows execution state");
            }
        }
    }

    public void StopTracking()
    {
        lock (_trackingLock)
        {
            _previousTrackingState = _trackingEnabled;
            _trackingEnabled = false;
            _currentEmployeeId = null;
            _currentEmployeeName = null;
            _currentToken = null;
            _logger.LogInformation("Tracking stopped");
        }

        // Record logout session event
        _ = RecordSessionEventAsync("logout", stoppingToken: default);

        if (OperatingSystem.IsWindows())
        {
            try { SetThreadExecutionState(ES_CONTINUOUS); }
            catch { }
        }
    }

    public void SetEmployeeInfo(string employeeId, string employeeName, string token)
    {
        lock (_trackingLock)
        {
            _currentEmployeeId = employeeId;
            _currentEmployeeName = employeeName;
            _currentToken = token;
        }
    }

    public bool IsTrackingEnabled
    {
        get { lock (_trackingLock) { return _trackingEnabled; } }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "LogCollectorService starting (machine={MachineId}, interval={Interval}s, session={SessionId}) — waiting for login to begin tracking",
            _config.ClientId, _config.CollectIntervalSec, SessionInfo.SessionId);

        await _store.InitializeAsync(stoppingToken);
        await RefreshEmployeeInfo(stoppingToken);

        var interval = TimeSpan.FromSeconds(Math.Max(5, _config.CollectIntervalSec));

        // Collect hardware and network info immediately on startup
        await CollectDeviceHardwareAsync(stoppingToken);
        await CollectNetworkInfoAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_trackingEnabled)
                {
                    await Task.Delay(5000, stoppingToken);
                    continue;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();

                if (_cycleCount % 6 == 0)
                {
                    await RefreshEmployeeInfo(stoppingToken);
                }

                // ─── Collect process snapshots → track AppSessions with ended_at ───
                var allLogs = await _collector.CollectAsync(stoppingToken);

                var installedAppLogs = allLogs
                    .Where(l => IsInstalledApp(l.ProcessName, l.WindowTitle))
                    .ToList();

                var filteredLogs = installedAppLogs
                    .Where(l => !string.IsNullOrWhiteSpace(l.WindowTitle))
                    .ToList();

                // Build set of current running session keys
                var currentKeys = new Dictionary<string, string>();
                foreach (var log in filteredLogs)
                {
                    var key = $"{log.ProcessName}|{log.WindowTitle ?? ""}|{_config.ClientId}|{SessionInfo.SessionId}";
                    currentKeys[key] = string.Empty;
                }

                // Close sessions that were running last cycle but aren't now
                var closeSessions = new List<AppSession>();
                foreach (var kvp in _previousSessionKeys)
                {
                    if (!currentKeys.ContainsKey(kvp.Key))
                    {
                        // Session ended — close it
                        closeSessions.Add(new AppSession
                        {
                            Id = kvp.Value,
                            EndedAt = DateTime.UtcNow
                        });
                    }
                    else
                    {
                        // Still running — pass the id to currentKeys
                        currentKeys[kvp.Key] = kvp.Value;
                    }
                }

                // Create new sessions for apps not previously tracked
                var newSessions = new List<AppSession>();
                var newItems = new List<AppItem>();
                foreach (var log in filteredLogs)
                {
                    var key = $"{log.ProcessName}|{log.WindowTitle ?? ""}|{_config.ClientId}|{SessionInfo.SessionId}";

                    if (!string.IsNullOrEmpty(currentKeys.GetValueOrDefault(key)))
                        continue; // already tracked

                    var session = new AppSession
                    {
                        ProcessName = log.ProcessName,
                        AppDisplayName = log.WindowTitle ?? log.ProcessName,
                        StartedAt = log.Timestamp,
                        EndedAt = null,
                        MachineId = _config.ClientId,
                        EmployeeId = _currentEmployeeId,
                        EmployeeName = _currentEmployeeName,
                        SessionId = SessionInfo.SessionId,
                        Platform = log.Platform,
                    };
                    newSessions.Add(session);
                    currentKeys[key] = session.Id;

                    // Create a generic AppItem row for this session
                    newItems.Add(new AppItem
                    {
                        AppSessionId = session.Id,
                        ItemType = IsShellProcess(log.ProcessName) ? "terminal" : "tab",
                        Title = log.WindowTitle ?? log.ProcessName,
                        Identifier = log.ProcessName,
                        OpenedAt = log.Timestamp,
                    });
                }

                // Close ended sessions via INSERT...ON CONFLICT(id) DO UPDATE SET ended_at
                if (closeSessions.Count > 0)
                    await _store.StoreAppSessionsAsync(closeSessions, stoppingToken);

                // Store new sessions + app_items
                if (newSessions.Count > 0)
                    await _store.StoreAppSessionsAsync(newSessions, stoppingToken);
                if (newItems.Count > 0)
                    await _store.StoreAppItemsAsync(newItems, stoppingToken);

                // Update previous session keys for next cycle
                _previousSessionKeys.Clear();
                foreach (var kvp in currentKeys)
                    _previousSessionKeys[kvp.Key] = kvp.Value;

                if (newSessions.Count > 0 || closeSessions.Count > 0)
                {
                    _logger.LogDebug(
                        "Sessions: {New} new, {Closed} closed, from {Total} processes in {Elapsed}ms",
                        newSessions.Count, closeSessions.Count, allLogs.Count, sw.ElapsedMilliseconds);
                }

                // ─── Collect device hardware info (every 30 cycles ~15 min) ───
                if (_cycleCount % 30 == 0)
                {
                    await CollectDeviceHardwareAsync(stoppingToken);
                }

                // ─── Collect network info (every 10 cycles ~5 min) ───
                if (_cycleCount % 10 == 0)
                {
                    await CollectNetworkInfoAsync(stoppingToken);
                }

                // ─── Scan installed applications (every 30 cycles ~15 min) ───
                if (_cycleCount % 30 == 0)
                {
                    await CollectInstalledApplicationsAsync(stoppingToken);
                }

                // ─── Periodic sync & cleanup ───
                _cycleCount++;
                if (_cycleCount % 10 == 0)
                {
                    await SyncUnsentData(stoppingToken);
                    await StorePermissionStatus(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during log collection cycle");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("LogCollectorService stopped");
    }

    private bool IsInstalledApp(string processName, string? windowTitle)
    {
        if (IsShellProcess(processName))
            return true;

        // Block known non-app system processes
        if (NonAppProcesses.Contains(processName))
            return false;

        // Block chrome crashpad and zygote sub-processes (no window = not a user-facing tab)
        if (processName.StartsWith("chrome", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(windowTitle))
            return false;

        try
        {
            return _appDetector.IsInstalledApplication(processName, GetExecutablePath(processName));
        }
        catch
        {
            return !string.IsNullOrWhiteSpace(windowTitle);
        }
    }

    private static bool IsShellProcess(string processName)
    {
        return processName switch
        {
            "cmd" or "powershell" or "pwsh" or "wsl" or
            "bash" or "zsh" or "sh" or "dash" or "fish" or
            "gnome-terminal" or "konsole" or "alacritty" or "kitty" or
            "iterm2" or "terminal" or "xterm" or "rxvt" or "urxvt" or "st" or
            "tmux" or "screen" => true,
            _ => false
        };
    }

    private static string? GetExecutablePath(string processName)
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(processName);
            if (procs.Length > 0)
            {
                try { return procs[0].MainModule?.FileName; }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private async Task RefreshEmployeeInfo(CancellationToken ct)
    {
        try
        {
            var info = await _store.GetEmployeeInfoAsync(ct);
            _currentEmployeeId = info?.EmployeeId;
            _currentEmployeeName = info?.Name;
            _currentToken = info?.Token;
        }
        catch
        {
            _currentEmployeeId = null;
            _currentEmployeeName = null;
            _currentToken = null;
        }
    }

    // ────────────────────────────────────────────
    // Device Hardware Info Collection
    // Fixes Issue 1: now collects mac_address, storage_devices, gpu_model
    // ────────────────────────────────────────────

    private async Task CollectDeviceHardwareAsync(CancellationToken ct)
    {
        try
        {
            var hostname = Environment.MachineName;
            var osName = RuntimeInformation.OSDescription;
            var osVersion = Environment.OSVersion.VersionString;
            var cpuCores = Environment.ProcessorCount;
            var cpuModel = GetCpuModel();
            var ramTotalMb = GetTotalRamMb();
            var macAddress = GetMacAddress();
            var rawStorageDevices = GetStorageDevices();
            var (gpuModel, gpuVramMb) = GetGpuInfo();

            // Build a fingerprint WITHOUT storage devices (they're relational now — doesn't change often)
            var fingerprint = $"{macAddress}|{hostname}|{osName}|{osVersion}|{cpuModel}|{cpuCores}|{ramTotalMb}|{gpuModel}";

            // Dedup: skip if hardware hasn't changed since last collection
            if (_lastHardwareFingerprint == fingerprint)
            {
                _logger.LogDebug("Hardware unchanged since last collection, skipping");
                return;
            }

            var hw = new DeviceHardwareInfo
            {
                MacAddress = macAddress,
                Hostname = hostname,
                OsName = osName,
                OsVersion = osVersion,
                CpuModel = cpuModel,
                CpuCores = cpuCores,
                RamTotalMb = ramTotalMb,
                GpuModel = gpuModel,
                GpuVramMb = gpuVramMb,
                CollectedAt = DateTime.UtcNow
            };

            await _store.StoreDeviceHardwareInfoAsync(new[] { hw }, ct);

            // Store storage devices as relational rows
            if (rawStorageDevices.Count > 0)
            {
                var storageRows = rawStorageDevices.Select(d => new StorageDevice
                {
                    DeviceHardwareId = hw.Id,
                    DeviceType = d.type,
                    Model = d.model,
                    CapacityMb = d.capacity_mb
                }).ToList();
                await _store.StoreStorageDevicesAsync(storageRows, ct);
            }

            _lastHardwareFingerprint = fingerprint;
            _logger.LogDebug(
                "Collected device hardware info: {Hostname}, {Os}, {Cores} cores, {Ram}MB RAM, MAC={Mac}",
                hostname, osName, cpuCores, ramTotalMb, macAddress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect device hardware info");
        }
    }

    private static string GetMacAddress()
    {
        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    var mac = ni.GetPhysicalAddress().ToString();
                    if (!string.IsNullOrEmpty(mac) && mac.Length >= 6)
                        return mac;
                }
            }
        }
        catch { }
        return "";
    }

    private static List<(string type, string model, long capacity_mb)> GetStorageDevices()
    {
        try
        {
            var devices = new List<(string, string, long)>();
            if (OperatingSystem.IsLinux())
            {
                // Use lsblk -J for reliable structured JSON output
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "lsblk",
                    Arguments = "-J -o NAME,TYPE,SIZE,MODEL,ROTA",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    using var doc = JsonDocument.Parse(output);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("blockdevices", out var blockDevices))
                    {
                        foreach (var dev in blockDevices.EnumerateArray())
                        {
                            var name = dev.GetProperty("name").GetString() ?? "";
                            var devType = dev.GetProperty("type").GetString() ?? "";
                            var sizeStr = dev.GetProperty("size").GetString() ?? "";
                            var model = dev.GetProperty("model").GetString() ?? "";

                            // Skip loop devices and partitions
                            if (devType != "disk") continue;

                            // Determine SSD vs HDD via ROTA flag (1 = HDD, 0 = SSD)
                            var isRotational = dev.TryGetProperty("rota", out var rota) && rota.GetInt32() == 1;
                            var diskType = isRotational ? "HDD" : "SSD";

                            // Parse size string like "119.2G" to MB
                            long capacityMb = ParseSizeToMb(sizeStr);

                            devices.Add((diskType, string.IsNullOrEmpty(model) ? name : model, capacityMb));
                        }
                    }
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                foreach (var drive in System.IO.DriveInfo.GetDrives())
                {
                    if (drive.IsReady && drive.DriveType == System.IO.DriveType.Fixed)
                    {
                        devices.Add(("Fixed", drive.VolumeLabel ?? drive.Name, drive.TotalSize / (1024 * 1024)));
                    }
                }
            }
            return devices;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"GetStorageDevices error: {ex.Message}");
        }
        return new List<(string, string, long)>();
    }

    private static long ParseSizeToMb(string sizeStr)
    {
        try
        {
            if (string.IsNullOrEmpty(sizeStr)) return 0;
            sizeStr = sizeStr.Trim();
            // Patterns: 119.2G, 1.8T, 512M, 1024K
            if (sizeStr.EndsWith("T", StringComparison.OrdinalIgnoreCase))
            {
                var val = double.Parse(sizeStr.TrimEnd('T', 't'), System.Globalization.CultureInfo.InvariantCulture);
                return (long)(val * 1024 * 1024);
            }
            if (sizeStr.EndsWith("G", StringComparison.OrdinalIgnoreCase))
            {
                var val = double.Parse(sizeStr.TrimEnd('G', 'g'), System.Globalization.CultureInfo.InvariantCulture);
                return (long)(val * 1024);
            }
            if (sizeStr.EndsWith("M", StringComparison.OrdinalIgnoreCase))
            {
                var val = double.Parse(sizeStr.TrimEnd('M', 'm'), System.Globalization.CultureInfo.InvariantCulture);
                return (long)val;
            }
            if (sizeStr.EndsWith("K", StringComparison.OrdinalIgnoreCase))
            {
                var val = double.Parse(sizeStr.TrimEnd('K', 'k'), System.Globalization.CultureInfo.InvariantCulture);
                return (long)(val / 1024);
            }
        }
        catch { }
        return 0;
    }

    private static (string model, long vramMb) GetGpuInfo()
    {
        try
        {
            if (OperatingSystem.IsLinux() && System.IO.File.Exists("/proc/driver/nvidia/version"))
            {
                // NVIDIA GPU detected
                var lines = System.IO.File.ReadAllLines("/proc/driver/nvidia/version");
                if (lines.Length > 0)
                {
                    return ($"NVIDIA {lines[0].Trim()}", 0);
                }
            }
            if (OperatingSystem.IsLinux())
            {
                // Try lspci for GPU info
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "lspci",
                    Arguments = "-vnn",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(2000);
                    foreach (var line in output.Split('\n'))
                    {
                        if (line.Contains("VGA") || line.Contains("3D") || line.Contains("Display"))
                        {
                            return (line.Trim(), 0);
                        }
                    }
                }
            }
            if (OperatingSystem.IsWindows())
            {
                // On Windows, return empty as WMI would require System.Management package
                return ("", 0);
            }
        }
        catch { }
        return ("", 0);
    }

    private static string GetCpuModel()
    {
        try
        {
            if (OperatingSystem.IsLinux() && System.IO.File.Exists("/proc/cpuinfo"))
            {
                foreach (var line in System.IO.File.ReadAllLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name"))
                    {
                        var parts = line.Split(':');
                        if (parts.Length > 1)
                            return parts[1].Trim();
                    }
                }
            }
            if (OperatingSystem.IsWindows())
                return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
        }
        catch { }
        return "";
    }

    private static long GetTotalRamMb()
    {
        try
        {
            if (OperatingSystem.IsLinux() && System.IO.File.Exists("/proc/meminfo"))
            {
                foreach (var line in System.IO.File.ReadAllLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:"))
                    {
                        var parts = line.Split(':', StringSplitOptions.TrimEntries);
                        if (parts.Length > 1)
                        {
                            var valStr = parts[1].Split(' ')[0];
                            if (long.TryParse(valStr, out var kb))
                                return kb / 1024;
                        }
                    }
                }
            }
            if (OperatingSystem.IsWindows())
                return Environment.Is64BitProcess ? 16384 : 4096;
        }
        catch { }
        return 0;
    }

    // ────────────────────────────────────────────
    // Network Info Collection
    // Fixes Issue 3: adds public_ip lookup, dedup by IP change,
    // removes mac_address (already in device_hardware_info)
    // ────────────────────────────────────────────

    private async Task CollectNetworkInfoAsync(CancellationToken ct)
    {
        try
        {
            // Get public IP from external service
            var publicIp = await GetPublicIpAsync(ct);

            // Get private IPs from local interfaces
            var hostName = System.Net.Dns.GetHostName();
            var hostEntry = await System.Net.Dns.GetHostEntryAsync(hostName, ct);
            var privateIps = hostEntry.AddressList
                .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(ip => ip.ToString())
                .ToList();

            var primaryPrivateIp = privateIps.FirstOrDefault() ?? "";

            // Dedup: only save if IPs differ from last record
            if (_lastNetworkPublicIp == publicIp && _lastNetworkPrivateIp == primaryPrivateIp)
            {
                _logger.LogDebug("Network info unchanged (public={PublicIp}, private={PrivateIp}), skipping", publicIp, primaryPrivateIp);
                return;
            }

            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                // Skip loopback — not useful for tracking
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;

                if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    var ipProps = ni.GetIPProperties();
                    var unicastAddr = ipProps.UnicastAddresses
                        .FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (unicastAddr == null) continue;

                    var info = new NetworkInfo
                    {
                        PublicIp = publicIp,
                        PrivateIp = unicastAddr.Address.ToString(),
                        NetworkInterfaceName = ni.Name,
                        CollectedAt = DateTime.UtcNow
                    };

                    await _store.StoreNetworkInfoAsync(new[] { info }, ct);
                }
            }

            _lastNetworkPublicIp = publicIp;
            _lastNetworkPrivateIp = primaryPrivateIp;
            _logger.LogDebug("Network info saved (public={PublicIp}, private={PrivateIp})", publicIp, primaryPrivateIp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect network info");
        }
    }

    private async Task<string> GetPublicIpAsync(CancellationToken ct)
    {
        try
        {
            // Try multiple public IP services for reliability
            string[] services = [
                "https://api.ipify.org",
                "https://icanhazip.com",
                "https://checkip.amazonaws.com"
            ];

            foreach (var service in services)
            {
                try
                {
                    var response = await _httpClient.GetStringAsync(service, ct);
                    var ip = response.Trim();
                    if (!string.IsNullOrEmpty(ip) && System.Net.IPAddress.TryParse(ip, out _))
                        return ip;
                }
                catch { }
            }
        }
        catch { }
        return "";
    }

    // ────────────────────────────────────────────
    // Installed Application Scanner
    // Fixes Issue 2: scans actual installed apps from OS registry/db,
    // NOT from running processes. Collects metadata (version, publisher, etc.)
    // ────────────────────────────────────────────

    private async Task CollectInstalledApplicationsAsync(CancellationToken ct)
    {
        try
        {
            // Force recheck to pick up newly installed apps
            _appDetector.ForceRecheck();

            var apps = _appDetector.GetAllInstalledApplications();
            if (apps.Count == 0)
            {
                _logger.LogDebug("No installed applications detected");
                return;
            }

            await _store.StoreInstalledApplicationsAsync(apps, ct);
            _logger.LogDebug("Scanned {Count} installed applications", apps.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scan installed applications");
        }
    }

    // ────────────────────────────────────────────
    // Session Event Recording
    // ────────────────────────────────────────────

    private async Task RecordSessionEventAsync(string eventType, CancellationToken stoppingToken)
    {
        try
        {
            var evt = new SessionEvent
            {
                EventType = eventType,
                OsUsername = Environment.UserName,
                EventAt = DateTime.UtcNow
            };
            var token = stoppingToken == CancellationToken.None ? CancellationToken.None : stoppingToken;
            await _store.StoreSessionEventsAsync(new[] { evt }, token);
            _logger.LogDebug("Recorded session event: {EventType} for user {User}", eventType, Environment.UserName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record session event: {EventType}", eventType);
        }
    }

    // ────────────────────────────────────────────
    // Sync Engine — sends unsent data for all tables
    // Uses app_items instead of old child tables
    // ────────────────────────────────────────────

    private const int BATCH_SIZE = 500;

    private async Task SyncUnsentData(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_currentEmployeeId) || string.IsNullOrEmpty(_currentToken))
            return;

        var serverUrl = _config.ServerUrl ?? "http://localhost:8080";

        // Phase 1 tables (no FK dependencies)
        await SyncTableBatch<DeviceHardwareInfo>(
            () => _store.GetUnsentDeviceHardwareInfoAsync(BATCH_SIZE, ct),
            entries => SerializeAndSend(
                serverUrl, "/api/v1/device-hardware/sync",
                entries, e => new
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
                    collectedAt = e.CollectedAt.ToString("O"),
                },
                ids => _store.MarkDeviceHardwareInfoSentAsync(ids, ct),
                ct),
            ct);

        // Storage devices (relational child of device_hardware_info)
        await SyncTableBatch<StorageDevice>(
            () => _store.GetUnsentStorageDevicesAsync(BATCH_SIZE, ct),
            entries => SerializeAndSend(
                serverUrl, "/api/v1/storage-devices/sync",
                entries, e => new
                {
                    id = e.Id,
                    deviceHardwareId = e.DeviceHardwareId,
                    deviceType = e.DeviceType,
                    model = e.Model,
                    capacityMb = e.CapacityMb,
                },
                ids => _store.MarkStorageDevicesSentAsync(ids, ct),
                ct),
            ct);

        await SyncTableBatch<InstalledApplication>(
            () => _store.GetUnsentInstalledApplicationsAsync(BATCH_SIZE, ct),
            entries => SerializeAndSend(
                serverUrl, "/api/v1/installed-apps/sync",
                entries, e => new
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
                },
                ids => _store.MarkInstalledApplicationsSentAsync(ids, ct),
                ct),
            ct);

        await SyncTableBatch<NetworkInfo>(
            () => _store.GetUnsentNetworkInfoAsync(BATCH_SIZE, ct),
            entries => SerializeAndSend(
                serverUrl, "/api/v1/network-info/sync",
                entries, e => new
                {
                    id = e.Id,
                    publicIp = e.PublicIp,
                    privateIp = e.PrivateIp,
                    networkInterfaceName = e.NetworkInterfaceName,
                    collectedAt = e.CollectedAt.ToString("O"),
                },
                ids => _store.MarkNetworkInfoSentAsync(ids, ct),
                ct),
            ct);

        await SyncTableBatch<SessionEvent>(
            () => _store.GetUnsentSessionEventsAsync(BATCH_SIZE, ct),
            entries => SerializeAndSend(
                serverUrl, "/api/v1/session-events/sync",
                entries, e => new
                {
                    id = e.Id,
                    eventType = e.EventType,
                    osUsername = e.OsUsername,
                    eventAt = e.EventAt.ToString("O"),
                },
                ids => _store.MarkSessionEventsSentAsync(ids, ct),
                ct),
            ct);

        // App sessions first (parents)
        await SyncAppSessions(serverUrl, ct);

        // App items (generic children — replaces browser_contexts, file_explorer_contexts, urls, url_visits)
        await SyncTableBatch<AppItem>(
            () => _store.GetUnsentAppItemsAsync(BATCH_SIZE, ct),
            entries => SerializeAndSend(
                serverUrl, "/api/v1/app-items/sync",
                entries, e => new
                {
                    id = e.Id,
                    appSessionId = e.AppSessionId,
                    parentItemId = e.ParentItemId,
                    itemType = e.ItemType,
                    title = e.Title,
                    identifier = e.Identifier,
                    openedAt = e.OpenedAt.ToString("O"),
                    closedAt = e.ClosedAt?.ToString("O"),
                },
                ids => _store.MarkAppItemsSentAsync(ids, ct),
                ct),
            ct);
    }

    private async Task SyncAppSessions(string serverUrl, CancellationToken ct)
    {
        var unsent = await _store.GetUnsentAppSessionsAsync(BATCH_SIZE, ct);
        if (unsent.Count == 0) return;

        var payload = new
        {
            employeeId = _currentEmployeeId,
            token = _currentToken,
            entries = unsent.Select(e => new
            {
                id = e.Id,
                processName = e.ProcessName,
                appDisplayName = e.AppDisplayName,
                startedAt = e.StartedAt.ToString("O"),
                endedAt = e.EndedAt?.ToString("O"),
                machineId = e.MachineId,
                employeeId = _currentEmployeeId,
                employeeName = _currentEmployeeName,
                sessionId = e.SessionId,
                platform = e.Platform,
            }).ToList()
        };

        try
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{serverUrl}/api/v1/app-sessions/sync", content, ct);

            if (response.IsSuccessStatusCode)
            {
                var syncedIds = unsent.Select(e => e.Id).ToList();
                await _store.MarkAppSessionsSentAsync(syncedIds, ct);
                _logger.LogDebug("Synced {Count} app sessions", unsent.Count);
            }
            else if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
            {
                _logger.LogWarning("Auth failed (status {Status}) during app session sync", (int)response.StatusCode);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("App session sync failed (status {Status}): {Body}", (int)response.StatusCode, body);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "App session sync failed (server unreachable)");
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("App session sync timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync app sessions");
        }
    }

    /// <summary>
    /// Generic sync helper: fetches unsent rows, sends in batches of up to 500,
    /// marks sent after each successful batch, stops on failure, retries next cycle.
    /// </summary>
    private async Task SyncTableBatch<T>(
        Func<Task<IReadOnlyList<T>>> fetchFn,
        Func<IReadOnlyList<T>, Task<bool>> sendFn,
        CancellationToken ct)
    {
        try
        {
            var entries = await fetchFn();
            if (entries.Count == 0) return;

            for (int i = 0; i < entries.Count; i += BATCH_SIZE)
            {
                ct.ThrowIfCancellationRequested();
                var batch = entries.Skip(i).Take(BATCH_SIZE).ToList();

                var success = await sendFn(batch);
                if (!success)
                {
                    _logger.LogDebug("Sync batch failed, retrying next cycle");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error syncing table batch");
        }
    }

    private async Task<bool> SerializeAndSend<T>(
        string serverUrl, string endpoint,
        IReadOnlyList<T> entries,
        Func<T, object> mapper,
        Func<IReadOnlyList<string>, Task> markSentFn,
        CancellationToken ct)
    {
        var payload = new
        {
            employeeId = _currentEmployeeId,
            token = _currentToken,
            entries = entries.Select(mapper).ToList()
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.PostAsync($"{serverUrl}{endpoint}", content, ct);

            if (response.IsSuccessStatusCode)
            {
                var ids = entries.Select(e =>
                {
                    var prop = typeof(T).GetProperty("Id");
                    return prop?.GetValue(e)?.ToString() ?? string.Empty;
                }).Where(id => !string.IsNullOrEmpty(id)).ToList();

                await markSentFn(ids);
                _logger.LogDebug("Synced {Count} rows to {Endpoint}", ids.Count, endpoint);
                return true;
            }
            else if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
            {
                _logger.LogWarning("Auth failed (status {Status}) for {Endpoint}", (int)response.StatusCode, endpoint);
                return false;
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Sync failed for {Endpoint} (status {Status}): {Body}", endpoint, (int)response.StatusCode, body);
                return false;
            }
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

    private async Task StorePermissionStatus(CancellationToken ct)
    {
        try
        {
            var sessionType = "unknown";
            if (OperatingSystem.IsLinux())
            {
                sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";
                var perms = client.Platform.Linux.ProcessCollector.GetPermissionStatus();
                await _store.SetPermissionStatusAsync(perms, sessionType, ct);
            }
            else if (OperatingSystem.IsWindows())
            {
                sessionType = "windows";
                var perms = client.Platform.Windows.ProcessCollector.GetPermissionStatus();
                await _store.SetPermissionStatusAsync(perms, sessionType, ct);
            }
            else if (OperatingSystem.IsMacOS())
            {
                sessionType = "macos";
                var perms = client.Platform.MacOS.ProcessCollector.GetPermissionStatus();
                await _store.SetPermissionStatusAsync(perms, sessionType, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store permission status");
        }
    }
}
