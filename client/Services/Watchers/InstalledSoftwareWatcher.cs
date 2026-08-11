using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services.Watchers;

/// <summary>
/// Near-real-time install/uninstall detection. Watches the OS locations where software
/// installation artifacts appear/disappear (.desktop files, dpkg state, Start Menu
/// shortcuts, /Applications bundles) and triggers an immediate <see cref="LogCollectorService"/>
/// inventory rescan when anything changes — so an install or uninstall is recorded within
/// seconds WITHOUT any GUI interaction and WITHOUT any minute-based polling (user rule
/// 2026-08-10: no periodic inventory scan — the watcher is the ONLY runtime trigger; the
/// one-time startup scan at boot is the only other scan).
///
/// Why file watching and not polling: install/uninstall is a discrete filesystem event
/// (.desktop created/deleted, dpkg status rewritten, .lnk created/deleted, .app moved into
/// /Applications) — a watcher reacts instantly, a poll can only be as fast as its interval.
/// Events are debounced (an apt transaction rewrites dpkg status several times) and
/// coalesced (a rescan already running is not stacked).
/// </summary>
public sealed class InstalledSoftwareWatcher : BackgroundService
{
    private readonly LogCollectorService _logCollector;
    private readonly ILogger<InstalledSoftwareWatcher> _logger;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _gate = new();
    private DateTime _firstUnscannedChangeAt = DateTime.MinValue;
    private DateTime _lastChangeAt = DateTime.MinValue;
    private DateTime _lastScanStartedAt = DateTime.MinValue;
    private bool _rescanRunning;

    // Debounce: an apt/dpkg transaction fires many status rewrites in quick succession;
    // wait for the burst to settle before scanning. Anchored to the FIRST unscanned change
    // (not the latest): if events kept arriving faster than the debounce window, anchoring
    // to the latest would starve the rescan forever — a constant event source would keep
    // pushing the deadline out and no scan would ever start.
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(1500);
    // Min gap between rescans: even with debounce, a flood of unrelated events (e.g. a
    // watched tree being touched by something else) must not spin the collectors.
    private static readonly TimeSpan MinScanGap = TimeSpan.FromSeconds(5);

    public InstalledSoftwareWatcher(LogCollectorService logCollector, ILogger<InstalledSoftwareWatcher> logger)
    {
        _logCollector = logCollector;
        _logger = logger;
    }

    /// <summary>
    /// The exact trees where install/uninstall leaves filesystem evidence, per platform.
    /// Deliberately structural (OS-provided paths) — no product-name lists.
    /// </summary>
    private static string[] GetWatchDirectories()
    {
        var dirs = new List<string>();

        if (OperatingSystem.IsLinux())
        {
            // .desktop entries — the GUI-app install signal (user + system + flatpak + snap exports).
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "applications"));
            dirs.Add("/usr/share/applications");
            dirs.Add("/usr/local/share/applications");
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "flatpak", "exports", "share", "applications"));
            dirs.Add("/var/lib/flatpak/exports/share/applications");
            // dpkg status — the apt/dpkg package signal (rewritten on every install/uninstall).
            dirs.Add("/var/lib/dpkg");
        }
        else if (OperatingSystem.IsWindows())
        {
            // Start Menu shortcuts — the Windows GUI-app install signal (user + common).
            dirs.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"));
            dirs.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"));
            // Program Files root changes when an installer adds/removes a top-level app folder.
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pfX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(pf)) dirs.Add(pf);
            if (!string.IsNullOrWhiteSpace(pfX86) && !string.Equals(pf, pfX86, StringComparison.OrdinalIgnoreCase))
                dirs.Add(pfX86);
        }
        else if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/Applications");
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"));
        }

        return dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watched = 0;
        foreach (var dir in GetWatchDirectories())
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                // LastWrite is only needed where state is REWRITTEN in place rather than
                // recreated — /var/lib/dpkg status. Applying it to every tree (e.g. Program
                // Files, where apps/AV write files in place constantly) would turn every
                // write into a rescan trigger.
                var needsInPlaceRewrite = dir.IndexOf("dpkg", StringComparison.OrdinalIgnoreCase) >= 0;
                // Program Files is watched top-level only: install/uninstall creates/removes
                // a top-level app folder there, and the recursive Start Menu watcher is the
                // primary Windows signal anyway — recursion would only add noise.
                var recurse = !(OperatingSystem.IsWindows()
                    && dir.IndexOf("Program Files", StringComparison.OrdinalIgnoreCase) >= 0);
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = recurse,
                    // FileName/DirectoryName: install/uninstall creates/deletes entries.
                    NotifyFilter = needsInPlaceRewrite
                        ? NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                        : NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                watcher.Created += OnFsChange;
                watcher.Deleted += OnFsChange;
                watcher.Renamed += OnFsChange;
                watcher.Changed += OnFsChange;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                watched++;
                _logger.LogDebug("Installed-software watcher attached to {Dir}", dir);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not attach install watcher to {Dir} (may need elevated rights)", dir);
            }
        }
        _logger.LogInformation("Installed-software watcher watching {Count} location(s)", watched);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
                await MaybeRescanAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        finally
        {
            foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
            _watchers.Clear();
        }
    }

    private void OnFsChange(object sender, FileSystemEventArgs e)
    {
        // Ignore our own DB dirs (never watched, but be safe) and editor temp noise.
        if (e.FullPath.Contains("alpha-ai-tracker", StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate)
        {
            _lastChangeAt = DateTime.UtcNow;
            // Remember the FIRST change of the burst — the debounce anchor. Re-armed only
            // while no rescan is consuming this burst (see MaybeRescanAsync).
            if (_firstUnscannedChangeAt == DateTime.MinValue)
                _firstUnscannedChangeAt = _lastChangeAt;
        }
    }

    private async Task MaybeRescanAsync(CancellationToken ct)
    {
        DateTime firstChange;
        lock (_gate) { firstChange = _firstUnscannedChangeAt; }

        if (firstChange == DateTime.MinValue) return;                        // nothing changed yet
        if (DateTime.UtcNow - firstChange < DebounceDelay) return;           // burst settling (from FIRST event — a constant event stream can't starve this)
        if (DateTime.UtcNow - _lastScanStartedAt < MinScanGap) return;       // avoid event floods

        bool run;
        lock (_gate)
        {
            if (_rescanRunning) return;   // a rescan is already in flight — coalesce
            _rescanRunning = true;
            // This burst is now being scanned. Any change arriving DURING the rescan
            // re-arms _firstUnscannedChangeAt via OnFsChange, so nothing is ever lost.
            _firstUnscannedChangeAt = DateTime.MinValue;
            run = true;
        }
        if (!run) return;

        // Capture the scan-start time BEFORE running, so the finally can consume only the
        // change that triggered THIS rescan (see the race-condition comment below).
        _lastScanStartedAt = DateTime.UtcNow;
        var scannedAt = _lastScanStartedAt;
        try
        {
            _logger.LogInformation("Software inventory change detected — rescanning (background)");
            // Defense-in-depth cap on the STORE/lifecycle phase (the detector phase is not
            // cancellation-aware, but each CLI probe inside it is hard time-boxed by
            // ProcessFilter.RunProbe, so the whole scan is bounded either way). This stops
            // a pathological store from holding the in-flight flag and freezing all later
            // install/uninstall events — the bug that made the DB look stale until restart.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));
            await _logCollector.RescanInventoryAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background inventory rescan failed");
        }
        finally
        {
            lock (_gate)
            {
                // Consume the change we just scanned — but only if no NEWER change arrived
                // while the rescan was running (otherwise that newer change would be lost
                // and its install/uninstall never recorded until the next periodic scan).
                if (_lastChangeAt <= scannedAt) _lastChangeAt = DateTime.MinValue;
                _rescanRunning = false;
            }
        }
    }
}
