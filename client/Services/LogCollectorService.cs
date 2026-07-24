using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

public class LogCollectorService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IActivityCollector _collector;
    private readonly ILogStore _store;
    private readonly ILogger<LogCollectorService> _logger;
    private readonly HttpClient _httpClient;
    private readonly IInstalledAppDetector _appDetector;
    private readonly IShellCommandCollector _shellCollector;
    private int _cycleCount;
    private string? _currentEmployeeId;  // Loosely synchronized — read in bg loop, written under _trackingLock
    private string? _currentEmployeeName; // Same as above
    private string? _currentToken;         // Same as above
    private bool _trackingEnabled;
    private readonly object _trackingLock = new();

    public LogCollectorService(
        AppConfig config,
        IActivityCollector collector,
        ILogStore store,
        ILogger<LogCollectorService> logger,
        HttpClient httpClient,
        IInstalledAppDetector appDetector,
        IShellCommandCollector shellCollector)
    {
        _config = config;
        _collector = collector;
        _store = store;
        _logger = logger;
        _httpClient = httpClient;
        _appDetector = appDetector;
        _shellCollector = shellCollector;
    }

    /// <summary>
    /// Start tracking — called after successful employee login.
    /// </summary>
    public void StartTracking()
    {
        lock (_trackingLock)
        {
            _trackingEnabled = true;
            _cycleCount = 0; // Reset cycle counter so sync/cleanup timing starts fresh
            _logger.LogInformation("Tracking started");
        }
    }

    /// <summary>
    /// Stop tracking — called on employee disconnect/logout.
    /// </summary>
    public void StopTracking()
    {
        lock (_trackingLock)
        {
            _trackingEnabled = false;
            _currentEmployeeId = null;
            _currentEmployeeName = null;
            _currentToken = null;
            _logger.LogInformation("Tracking stopped");
        }
    }

    /// <summary>
    /// Update the employee info used for tagging logs.
    /// </summary>
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // ─── Only collect if tracking is enabled (user is logged in) ───
                if (!_trackingEnabled)
                {
                    // While idle, just check every 5 seconds if tracking was enabled
                    await Task.Delay(5000, stoppingToken);
                    continue;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Refresh employee info periodically
                if (_cycleCount % 6 == 0)
                {
                    await RefreshEmployeeInfo(stoppingToken);
                }

                // ─── Phase 1: Collect installed application activity ───
                var allLogs = await _collector.CollectAsync(stoppingToken);

                // Filter to INSTALLED APPLICATIONS only (not random scripts/binaries)
                var installedAppLogs = allLogs
                    .Where(l => IsInstalledApp(l.ProcessName, l.WindowTitle))
                    .ToList();

                // Only keep logs that have a meaningful window title
                var filteredLogs = installedAppLogs
                    .Where(l => !string.IsNullOrWhiteSpace(l.WindowTitle))
                    .ToList();

                // Attach current employee info
                if (filteredLogs.Count > 0)
                {
                    foreach (var log in filteredLogs)
                    {
                        log.EmployeeId = _currentEmployeeId;
                        log.EmployeeName = _currentEmployeeName;
                    }

                    await _store.StoreAsync(filteredLogs, stoppingToken);
                    var activityTotal = await _store.GetCountAsync(stoppingToken);
                    _logger.LogDebug(
                        "Collected {Count} app logs from {TotalProcesses} total (total in db: {Total}) in {Elapsed}ms",
                        filteredLogs.Count, allLogs.Count, activityTotal, sw.ElapsedMilliseconds);
                }

                // ─── Phase 2: Collect shell/terminal commands ───
                try
                {
                    var shellCommands = await _shellCollector.CollectNewCommandsAsync(stoppingToken);
                    if (shellCommands.Count > 0)
                    {
                        foreach (var cmd in shellCommands)
                        {
                            cmd.EmployeeId = _currentEmployeeId;
                            cmd.EmployeeName = _currentEmployeeName;
                            cmd.MachineId = _config.ClientId;
                        }

                        await _store.StoreShellCommandsAsync(shellCommands, stoppingToken);
                        var shellTotal = await _store.GetShellCommandCountAsync(stoppingToken);
                        _logger.LogDebug(
                            "Collected {Count} shell commands (total in db: {Total})",
                            shellCommands.Count, shellTotal);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to collect shell commands");
                }

                // ─── Phase 3: Periodic sync & cleanup ───
                _cycleCount++;
                if (_cycleCount % 10 == 0)
                {
                    await SyncUnsentLogs(stoppingToken);
                    await SyncUnsentShellCommands(stoppingToken);
                    await StorePermissionStatus(stoppingToken);
                }
                if (_cycleCount % 120 == 0)
                {
                    await CleanupSyncedLogs(stoppingToken);
                    await _store.CleanupShellCommandsSyncedAsync(TimeSpan.FromHours(24), stoppingToken);
                    await _store.CleanupAsync(TimeSpan.FromDays(30), stoppingToken);
                    _logger.LogDebug("Ran periodic cleanup");
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

    /// <summary>
    /// Check if a process is a known installed application.
    /// </summary>
    private bool IsInstalledApp(string processName, string? windowTitle)
    {
        // Always track shell/terminal processes for command collection
        if (IsShellProcess(processName))
            return true;

        // Check via InstalledAppDetector
        try
        {
            return _appDetector.IsInstalledApplication(processName, GetExecutablePath(processName));
        }
        catch
        {
            // Fallback: include processes with window titles (user is interacting with it)
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

    private async Task SyncUnsentLogs(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_currentEmployeeId) || string.IsNullOrEmpty(_currentToken))
            return;

        try
        {
            var unsentLogs = await _store.GetUnsentAsync(100, ct);
            if (unsentLogs.Count == 0) return;

            var serverUrl = _config.ServerUrl ?? "http://localhost:8080";

            var payload = new
            {
                employeeId = _currentEmployeeId,
                token = _currentToken,
                logs = unsentLogs.Select(l => new
                {
                    id = l.Id,
                    machineId = l.MachineId,
                    timestamp = l.Timestamp.ToString("O"),
                    processName = l.ProcessName,
                    windowTitle = l.WindowTitle,
                    processId = l.ProcessId,
                    cpuPercent = l.CpuPercent,
                    memoryBytes = l.MemoryBytes,
                    isForeground = l.IsForeground,
                    userName = l.UserName,
                    platform = l.Platform,
                    sessionId = l.SessionId,
                    employeeId = _currentEmployeeId,
                    employeeName = _currentEmployeeName
                }).ToList()
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{serverUrl}/api/v1/activity-logs/sync", content, ct);

            if (response.IsSuccessStatusCode)
            {
                var syncedIds = unsentLogs.Select(l => l.Id).ToList();
                await _store.MarkSentAsync(syncedIds, ct);
                _logger.LogDebug("Synced {Count} logs to server", unsentLogs.Count);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Sync failed (status {Status}): {Body}", (int)response.StatusCode, body);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Sync failed (server unreachable)");
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Sync timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync logs");
        }
    }

    private async Task SyncUnsentShellCommands(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_currentEmployeeId) || string.IsNullOrEmpty(_currentToken))
            return;

        try
        {
            var unsent = await _store.GetUnsentShellCommandsAsync(100, ct);
            if (unsent.Count == 0) return;

            var serverUrl = _config.ServerUrl ?? "http://localhost:8080";

            var payload = new
            {
                employeeId = _currentEmployeeId,
                token = _currentToken,
                commands = unsent.Select(c => new
                {
                    id = c.Id,
                    machineId = c.MachineId,
                    timestamp = c.Timestamp.ToString("O"),
                    shellName = c.ShellName,
                    shellPid = c.ShellPid,
                    command = c.Command,
                    workingDirectory = c.WorkingDirectory,
                    exitCode = c.ExitCode,
                    userName = c.UserName,
                    platform = c.Platform,
                    sessionId = c.SessionId,
                    employeeId = _currentEmployeeId,
                    employeeName = _currentEmployeeName
                }).ToList()
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{serverUrl}/api/v1/shell-commands/sync", content, ct);

            if (response.IsSuccessStatusCode)
            {
                var syncedIds = unsent.Select(c => c.Id).ToList();
                await _store.MarkShellCommandsSentAsync(syncedIds, ct);
                _logger.LogDebug("Synced {Count} shell commands to server", unsent.Count);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Shell sync failed (status {Status}): {Body}", (int)response.StatusCode, body);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Shell sync failed (server unreachable)");
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Shell sync timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync shell commands");
        }
    }

    private async Task CleanupSyncedLogs(CancellationToken ct)
    {
        try
        {
            await _store.CleanupSyncedAsync(TimeSpan.FromHours(24), ct);
            _logger.LogDebug("Cleaned up synced logs older than 24h");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to cleanup synced logs");
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
