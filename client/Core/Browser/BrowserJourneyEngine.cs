using System.Security.Cryptography;
using System.Text;
using client.Core.Abstractions;
using client.Core.Browser.Abstractions;
using client.Core.Models;
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
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BrowserJourneyEngine(ILogStore logStore, IBrowserRuntimeStore runtimeStore, ILogger<BrowserJourneyEngine> logger)
    {
        _logStore = logStore;
        _runtimeStore = runtimeStore;
        _logger = logger;
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
        if (_openByTab.ContainsKey(e.TabId))
        {
            _openByTab[e.TabId].LastUrl = e.Url ?? _openByTab[e.TabId].LastUrl;
            return;
        }

        var journeyId = e.JourneyId;
        var sessionId = Guid.NewGuid().ToString("N");
        var binaryName = GetMetadata(e, "binaryName");

        // Resolve the FK to installed_applications from the CLIENT DB (the detector's
        // transient id is not guaranteed to exist there, and an invalid FK aborts the insert).
        string? installedAppId = null;
        try
        {
            installedAppId = (await _logStore.GetInstalledAppByBinaryNameAsync(binaryName, ct))?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Installed-app lookup failed for {Binary}", binaryName);
        }

        var session = new AppSession
        {
            Id = sessionId,
            ProcessName = binaryName,
            AppDisplayName = GetMetadata(e, "displayName"),
            StartedAt = e.Timestamp.UtcDateTime,
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
        };
        _logger.LogInformation("Browser journey opened {Tab} → session {Session}", e.TabId.ToString("N")[..8], sessionId);
    }

    /// <summary>Close every open tab of a runtime that just went away (browser process exit).</summary>
    public async Task CloseRuntimeTabsAsync(Guid runtimeId, DateTime closedAt, CancellationToken ct)
    {
        var victims = _openByTab.Where(kv => kv.Value.RuntimeId == runtimeId).Select(kv => kv.Key).ToList();
        foreach (var tabId in victims)
        {
            if (!_openByTab.Remove(tabId, out var tab)) continue;
            var closing = new AppSession { Id = tab.SessionId, EndedAt = closedAt };
            await _logStore.CloseSessionsAndAppItemsAsync(new[] { closing }, closedAt, ct);
            await _runtimeStore.CloseJourneyAsync(tab.JourneyId, tab.SessionId, closedAt, ct);
            _logger.LogInformation("Browser journey closed on runtime exit {Tab}", tabId.ToString("N")[..8]);
        }
    }

    private async Task RecordNavigationAsync(BrowserEvent e, CancellationToken ct, bool requireUrlChange = false)
    {
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
        if (!_openByTab.Remove(e.TabId, out var tab)) return;
        var closedAt = e.Timestamp.UtcDateTime;
        var closing = new AppSession { Id = tab.SessionId, EndedAt = closedAt };
        await _logStore.CloseSessionsAndAppItemsAsync(new[] { closing }, closedAt, ct);
        await _runtimeStore.CloseJourneyAsync(tab.JourneyId, tab.SessionId, closedAt, ct);
        _logger.LogInformation("Browser journey closed {Tab} → session {Session}", e.TabId.ToString("N")[..8], tab.SessionId);
    }

    private int NextSequence(Guid journeyId)
    {
        _sequenceByJourney.TryGetValue(journeyId, out var seq);
        seq++;
        _sequenceByJourney[journeyId] = seq;
        return seq;
    }

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
}
