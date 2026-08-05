using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using client.Core.Abstractions;
using client.Core.Browser.Abstractions;
using client.Core.Models;
using client.Configuration;
using Microsoft.Extensions.Logging;

namespace client.Core.Browser;

/// <summary>
/// Writes per-tab browser journeys into the standard activity schema:
/// one AppSession per tab (browser_tab root item) + browser_navigation children.
/// Exactly one journey per tab; identity is UUID (never titles/PIDs).
/// Depends on Coordinator (upstream) and ILogStore + IBrowserRuntimeStore (downstream).
/// </summary>
public sealed class BrowserJourneyEngine
{
    private readonly ILogStore _logStore;
    private readonly IBrowserRuntimeStore _runtimeStore;
    private readonly AppConfig _config;
    private readonly ILogger<BrowserJourneyEngine> _logger;

    private sealed class OpenTab
    {
        public required string SessionId { get; init; }
        public required Guid JourneyId { get; init; }
        public required Guid RuntimeId { get; init; }
        public string? TabItemId { get; set; }
        public string? LastUrl { get; set; }
        public DateTime LastNavAt { get; set; }
    }

    private readonly Dictionary<Guid, OpenTab> _openByTab = new();
    private readonly Dictionary<Guid, int> _sequenceByJourney = new();
    private readonly Dictionary<Guid, PendingTab> _pendingByTab = new();
    private readonly List<FileSystemWatcher> _downloadWatchers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed class PendingTab
    {
        public required BrowserEvent FirstEvent { get; init; }
        public int EventCount { get; set; }
        public DateTime LastEventAt { get; set; }
        public CancellationTokenSource? PersistCts { get; set; }
    }

    public BrowserJourneyEngine(ILogStore logStore, IBrowserRuntimeStore runtimeStore, AppConfig config, ILogger<BrowserJourneyEngine> logger)
    {
        _logStore = logStore;
        _runtimeStore = runtimeStore;
        _config = config;
        _logger = logger;
        StartDefaultDownloadWatchers();
    }

    public void Attach(BrowserEventCoordinator coordinator)
    {
        coordinator.CanonicalEvent += OnCanonicalEvent;
    }

    /// <summary>Rebuild the in-memory tab map from persisted open journeys (boot recovery).</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        var open = await _runtimeStore.LoadOpenJourneysAsync(ct);
        foreach (var journey in open)
        {
            var tabItem = await _logStore.GetOpenAppItemAsync(
                journey.SessionId, "browser_tab", journey.TabId.ToString("N"), ct);
            _openByTab[journey.TabId] = new OpenTab
            {
                SessionId = journey.SessionId,
                JourneyId = journey.JourneyId,
                RuntimeId = journey.RuntimeId,
                TabItemId = tabItem?.Id,
                // Recovered journeys must not look instantly idle, or the first idle sweep would
                // close them the moment the tracker restarts.
                LastNavAt = DateTime.UtcNow,
            };
        }
        _logger.LogInformation("Browser journey engine recovered {Count} open journeys", open.Count);
    }

    private async void OnCanonicalEvent(object? sender, BrowserEvent e)
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                switch (e.Action)
                {
                    case BrowserEventAction.Created:
                    case BrowserEventAction.Activated:
                        await EnsureTabOpenAsync(e, CancellationToken.None);
                        break;
                    case BrowserEventAction.Navigated:
                    case BrowserEventAction.Reloaded:
                        await EnsureTabOpenAsync(e, CancellationToken.None);
                        await RecordNavigationAsync(e, CancellationToken.None);
                        break;
                    case BrowserEventAction.Updated:
                        if (_openByTab.ContainsKey(e.TabId))
                            await RecordNavigationAsync(e, CancellationToken.None, requireUrlChange: true);
                        break;
                    case BrowserEventAction.Closed:
                        await CloseTabAsync(e, CancellationToken.None);
                        break;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle browser event {Action}", e.Action);
        }
    }

    private async Task EnsureTabOpenAsync(BrowserEvent e, CancellationToken ct)
    {
        // If already open, just refresh last URL
        if (_openByTab.ContainsKey(e.TabId))
        {
            _openByTab[e.TabId].LastUrl = e.Url ?? _openByTab[e.TabId].LastUrl;
            return;
        }

        // If this tab is pending, update counters and potentially persist early
        if (_pendingByTab.TryGetValue(e.TabId, out var pending))
        {
            pending.EventCount++;
            pending.LastEventAt = DateTime.UtcNow;
            if (pending.EventCount >= _config.BrowserMinMeaningfulEvents)
            {
                await PersistPendingTabAsync(e.TabId, ct);
            }
            return;
        }

        // New pending tab: accumulate events for a short threshold before persisting to DB
        var first = e;
        pending = new PendingTab
        {
            FirstEvent = first,
            EventCount = 1,
            LastEventAt = DateTime.UtcNow,
            PersistCts = new CancellationTokenSource()
        };
        _pendingByTab[e.TabId] = pending;

        // Schedule delayed persistence based on config threshold
        var delay = Math.Max(1, _config.BrowserRuntimePersistThresholdSec);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), pending.PersistCts!.Token);
                await _gate.WaitAsync();
                try
                {
                    // Time-based guarantee: a tab still open after the persist threshold is real
                    // activity even if it produced fewer than MinMeaningfulEvents events. Tabs
                    // closed BEFORE the threshold are dropped by CloseTabAsync — that is the
                    // actual ephemeral-tab filter. (The former "ephemeral TTL drop" else-if was
                    // unreachable: the delay already equals the persist threshold, so the drop
                    // branch could never run. Per-tab TTL is handled by CloseTabAsync and
                    // CloseIdleJourneysAsync instead.)
                    if (_pendingByTab.ContainsKey(e.TabId))
                        await PersistPendingTabAsync(e.TabId, CancellationToken.None);
                }
                finally { _gate.Release(); }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Persist scheduling failed for tab {Tab}", e.TabId.ToString("N")[..8]);
            }
        });
    }

    /// <summary>Close every open tab of a runtime that just went away (browser process exit).</summary>
    public async Task CloseRuntimeTabsAsync(Guid runtimeId, DateTime closedAt, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var victims = _openByTab.Where(kv => kv.Value.RuntimeId == runtimeId).Select(kv => kv.Key).ToList();
            foreach (var tabId in victims)
                await CloseOpenTabAsync(tabId, closedAt, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task RecordNavigationAsync(BrowserEvent e, CancellationToken ct, bool requireUrlChange = false)
    {
        // If tab is pending, count the event and potentially persist first
        if (_pendingByTab.TryGetValue(e.TabId, out var pending))
        {
            pending.EventCount++;
            pending.LastEventAt = DateTime.UtcNow;
            if (pending.EventCount >= _config.BrowserMinMeaningfulEvents)
            {
                await PersistPendingTabAsync(e.TabId, ct);
            }
            return;
        }

        if (!_openByTab.TryGetValue(e.TabId, out var tab)) return;
        if (string.IsNullOrWhiteSpace(e.Url)) return;
        if (e.Url == tab.LastUrl && requireUrlChange) return;
        if (e.Url == tab.LastUrl && (DateTime.UtcNow - tab.LastNavAt).TotalSeconds < 30) return;

        var previous = tab.LastUrl ?? string.Empty;
        tab.LastUrl = e.Url;
        tab.LastNavAt = DateTime.UtcNow;

        var nav = new AppItem
        {
            AppSessionId = tab.SessionId,
            ParentItemId = tab.TabItemId,
            ItemType = "browser_navigation",
            Title = e.Title ?? e.Url!,
            Identifier = e.Url!,
            Url = e.Url!,
            Domain = e.Domain ?? string.Empty,
            OpenedAt = e.Timestamp.UtcDateTime,
            ObjectType = "Page",
            Action = e.Action == BrowserEventAction.Reloaded ? "reload" : "navigate",
            JourneyId = tab.JourneyId.ToString("N"),
            Sequence = NextSequence(tab.JourneyId),
            PreviousPath = previous,
            CurrentPath = e.Url!,
            WindowId = WindowToInt(e.WindowId),
            TabId = TabToInt(e.TabId),
            MetadataJson = e.ToMetadataJson(),
        };
        await _logStore.StoreAppItemsAsync(new[] { nav }, ct);
    }

    private async Task CloseTabAsync(BrowserEvent e, CancellationToken ct)
    {
        // If this tab was pending but never met threshold, drop it silently
        if (_pendingByTab.TryGetValue(e.TabId, out var pending))
        {
            pending.PersistCts?.Cancel();
            _pendingByTab.Remove(e.TabId);
            _logger.LogDebug("Ephemeral tab closed before persist {Tab}", e.TabId.ToString("N")[..8]);
            return;
        }

        await CloseOpenTabAsync(e.TabId, e.Timestamp.UtcDateTime, ct);
    }

    /// <summary>
    /// Close journeys that have been idle longer than BrowserJourneyIdleMinutes (no navigation
    /// and no tab event). Also drops pending tabs idle past the same window. Called by the
    /// watchdog every minute — prevents sessions staying open for weeks on an idle tab.
    /// </summary>
    public async Task CloseIdleJourneysAsync(CancellationToken ct)
    {
        var idle = TimeSpan.FromMinutes(Math.Max(1, _config.BrowserJourneyIdleMinutes));
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var idleTabs = _openByTab.Where(kv => now - kv.Value.LastNavAt > idle).Select(kv => kv.Key).ToList();
            foreach (var tabId in idleTabs)
            {
                _logger.LogInformation(
                    "Browser journey closed (idle {Min}m) {Tab}", _config.BrowserJourneyIdleMinutes, tabId.ToString("N")[..8]);
                await CloseOpenTabAsync(tabId, now, ct);
            }

            var idlePending = _pendingByTab.Where(kv => now - kv.Value.LastEventAt > idle).Select(kv => kv.Key).ToList();
            foreach (var tabId in idlePending)
            {
                _pendingByTab[tabId].PersistCts?.Cancel();
                _pendingByTab.Remove(tabId);
                _logger.LogDebug("Dropped idle pending tab {Tab}", tabId.ToString("N")[..8]);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Close a single open tab: close its session + open app_items + journey row.</summary>
    private async Task CloseOpenTabAsync(Guid tabId, DateTime closedAt, CancellationToken ct)
    {
        if (!_openByTab.Remove(tabId, out var tab)) return;
        var closing = new AppSession { Id = tab.SessionId, EndedAt = closedAt };
        await _logStore.CloseSessionsAndAppItemsAsync(new[] { closing }, closedAt, ct);
        await _runtimeStore.CloseJourneyAsync(tab.JourneyId, tab.SessionId, closedAt, ct);
        _logger.LogInformation("Browser journey closed {Tab} → session {Session}", tabId.ToString("N")[..8], tab.SessionId);
    }

    private int NextSequence(Guid journeyId)
    {
        _sequenceByJourney.TryGetValue(journeyId, out var seq);
        seq++;
        _sequenceByJourney[journeyId] = seq;
        return seq;
    }

    private void StartDefaultDownloadWatchers()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // Distinct with an ordinal-ignore-case comparer: on case-insensitive file systems
            // (Windows) "Downloads" and "downloads" are the SAME folder — without this every
            // download would be recorded twice.
            var paths = new[]
            {
                Path.Combine(home, "Downloads"),
                Path.Combine(home, "downloads"),
            }.Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                try
                {
                    if (!Directory.Exists(p)) continue;
                    var fsw = new FileSystemWatcher(p)
                    {
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size
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
            _logger.LogDebug(ex, "Failed to initialize default download watchers");
        }
    }

    private async void OnDownloadFileCreated(object? sender, FileSystemEventArgs e)
    {
        try
        {
            // brief delay to allow file to appear
            await Task.Delay(500);
            var fi = new FileInfo(e.FullPath);
            if (!fi.Exists) return;

            // attempt to correlate with most-recent open tab by domain
            string? matchedSession = null;
            string? matchedTabId = null;
            string? host = null;
            await _gate.WaitAsync();
            try
            {
                // Try to find domain in filename (common when Chrome uses origin in names)
                var name = fi.Name;
                // simplistic: find an open tab whose LastUrl host appears in filename
                foreach (var kv in _openByTab)
                {
                    var last = kv.Value.LastUrl;
                    if (string.IsNullOrWhiteSpace(last)) continue;
                    if (Uri.TryCreate(last, UriKind.Absolute, out var u))
                    {
                        if (name.Contains(u.Host, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedSession = kv.Value.SessionId;
                            matchedTabId = kv.Key.ToString("N");
                            host = u.Host;
                            break;
                        }
                    }
                }

                // fallback: attach to any most-recent open tab
                if (matchedSession == null && _openByTab.Count > 0)
                    matchedSession = _openByTab.Values.Last().SessionId;
            }
            finally { _gate.Release(); }

            // No open tab to correlate with → skip. Writing an item with an empty session id
            // would create an orphan row that joins to nothing locally or on the server.
            if (matchedSession == null)
            {
                _logger.LogDebug("Download {File} not recorded — no open browser tab to correlate with", e.FullPath);
                return;
            }

            var downloadItem = new AppItem
            {
                AppSessionId = matchedSession,
                ItemType = "browser_download",
                Title = fi.Name,
                Identifier = e.FullPath,
                Url = string.Empty,
                Domain = host ?? string.Empty,
                OpenedAt = DateTime.UtcNow,
                ObjectType = "Download",
                Action = "download",
                JourneyId = matchedTabId ?? string.Empty,
                Sequence = 0,
                CurrentPath = e.FullPath,
                TabId = matchedTabId != null && Guid.TryParse(matchedTabId, out var tabGuid) ? TabToInt(tabGuid) : null,
                MetadataJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = e.FullPath })
            };
            await _logStore.StoreAppItemsAsync(new[] { downloadItem }, CancellationToken.None);
            _logger.LogInformation("Recorded download {File}", e.FullPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Download watcher handler failed for {File}", e.FullPath);
        }
    }

    private static string DetectPlatform() =>
        OperatingSystem.IsLinux() ? "Linux"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsWindows() ? "Windows"
        : Environment.OSVersion.ToString();

    private static string GetMetadata(BrowserEvent e, string key) =>
        e.Metadata.TryGetValue(key, out var v) ? v : string.Empty;

    private static int? WindowToInt(Guid? windowId)
    {
        if (windowId == null) return null;
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("w/" + windowId.Value.ToString("N")));
        var value = BitConverter.ToInt32(hash, 0) & 0x7FFFFFFF;
        return value;
    }

    private static int? TabToInt(Guid tabId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("t/" + tabId.ToString("N")));
        var value = BitConverter.ToInt32(hash, 0) & 0x7FFFFFFF;
        return value;
    }

    // Persist a pending tab into the real session/journey/app_items
    private async Task PersistPendingTabAsync(Guid tabId, CancellationToken ct)
    {
        if (!_pendingByTab.TryGetValue(tabId, out var pending)) return;
        // cancel outstanding timer
        try { pending.PersistCts?.Cancel(); } catch { }
        _pendingByTab.Remove(tabId);

        var e = pending.FirstEvent;
        var journeyId = e.JourneyId;
        var sessionId = Guid.NewGuid().ToString("N");
        var binaryName = GetMetadata(e, "binaryName");

        string? installedAppId = null;
        try { installedAppId = (await _logStore.GetInstalledAppByBinaryNameAsync(binaryName, ct))?.Id; } catch { }

        string? empId = null; string? empName = null;
        try { var emp = await _logStore.GetEmployeeInfoAsync(ct); empId = emp?.EmployeeId; empName = emp?.Name; } catch { }

        var session = new AppSession
        {
            Id = sessionId,
            ProcessName = binaryName,
            AppDisplayName = GetMetadata(e, "displayName"),
            StartedAt = e.Timestamp.UtcDateTime,
            MachineId = _config.ClientId,
            EmployeeId = empId,
            EmployeeName = empName,
            SessionId = SessionInfo.SessionId,
            Platform = DetectPlatform(),
            ContextLabel = GetMetadata(e, "contextLabel"),
            InstalledAppId = installedAppId,
        };
        await _logStore.StoreAppSessionsAsync(new[] { session }, ct);
        await _runtimeStore.UpsertJourneyAsync(new BrowserJourneyRecord
        {
            JourneyId = journeyId,
            TabId = e.TabId,
            RuntimeId = e.RuntimeId,
            SessionId = sessionId,
            OpenedAt = e.Timestamp.UtcDateTime,
        }, ct);

        var tabItem = new AppItem
        {
            AppSessionId = sessionId,
            ItemType = "browser_tab",
            Title = e.Title ?? "New Tab",
            Identifier = e.TabId.ToString("N"),
            Url = e.Url ?? string.Empty,
            Domain = e.Domain ?? string.Empty,
            OpenedAt = e.Timestamp.UtcDateTime,
            ObjectType = "Tab",
            Action = "open",
            JourneyId = journeyId.ToString("N"),
            Sequence = NextSequence(journeyId),
            CurrentPath = e.Url ?? string.Empty,
            WindowId = WindowToInt(e.WindowId),
            MetadataJson = e.ToMetadataJson(),
        };
        await _logStore.StoreAppItemsAsync(new[] { tabItem }, ct);

        _openByTab[e.TabId] = new OpenTab
        {
            SessionId = sessionId,
            JourneyId = journeyId,
            RuntimeId = e.RuntimeId,
            TabItemId = tabItem.Id,
            LastUrl = e.Url,
            LastNavAt = DateTime.UtcNow,
        };
        _logger.LogInformation("Browser journey opened (persisted) {Tab} → session {Session}", e.TabId.ToString("N")[..8], sessionId);
    }

}
