namespace client.Core.Browser.Abstractions;

/// <summary>
/// A live debugger connection to a runtime. Raises raw engine events and can be
/// asked for a tab snapshot. One connection per runtime covers all its
/// profiles + windows + tabs.
/// </summary>
public interface IBrowserConnection : IAsyncDisposable
{
    event EventHandler<RawBrowserEvent>? EventReceived;
    bool IsConnected { get; }
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    Task<IReadOnlyList<BrowserTabSnapshot>> QueryTabsAsync(CancellationToken ct);
}

/// <summary>
/// Engine-scoped adapter. Each engine (Chromium CDP / Gecko RDP+BiDi / WebKit WIP)
/// implements this. Detection is driven by installed-app metadata, never brand strings.
/// </summary>
public interface IBrowserEngineAdapter
{
    BrowserEngine Engine { get; }

    /// <summary>Detect installed runtimes of this engine on the machine.</summary>
    Task<IReadOnlyList<DetectedBrowserRuntime>> DetectRuntimesAsync(CancellationToken ct);

    /// <summary>Probe what a specific runtime can actually do (never assumed).</summary>
    BrowserCapabilities ProbeCapabilities(DetectedBrowserRuntime runtime);

    /// <summary>
    /// Discover profiles for a runtime from its profile database on disk
    /// (Local State / profiles.ini / WebKit profile dir).
    /// </summary>
    IReadOnlyList<BrowserProfileInfo> DiscoverProfiles(DetectedBrowserRuntime runtime);

    /// <summary>
    /// Ensure a debugger is available for the runtime on the given port and open a
    /// connection. May launch/relaunch the browser with the debug flag. Returns null
    /// when the engine cannot provide a debugger (capability Unsupported / Manual).
    /// </summary>
    Task<IBrowserConnection?> LaunchAndConnectAsync(DetectedBrowserRuntime runtime, int port, CancellationToken ct);
}

/// <summary>Persistence contract for browser runtime state (runtimes, profiles, tabs, ports, journeys).</summary>
public interface IBrowserRuntimeStore
{
    Task UpsertRuntimeAsync(DetectedBrowserRuntime runtime, CancellationToken ct);
    Task<IReadOnlyList<DetectedBrowserRuntime>> LoadRuntimesAsync(CancellationToken ct);
    Task DeleteRuntimeAsync(Guid runtimeId, CancellationToken ct);

    Task UpsertJourneyAsync(BrowserJourneyRecord journey, CancellationToken ct);
    Task<IReadOnlyList<BrowserJourneyRecord>> LoadOpenJourneysAsync(CancellationToken ct);
    Task CloseJourneyAsync(Guid journeyId, string sessionId, DateTime closedAt, CancellationToken ct);
    Task CloseAllOpenJourneysAsync(DateTime closedAt, CancellationToken ct);

    Task SetPortLeaseAsync(Guid runtimeId, int port, CancellationToken ct);
    Task<int?> GetPortLeaseAsync(Guid runtimeId, CancellationToken ct);
    Task ClearPortLeaseAsync(Guid runtimeId, CancellationToken ct);
}

/// <summary>Persisted mapping of a per-tab journey to its app_sessions row.</summary>
public sealed class BrowserJourneyRecord
{
    public Guid JourneyId { get; set; }
    public Guid TabId { get; set; }
    public Guid RuntimeId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
}
