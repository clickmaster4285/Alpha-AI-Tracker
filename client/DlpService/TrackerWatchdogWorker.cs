namespace AlphaAITracker.Service;

/// <summary>
/// Keeps the user-session tracker alive after Task Manager "End Task".
/// LocalSystem cannot make a process truly unkillable for a local admin, but
/// restarting within seconds restores tracking. Skips when teardown.ok exists
/// (authorized uninstall) or device.auth is missing (logged out / wiped).
/// </summary>
internal sealed class TrackerWatchdogWorker : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly ILogger<TrackerWatchdogWorker> _log;
    private DateTimeOffset _lastLaunchAttempt = DateTimeOffset.MinValue;

    public TrackerWatchdogWorker(DlpConfig cfg, ILogger<TrackerWatchdogWorker> log)
    {
        _cfg = cfg;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.WatchdogEnabled)
        {
            _log.LogInformation("Tracker watchdog parked (ALPHA_DLP_WATCHDOG=false)");
            return;
        }

        // Park briefly so boot/login can start the tracker first.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var poll = TimeSpan.FromSeconds(Math.Clamp(_cfg.WatchdogPollSec, 3, 60));
        var exeName = ServiceIdentity.ExecutableName;
        _log.LogInformation(
            "Tracker watchdog active (process={Name}, poll={Poll}s)",
            exeName, poll.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick(exeName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Watchdog tick failed");
            }

            try { await Task.Delay(poll, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick(string exeName)
    {
        // Authorized uninstall in progress / done — do not fight the operator.
        if (File.Exists(TeardownGate.PathIn(_cfg.DataDir)))
            return;

        // No device credentials ⇒ machine is logged out or wiped; stay parked.
        if (!DeviceCredentialStore.TryLoad(out _, out var tok) || string.IsNullOrWhiteSpace(tok))
            return;

        if (UserSessionProcessLauncher.IsProcessRunning(exeName))
            return;

        // Cooldown so a broken launch path does not spam CreateProcessAsUser.
        if (DateTimeOffset.UtcNow - _lastLaunchAttempt < TimeSpan.FromSeconds(15))
            return;

        var path = ResolveClientExe();
        if (path is null)
        {
            _log.LogDebug("Watchdog: client.exe not found under Program Files");
            return;
        }

        _lastLaunchAttempt = DateTimeOffset.UtcNow;
        _log.LogWarning(
            "Watchdog: {Name} not running while device.auth exists — relaunching {Path}",
            exeName, path);
        UserSessionProcessLauncher.TryLaunch(path, "--background", _log);
    }

    private static string? ResolveClientExe()
    {
        var name = ServiceIdentity.ExecutableName + ".exe";
        var folder = ServiceIdentity.DisplayName;
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                folder, name),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                folder, name),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        // Dev / sideload: same directory as the DLP service binary (publish layout).
        var beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside)) return beside;

        var parent = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrEmpty(parent))
        {
            var sibling = Path.Combine(parent, name);
            if (File.Exists(sibling)) return sibling;
        }

        return null;
    }
}
