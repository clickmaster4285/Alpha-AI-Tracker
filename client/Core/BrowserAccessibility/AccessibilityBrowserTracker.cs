using System.Text.Json;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Core.BrowserAccessibility;

/// <summary>
/// Option B — accessibility-based browser journey tracker.
///
/// Polls the OS accessibility tree (AT-SPI / UIA / AX) and writes the employee's REAL
/// browser journeys into SQLite as <c>app_sessions</c> + <c>app_items</c>:
///   - one session per browser window (keyed by the a11y object identity),
///   - a <c>browser_tab</c> root item carrying the active tab's exact URL (from the
///     address bar) + title,
///   - <c>browser_navigation</c> child items whenever the address-bar URL changes,
///   - downloads in the Downloads folder appended to the most recent browser session.
///
/// Works on every browser and every Chrome version — no debugger, no extension, no
/// catalog dependency. Incognito windows are flagged; their URL is only stored when
/// <see cref="AppConfig.BrowserCaptureIncognito"/> is enabled (default: off, legal-safe).
/// </summary>
public sealed class AccessibilityBrowserTracker : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IAccessibilityBrowserReader _reader;
    private readonly ILogStore _store;
    private readonly ILogger<AccessibilityBrowserTracker> _logger;

    private sealed class TrackedWindow
    {
        public required string SessionId { get; init; }
        // Mutable: each page navigation closes the current browser_tab root and opens
        // a new one, so the tracker must re-point at the latest root item.
        public string RootItemId { get; set; } = string.Empty;
        public required string JourneyId { get; init; }
        public required int ProcessId { get; init; }
        public string? LastUrl { get; set; }
        public string LastTitle { get; set; } = string.Empty;
        public bool IsIncognito { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        /// <summary>Title seen only briefly (badge/timer flicker) — rotated only once stable.</summary>
        public string? PendingTitle { get; set; }
        public DateTime PendingSinceUtc { get; set; }
        public DateTime? LastSeen { get; set; }
    }

    private const int MissingPollsToClose = 3;
    /// <summary>Minimum dwell before a title-only change rotates the browser_tab record
    /// (badges/timers/player-state titles change often and would fragment the journey).</summary>
    private static readonly TimeSpan MinTabRotationInterval = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, TrackedWindow> _tracked = new();
    private readonly List<FileSystemWatcher> _downloadWatchers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _stopping;

    private string? _employeeId;
    private string? _employeeName;
    private DateTime _lastEmployeeRefresh = DateTime.MinValue;

    public AccessibilityBrowserTracker(
        AppConfig config,
        IAccessibilityBrowserReader reader,
        ILogStore store,
        ILogger<AccessibilityBrowserTracker> logger)
    {
        _config = config;
        _reader = reader;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Accessibility browser tracker starting (platform={Platform}, poll={Poll}s, idle-close={Idle}min, capture-incognito={Incognito})",
            _reader.Platform, _config.BrowserAccessibilityPollSec, _config.BrowserJourneyIdleMinutes, _config.BrowserCaptureIncognito);

        await _store.InitializeAsync(stoppingToken);
        await RefreshEmployeeInfoAsync(stoppingToken);
        StartDownloadWatchers();
        _stopping = false;

        var interval = TimeSpan.FromSeconds(Math.Max(2, _config.BrowserAccessibilityPollSec));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Accessibility browser tracker poll failed");
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

        // Graceful shutdown: close whatever is still open so no orphan sessions remain.
        _stopping = true;
        try
        {
            foreach (var watcher in _downloadWatchers)
            {
                try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
            }
            _downloadWatchers.Clear();

            await CloseAllAsync(DateTime.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to close browser sessions on shutdown");
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        if ((now - _lastEmployeeRefresh).TotalMinutes >= 10)
            await RefreshEmployeeInfoAsync(ct);

        var snapshots = await _reader.ReadAsync(ct);

        var present = new HashSet<string>();
        foreach (var snap in snapshots)
        {
            if (string.IsNullOrWhiteSpace(snap.WindowTitle) && string.IsNullOrWhiteSpace(snap.Url))
                continue;
            present.Add(snap.WindowKey);
        }

        await _gate.WaitAsync(ct);
        try
        {
            foreach (var snap in snapshots)
            {
                var key = ResolveWindowKey(snap);
                if (!_tracked.TryGetValue(key, out var tw))
                {
                    await OpenWindowAsync(key, snap, ct);
                    continue;
                }

                tw.LastSeen = now;
                await UpdateWindowAsync(tw, snap, ct);
            }

            // Close windows that vanished (browser closed). Give a few polls for
            // transient a11y-tree misses before declaring the window gone.
            foreach (var kv in _tracked.ToList())
            {
                var tw = kv.Value;
                if (present.Contains(kv.Key)) continue;
                if (tw.LastSeen is { } lastSeen &&
                    (now - lastSeen).TotalSeconds >= intervalSeconds * MissingPollsToClose)
                {
                    _logger.LogInformation("Browser window closed (no longer visible): {Title}", tw.LastTitle);
                    await CloseWindowAsync(kv.Key, tw, now, ct);
                }
            }

            // Idle close: no URL/title activity for BrowserJourneyIdleMinutes.
            var idleLimit = TimeSpan.FromMinutes(Math.Max(1, _config.BrowserJourneyIdleMinutes));
            foreach (var kv in _tracked.ToList())
            {
                if ((now - kv.Value.LastActivity) >= idleLimit)
                {
                    _logger.LogInformation("Browser journey closed after {Min}min idle: {Title}",
                        idleLimit.TotalMinutes, kv.Value.LastTitle);
                    await CloseWindowAsync(kv.Key, kv.Value, now, ct);
                }
            }

            await _store.SetStatusAsync("browser_tracking_method", "accessibility", ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// macOS exposes no stable window id — when a brand-new key arrives whose pid matches
    /// exactly one tracked window, treat it as the same window (key churn) instead of
    /// opening a duplicate session.
    /// </summary>
    private string ResolveWindowKey(AccessibilitySnapshot snap)
    {
        if (_tracked.ContainsKey(snap.WindowKey)) return snap.WindowKey;

        if (snap.ProcessId > 0)
        {
            var matches = _tracked.Where(kv => kv.Value.ProcessId == snap.ProcessId).ToList();
            if (matches.Count == 1)
            {
                var oldKey = matches[0].Key;
                var tw = matches[0].Value;
                _tracked.Remove(oldKey);
                _tracked[snap.WindowKey] = tw;
                _logger.LogTrace("Re-keyed window {Old} → {New} (pid {Pid})", oldKey, snap.WindowKey, snap.ProcessId);
                return snap.WindowKey;
            }
        }
        return snap.WindowKey;
    }

    private async Task OpenWindowAsync(string key, AccessibilitySnapshot snap, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var now = snap.CapturedAt;
        var url = ResolveStoredUrl(snap);

        string? installedAppId = null;
        string? displayName = null;
        try
        {
            var app = await _store.GetInstalledAppByBinaryNameFuzzyAsync(snap.ProcessName, ct)
                      ?? await _store.GetInstalledAppByBinaryNameAsync(snap.ProcessName, ct);
            installedAppId = app?.Id;
            displayName = app?.AppName;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Installed-app lookup failed for {Proc}", snap.ProcessName);
        }

        var session = new AppSession
        {
            Id = sessionId,
            ProcessName = snap.ProcessName,
            AppDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? StripBrowserSuffix(snap.WindowTitle)
                : displayName,
            StartedAt = now,
            MachineId = _config.ClientId,
            EmployeeId = _employeeId,
            EmployeeName = _employeeName,
            SessionId = SessionInfo.SessionId,
            Platform = _reader.Platform,
            InstalledAppId = installedAppId,
            ProcessId = snap.ProcessId,
            ContextLabel = snap.WindowTitle,
        };

        var rootItem = new AppItem
        {
            AppSessionId = sessionId,
            ItemType = "browser_tab",
            Title = string.IsNullOrWhiteSpace(snap.WindowTitle) ? "Browser Window" : snap.WindowTitle,
            Identifier = key,
            Url = url,
            Domain = BrowserAccessibilityHelpers.ExtractDomain(url),
            OpenedAt = now,
            ProcessId = snap.ProcessId,
            ObjectType = "Tab",
            Action = "open",
            JourneyId = sessionId,
            Sequence = 1,
            CurrentPath = url,
            WindowId = BrowserAccessibilityHelpers.StableInt32(key),
            MetadataJson = BuildMetadata(snap, key),
        };

        await _store.StoreAppSessionsAsync(new[] { session }, ct);
        await _store.StoreAppItemsAsync(new[] { rootItem }, ct);

        _tracked[key] = new TrackedWindow
        {
            SessionId = sessionId,
            RootItemId = rootItem.Id,
            JourneyId = sessionId,
            ProcessId = snap.ProcessId,
            LastUrl = url,
            LastTitle = snap.WindowTitle,
            IsIncognito = snap.IsIncognito,
            LastActivity = now,
            LastSeen = now,
        };

        _logger.LogInformation(
            "Browser journey opened via accessibility: {App} | {Title} | {Url}",
            session.AppDisplayName, snap.WindowTitle, string.IsNullOrEmpty(url) ? "(no url)" : url);
    }

    private async Task UpdateWindowAsync(TrackedWindow tw, AccessibilitySnapshot snap, CancellationToken ct)
    {
        var url = ResolveStoredUrl(snap);
        var titleChanged = !string.Equals(tw.LastTitle, snap.WindowTitle, StringComparison.Ordinal);
        var urlChanged = !string.Equals(tw.LastUrl ?? string.Empty, url, StringComparison.Ordinal);

        if (!titleChanged && !urlChanged)
            return;

        var now = snap.CapturedAt;

        // Title-only change: wait for the title to persist ~MinTabRotationInterval before
        // rotating the tab record, so badge/timer/player-state flicker never fragments the
        // journey. A real URL change (true navigation) rotates immediately. LastTitle is
        // NOT advanced during the hold, so the next poll still sees the pending change and
        // the video page is never silently merged into the previous page's record.
        if (titleChanged && !urlChanged)
        {
            var stable = string.Equals(snap.WindowTitle, tw.PendingTitle, StringComparison.Ordinal)
                         && now - tw.PendingSinceUtc >= MinTabRotationInterval;
            if (!stable)
            {
                if (!string.Equals(snap.WindowTitle, tw.PendingTitle, StringComparison.Ordinal))
                {
                    tw.PendingTitle = snap.WindowTitle;
                    tw.PendingSinceUtc = now;
                }
                tw.LastActivity = now;
                return;
            }
        }

        var newTitle = string.IsNullOrWhiteSpace(snap.WindowTitle) ? tw.LastTitle : snap.WindowTitle;
        // Accurate page-start time: for a title-only rotation this is when the new title
        // FIRST appeared (not when the 10s stability window elapsed).
        var openedAt = titleChanged && !urlChanged ? tw.PendingSinceUtc : now;

        // A persisted blank/unchanged title (transient load state) is not a new page —
        // don't rotate a duplicate-looking record.
        if (!urlChanged && string.Equals(newTitle, tw.LastTitle, StringComparison.Ordinal))
        {
            tw.PendingTitle = null;
            tw.PendingSinceUtc = now;
            tw.LastActivity = now;
            return;
        }

        // Each page visit gets its OWN browser_tab record — never overwrite the previous
        // page's title in place (that made "YouTube - Google Chrome" silently become the
        // video page). Close the current tab root keeping its ORIGINAL title/url/metadata
        // intact, then open a fresh root for the new page. The window session itself stays
        // open across navigations; browser_navigation children still record transitions.
        var newRootId = Guid.NewGuid().ToString("N");

        _logger.LogDebug("Browser tab rotated: '{From}' → '{To}' (urlChanged={UrlChanged})",
            tw.LastTitle, newTitle, urlChanged);

        // Dedicated close (sets is_synced = 0): a bare upsert would NOT reset is_synced,
        // so if the tab row was already synced the server would never learn it closed.
        // closed_at = the new page's opened_at for clean record continuity.
        await _store.CloseAppItemAsync(tw.RootItemId, openedAt, ct);

        await _store.StoreAppItemsAsync(new[]
        {
            new AppItem
            {
                Id = newRootId,
                AppSessionId = tw.SessionId,
                ItemType = "browser_tab",
                Title = newTitle,
                Identifier = newRootId,
                Url = url,
                Domain = BrowserAccessibilityHelpers.ExtractDomain(url),
                OpenedAt = openedAt,
                ProcessId = tw.ProcessId,
                ObjectType = "Tab",
                Action = "open",
                JourneyId = tw.JourneyId,
                Sequence = 1,
                CurrentPath = url,
                WindowId = BrowserAccessibilityHelpers.StableInt32(tw.SessionId),
                MetadataJson = BuildMetadata(snap, newRootId),
            }
        }, ct);

        // Record a navigation event when the address-bar URL changed.
        if (urlChanged && !string.IsNullOrEmpty(url) && !string.IsNullOrWhiteSpace(tw.LastUrl))
        {
            int sequence = 1;
            try { sequence = await _store.GetNextSequenceAsync(tw.JourneyId, ct); } catch { }

            await _store.StoreAppItemsAsync(new[]
            {
                new AppItem
                {
                    AppSessionId = tw.SessionId,
                    ParentItemId = newRootId,
                    ItemType = "browser_navigation",
                    Title = snap.WindowTitle,
                    Identifier = url,
                    Url = url,
                    Domain = BrowserAccessibilityHelpers.ExtractDomain(url),
                    OpenedAt = now,
                    ProcessId = tw.ProcessId,
                    ObjectType = "Page",
                    Action = "navigate",
                    JourneyId = tw.JourneyId,
                    Sequence = sequence,
                    PreviousPath = tw.LastUrl ?? string.Empty,
                    CurrentPath = url,
                    WindowId = BrowserAccessibilityHelpers.StableInt32(tw.SessionId),
                    MetadataJson = BuildMetadata(snap, newRootId),
                }
            }, ct);

            _logger.LogDebug("Browser navigation: {From} → {To}", tw.LastUrl, url);
        }

        tw.RootItemId = newRootId;
        tw.PendingTitle = null;
        tw.PendingSinceUtc = now;
        tw.LastUrl = url;
        tw.LastTitle = snap.WindowTitle;
        tw.LastActivity = now;
    }

    private async Task CloseWindowAsync(string key, TrackedWindow tw, DateTime closedAt, CancellationToken ct)
    {
        await _store.CloseSessionsAndAppItemsAsync(
            new[] { new AppSession { Id = tw.SessionId, ProcessName = string.Empty, EndedAt = closedAt } },
            closedAt, ct);
        _tracked.Remove(key);
    }

    private async Task CloseAllAsync(DateTime closedAt, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var kv in _tracked.ToList())
                await CloseWindowAsync(kv.Key, kv.Value, closedAt, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Incognito URLs are dropped unless explicitly enabled (legal-safe default).</summary>
    private string ResolveStoredUrl(AccessibilitySnapshot snap)
    {
        if (snap.IsIncognito && !_config.BrowserCaptureIncognito)
            return string.Empty;
        return snap.Url ?? string.Empty;
    }

    private string BuildMetadata(AccessibilitySnapshot snap, string windowKey) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["source"] = "accessibility",
            ["windowKey"] = windowKey,
            ["incognito"] = snap.IsIncognito,
            ["processName"] = snap.ProcessName,
            ["capturedAt"] = snap.CapturedAt.ToString("O"),
        });

    private static string StripBrowserSuffix(string title)
    {
        foreach (var marker in new[] { " - Google Chrome", " - Mozilla Firefox", " - Microsoft Edge", " - Brave", " - Opera", " - Vivaldi" })
        {
            var idx = title.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return title[..idx].Trim();
        }
        return string.IsNullOrWhiteSpace(title) ? "Browser" : title;
    }

    private async Task RefreshEmployeeInfoAsync(CancellationToken ct)
    {
        try
        {
            var info = await _store.GetEmployeeInfoAsync(ct);
            _employeeId = info?.EmployeeId;
            _employeeName = info?.Name;
            _lastEmployeeRefresh = DateTime.UtcNow;
        }
        catch
        {
            _employeeId = null;
            _employeeName = null;
        }
    }

    private int intervalSeconds => Math.Max(2, _config.BrowserAccessibilityPollSec);

    // ─── Downloads watcher (part of the journey: files saved from the browser) ───

    private void StartDownloadWatchers()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var paths = new[]
            {
                Path.Combine(home, "Downloads"),
                Path.Combine(home, "downloads"),
            };
            foreach (var p in paths.Distinct())
            {
                try
                {
                    if (!Directory.Exists(p)) continue;
                    var fsw = new FileSystemWatcher(p)
                    {
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
                    };
                    fsw.Created += OnDownloadFileCreated;
                    _downloadWatchers.Add(fsw);
                    _logger.LogDebug("Download watcher registered for {Path}", p);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to start download watcher for {Path}", p);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to initialize download watchers");
        }
    }

    private async void OnDownloadFileCreated(object? sender, FileSystemEventArgs e)
    {
        try
        {
            if (_stopping) return;

            // Skip temp/partial files (Chrome uses .crdownload, browsers use .part).
            var ext = Path.GetExtension(e.FullPath);
            if (ext.Equals(".crdownload", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                return;

            await Task.Delay(800);
            var fi = new FileInfo(e.FullPath);
            if (!fi.Exists) return;

            TrackedWindow? target = null;
            await _gate.WaitAsync();
            try
            {
                target = _tracked.Values
                    .OrderByDescending(t => t.LastActivity)
                    .FirstOrDefault();
            }
            finally
            {
                _gate.Release();
            }
            if (target == null)
            {
                _logger.LogDebug("Download recorded without an active browser session (skipped): {File}", e.FullPath);
                return;
            }

            int sequence = 1;
            try { sequence = await _store.GetNextSequenceAsync(target.JourneyId, CancellationToken.None); } catch { }

            var item = new AppItem
            {
                AppSessionId = target.SessionId,
                ParentItemId = target.RootItemId,
                ItemType = "browser_download",
                Title = fi.Name,
                Identifier = e.FullPath,
                CurrentPath = e.FullPath,
                OpenedAt = DateTime.UtcNow,
                ProcessId = target.ProcessId,
                ObjectType = "Download",
                Action = "download",
                JourneyId = target.JourneyId,
                Sequence = sequence,
                WindowId = BrowserAccessibilityHelpers.StableInt32(target.SessionId),
                MetadataJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["source"] = "downloads-watcher",
                    ["path"] = e.FullPath,
                }),
            };
            await _store.StoreAppItemsAsync(new[] { item }, CancellationToken.None);
            _logger.LogInformation("Recorded browser download: {File}", fi.Name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Download watcher handler failed for {File}", e.FullPath);
        }
    }
}
