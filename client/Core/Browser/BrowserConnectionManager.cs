using client.Core.Browser.Abstractions;
using Microsoft.Extensions.Logging;

namespace client.Core.Browser;

/// <summary>
/// Per-runtime connection lifecycle: connect, initial-tab rebuild, reconnect with backoff.
/// One connection per runtime covers all profiles/windows/tabs of that runtime.
/// </summary>
public sealed class BrowserConnectionManager
{
    private readonly ILogger<BrowserConnectionManager> _logger;
    private readonly BrowserEventCoordinator _coordinator;
    private readonly Dictionary<Guid, ManagedConnection> _managed = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private sealed class ManagedConnection
    {
        public required IBrowserEngineAdapter Adapter { get; init; }
        public required DetectedBrowserRuntime Runtime { get; init; }
        public required int Port { get; init; }
        public IBrowserConnection? Connection { get; set; }
        public DateTime LastReconnectAttempt { get; set; }
    }

    public BrowserConnectionManager(BrowserEventCoordinator coordinator, ILogger<BrowserConnectionManager> logger)
    {
        _coordinator = coordinator;
        _logger = logger;
    }

    public bool IsManaged(Guid runtimeId) { lock (_gate) return _managed.ContainsKey(runtimeId); }

    public async Task StartAsync(IBrowserEngineAdapter adapter, DetectedBrowserRuntime runtime, int port, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_managed.ContainsKey(runtime.Id)) return;
            _managed[runtime.Id] = new ManagedConnection { Adapter = adapter, Runtime = runtime, Port = port };
        }

        await ConnectAsync(runtime.Id, ct);
    }

    public async Task ReconnectIdleAsync(CancellationToken ct)
    {
        List<ManagedConnection> snapshot;
        lock (_gate) snapshot = _managed.Values.ToList();
        foreach (var managed in snapshot)
        {
            if (managed.Connection != null && managed.Connection.IsConnected) continue;
            if ((DateTime.UtcNow - managed.LastReconnectAttempt).TotalSeconds < 10) continue;
            managed.LastReconnectAttempt = DateTime.UtcNow;
            await ConnectAsync(managed.Runtime.Id, ct);
        }
    }

    private async Task ConnectAsync(Guid runtimeId, CancellationToken ct)
    {
        await _connectGate.WaitAsync(ct);
        try
        {
            ManagedConnection? managed;
            lock (_gate) _managed.TryGetValue(runtimeId, out managed);
            if (managed == null) return;
            if (managed.Connection != null && managed.Connection.IsConnected) return;

            var conn = await managed.Adapter.LaunchAndConnectAsync(managed.Runtime, managed.Port, ct);
            if (conn == null)
            {
                managed.Runtime.State = BrowserRuntimeState.Recovery;
                _logger.LogWarning("No debugger available for {Runtime}; will retry", managed.Runtime.BinaryName);
                return;
            }

            managed.Runtime.State = BrowserRuntimeState.DebuggerConnected;
            _coordinator.Attach(conn);
            managed.Connection = conn;
            await RebuildInitialTabsAsync(conn, managed.Runtime, ct);
            _logger.LogInformation("Debugger connected for {Runtime} on port {Port}", managed.Runtime.BinaryName, managed.Port);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Emit a created event for every currently-open tab so journeys start for pre-existing tabs.</summary>
    private async Task RebuildInitialTabsAsync(IBrowserConnection conn, DetectedBrowserRuntime runtime, CancellationToken ct)
    {
        try
        {
            var tabs = await conn.QueryTabsAsync(ct);
            foreach (var tab in tabs)
            {
                if (string.IsNullOrEmpty(tab.TargetId)) continue;
                _coordinator.Publish(new RawBrowserEvent
                {
                    Engine = runtime.Engine,
                    Source = ToSource(runtime.Engine),
                    RuntimeId = runtime.Id.ToString("N"),
                    TabId = tab.TargetId,
                    WindowId = tab.WindowId,
                    Title = tab.Title,
                    Url = tab.Url,
                    Incognito = tab.Incognito,
                    Action = "created",
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial tab rebuild failed for {Runtime}", runtime.BinaryName);
        }
    }

    public async Task StopAsync(Guid runtimeId, CancellationToken ct)
    {
        ManagedConnection? managed;
        lock (_gate) _managed.TryGetValue(runtimeId, out managed);
        if (managed == null) return;
        if (managed.Connection != null)
        {
            _coordinator.Detach(managed.Connection);
            try
            {
                await managed.Connection.StopAsync();
                await managed.Connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Connection teardown for {Runtime} had a non-fatal error", managed.Runtime.BinaryName);
            }
            managed.Connection = null;
        }
        lock (_gate) _managed.Remove(runtimeId);
    }

    private static BrowserEventSource ToSource(BrowserEngine engine) => engine switch
    {
        BrowserEngine.Gecko => BrowserEventSource.GeckoRdp,
        BrowserEngine.WebKit => BrowserEventSource.WebKitInspector,
        _ => BrowserEventSource.Cdp,
    };
}
