using client.Core.Browser.Abstractions;
using Microsoft.Extensions.Logging;
using client.Configuration;

namespace client.Core.Browser;

/// <summary>
/// Owner of the in-memory Browser Registry: Engine → Runtime → Profiles → Connections.
/// Runs the per-runtime state machine and persists runtime rows. This is the top of the
/// journey dependency chain (aiplan.txt §29): Runtime → Coordinator → JourneyEngine → Store.
/// </summary>
public sealed class BrowserRuntimeManager
{
    private readonly AppConfig _config;
    private readonly IReadOnlyList<IBrowserEngineAdapter> _adapters;
    private readonly IBrowserRuntimeStore _store;
    private readonly BrowserEventCoordinator _coordinator;
    private readonly BrowserConnectionManager _connections;
    private readonly DebugPortManager _ports;
    private readonly ILogger<BrowserRuntimeManager> _logger;

    private readonly Dictionary<Guid, DetectedBrowserRuntime> _runtimes = new();
    private readonly Dictionary<Guid, DateTime> _firstSeen = new();

    public event EventHandler<Guid>? RuntimeGone;

    public BrowserRuntimeManager(
        AppConfig config,
        IEnumerable<IBrowserEngineAdapter> adapters,
        IBrowserRuntimeStore store,
        BrowserEventCoordinator coordinator,
        BrowserConnectionManager connections,
        DebugPortManager ports,
        ILogger<BrowserRuntimeManager> logger)
    {
        _config = config;
        _adapters = adapters.ToList();
        _store = store;
        _coordinator = coordinator;
        _connections = connections;
        _ports = ports;
        _logger = logger;
    }

    public IReadOnlyCollection<DetectedBrowserRuntime> All => _runtimes.Values;

    public DetectedBrowserRuntime? Lookup(Guid runtimeId) =>
        _runtimes.TryGetValue(runtimeId, out var r) ? r : null;

    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var persisted in await _store.LoadRuntimesAsync(ct))
        {
            _runtimes[persisted.Id] = persisted;
        }
        await RefreshAsync(ct);
    }

    /// <summary>Detect installed runtimes, merge into the registry, and (re)open connections.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        foreach (var adapter in _adapters)
        {
            IReadOnlyList<DetectedBrowserRuntime> detected;
            try
            {
                detected = await adapter.DetectRuntimesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Detection failed for engine {Engine}", adapter.Engine);
                continue;
            }

            foreach (var runtime in detected)
            {
                runtime.Id = BrowserIdMapper.ForRuntime(runtime.BinaryPath ?? runtime.BinaryName);
                runtime.State = BrowserRuntimeState.Detected;
                runtime.LastSeenAt = DateTime.UtcNow;

                var tracked = runtime;
                if (_runtimes.TryGetValue(runtime.Id, out var existing))
                {
                    existing.DisplayName = runtime.DisplayName;
                    existing.Version = runtime.Version;
                    existing.UserDataDir = runtime.UserDataDir;
                    existing.Profiles = runtime.Profiles;
                    existing.Capabilities = runtime.Capabilities;
                    existing.LastSeenAt = runtime.LastSeenAt;
                    tracked = existing;
                }
                else
                {
                    _runtimes[runtime.Id] = runtime;
                    _firstSeen[runtime.Id] = DateTime.UtcNow;
                    _logger.LogInformation("Detected browser runtime {Display} ({Binary})", runtime.DisplayName, runtime.BinaryName);
                }

                await _store.UpsertRuntimeAsync(tracked, ct);

                // Start debounce: suppress attach attempts within BrowserStartDebounceSec of the
                // first sighting so a browser that keeps crashing/restarting does not churn the
                // attach path (and the hijack) on every watchdog cycle.
                if (_firstSeen.TryGetValue(tracked.Id, out var seen)
                    && DateTime.UtcNow - seen < TimeSpan.FromSeconds(Math.Max(0, _config.BrowserStartDebounceSec)))
                {
                    _logger.LogDebug("Start debounce: skipping attach for {Runtime}", tracked.BinaryName);
                    continue;
                }

                if (!_connections.IsManaged(tracked.Id) && IsAutoDebugger(tracked))
                {
                    if (_config.BrowserMaxConcurrentSessions > 0 && _connections.ManagedCount() >= _config.BrowserMaxConcurrentSessions)
                    {
                        _logger.LogDebug("Max concurrent browser sessions reached ({Max}) — skipping attach for {Runtime}",
                            _config.BrowserMaxConcurrentSessions, tracked.BinaryName);
                        continue;
                    }

                    var port = await _ports.AllocateAsync(tracked.Id, ct);
                    tracked.DebugPort = port;
                    await _store.UpsertRuntimeAsync(tracked, ct);
                    await _connections.StartAsync(adapter, tracked, port, ct);
                }
                else if (!IsAutoDebugger(tracked))
                {
                    tracked.State = tracked.Capabilities[Capability.Debugger] == CapabilityClassification.AdminAssisted ||
                                    tracked.Capabilities[Capability.Debugger] == CapabilityClassification.UserAssisted
                        ? BrowserRuntimeState.ManualSetupRequired
                        : BrowserRuntimeState.Unsupported;
                    await _store.UpsertRuntimeAsync(tracked, ct);
                }
            }
        }
    }

    /// <summary>Watchdog tick: reconcile process liveness, emit RuntimeGone when a browser exits.</summary>
    public async Task ScanProcessStateAsync(CancellationToken ct)
    {
        foreach (var runtime in _runtimes.Values.ToList())
        {
            var running = BrowserProcessProbe.IsRunning(runtime);
            runtime.IsRunning = running;

            if (!running && _connections.IsManaged(runtime.Id))
            {
                if (runtime.State is BrowserRuntimeState.Running or BrowserRuntimeState.JourneyActive or BrowserRuntimeState.DebuggerConnected)
                {
                    runtime.State = BrowserRuntimeState.ManagedStopped;
                    await _connections.StopAsync(runtime.Id, ct);
                    await _ports.ReleaseAsync(runtime.Id, ct);
                    RuntimeGone?.Invoke(this, runtime.Id);
                    _logger.LogInformation("Runtime {Binary} exited", runtime.BinaryName);
                }
            }
            else if (running && _connections.IsManaged(runtime.Id))
            {
                runtime.State = BrowserRuntimeState.JourneyActive;
            }
            await _store.UpsertRuntimeAsync(runtime, ct);
        }
    }

    /// <summary>Garbage-collect ephemeral runtimes that were never used actively.</summary>
    public async Task GarbageCollectEphemeralAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ttl = TimeSpan.FromSeconds(_config.BrowserEphemeralTtlSec);
        var victims = _runtimes.Values.Where(r => !r.IsRunning && !_connections.IsManaged(r.Id) && r.LastSeenAt.HasValue && now - r.LastSeenAt.Value > ttl).ToList();
        foreach (var runtime in victims)
        {
            try
            {
                _logger.LogInformation("Garbage-collecting ephemeral runtime {Binary}", runtime.BinaryName);
                await _ports.ReleaseAsync(runtime.Id, ct);
                await _store.DeleteRuntimeAsync(runtime.Id, ct);
                _runtimes.Remove(runtime.Id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to garbage-collect runtime {Binary}", runtime.BinaryName);
            }
        }
    }

    private static bool IsAutoDebugger(DetectedBrowserRuntime runtime) =>
        runtime.Capabilities[Capability.Debugger] == CapabilityClassification.Automatic;
}
