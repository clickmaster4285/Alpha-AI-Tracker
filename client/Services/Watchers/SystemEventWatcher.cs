using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;
using client.Storage;

namespace client.Services.Watchers;

/// <summary>
/// Time and Attendance (Phase 1, finalplan section 2.3): cross-platform OS power /
/// lock / login event subscriber. Emits session_events via <see cref="IEventRecorder"/>:
///   power_on / power_off / resume / os_login / os_logout / screen_lock / screen_unlock.
///
/// R2 (No-Hardcoded-Names) compliance: every platform uses the OS's OWN event surface,
/// not a process-name / window-title heuristic:
///   - Linux: D-Bus interfaces (UPower, logind, org.gnome.ScreenSaver). No /dev/input
///     reads (root-required, planv1 gap #1). No Mutter.IdleMonitor (that's A.4).
///   - Windows: Microsoft.Win32.SystemEvents + SystemEvents.SessionSwitch.
///   - macOS: NSWorkspace notifications + CGSessionCopyCurrentDictionary polling.
///
/// All writes funnel through <see cref="IEventRecorder"/> so the 5s dedup window
/// collapses bursts (e.g. UPower + ScreenSaver firing within 1s of each other
/// produce one screen_lock row, not two). All event handlers are wrapped in
/// try/catch - one bad D-Bus signal must never kill the watcher.
///
/// DI ordering (finalplan section 3): registered AFTER ShutdownSentinel +
/// IEventRecorder and AFTER LogCollectorService. Singleton + IHostedService.
/// </summary>
public sealed partial class SystemEventWatcher : BackgroundService
{
    private readonly IEventRecorder _eventRecorder;
    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly ILogger<SystemEventWatcher> _logger;

    // Lock-hysteresis: a flaky screensaver / GDM switcher can fire screen_lock +
    // screen_unlock every few seconds. Finalplan section 2.3 says "30s hysteresis"
    // so we only re-emit a screen_lock after 30s of no screen_unlock.
    private static readonly TimeSpan LockHysteresis = TimeSpan.FromSeconds(30);
    private DateTime _lastScreenLockAt = DateTime.MinValue;
    private DateTime _lastScreenUnlockAt = DateTime.MinValue;
    private readonly object _hysteresisGate = new();

    // macOS-only: track the last known screen-locked state for the CGSession
    // poll (landed with the maccatalyst TFM split — see SystemEventWatcher.MacOS.cs).
    // Kept as a stub so the A.3 placeholder compiles warning-free on all TFMs.

    public SystemEventWatcher(
        IEventRecorder eventRecorder,
        ILogStore store,
        AppConfig config,
        ILogger<SystemEventWatcher> logger)
    {
        _eventRecorder = eventRecorder;
        _store = store;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SystemEventWatcher starting (osDescription={Os})", System.Runtime.InteropServices.RuntimeInformation.OSDescription);

        // First-line guard: also record power_on here. LogCollectorService also
        // records power_on in StartTracking(); the recorder's 5s dedup makes the
        // second write a no-op. Belt-and-suspenders.
        await SafeRecordAsync(SessionEventTypes.PowerOn, "boot", stoppingToken);

        // Per-platform subscription. Each method is best-effort: a failure on one
        // platform (e.g. a D-Bus interface that doesn't exist on KDE) is logged
        // and the watcher continues with whatever DID subscribe.
        try
        {
            if (OperatingSystem.IsLinux())
            {
                await SubscribeLinuxAsync(stoppingToken);
            }
            else if (OperatingSystem.IsWindows())
            {
                SubscribeWindows();
            }
            else if (OperatingSystem.IsMacOS())
            {
                SubscribeMac();
            }
            else
            {
                _logger.LogWarning("SystemEventWatcher: unsupported OS, no subscriptions made");
            }
        }
        catch (Exception ex)
        {
            // A subscription failure must NEVER kill the host. Log and continue.
            _logger.LogError(ex, "SystemEventWatcher: subscription failed (continuing with whatever succeeded)");
        }

        // Park the background task. All events flow through callbacks registered
        // above; ExecuteAsync just needs to stay alive until shutdown.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SystemEventWatcher stopping");

        // Unsubscribe from Windows events. Implementation in the Windows partial
        // (the delegate fields are private to that partial so the base can't
        // touch them directly).
        if (OperatingSystem.IsWindows())
        {
            UnsubscribeWindows();
        }

        // Tear down D-Bus connection + subscriptions (Linux). Implementation in
        // the Linux partial (owns the connection + subscription list).
        if (OperatingSystem.IsLinux())
        {
            UnsubscribeLinux();
        }

        await base.StopAsync(cancellationToken);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Common emission path: dedup, optional hysteresis, write to recorder, and
    // touch the watcher's health watermark (BUG-8 fix from the plan).
    // ════════════════════════════════════════════════════════════════════════

    private async Task SafeRecordAsync(string eventType, string source, CancellationToken ct)
    {
        // Hysteresis for screen_lock / screen_unlock (finalplan section 2.3).
        // We allow unlock at any time (a stuck lock would be visible immediately),
        // but we suppress re-locks within 30s of the previous lock or unlock.
        if (eventType == SessionEventTypes.ScreenLock || eventType == SessionEventTypes.ScreenUnlock)
        {
            lock (_hysteresisGate)
            {
                var now = DateTime.UtcNow;
                if (eventType == SessionEventTypes.ScreenLock &&
                    (now - _lastScreenLockAt) < LockHysteresis &&
                    (now - _lastScreenUnlockAt) < LockHysteresis)
                {
                    _logger.LogDebug("SystemEventWatcher: screen_lock suppressed by hysteresis (source={Source})", source);
                    return;
                }
                if (eventType == SessionEventTypes.ScreenLock) _lastScreenLockAt = now;
                else _lastScreenUnlockAt = now;
            }
        }

        try
        {
            var meta = new Dictionary<string, string> { ["source"] = source };
            await _eventRecorder.RecordAsync(eventType, meta: meta, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SystemEventWatcher: failed to record {EventType}", eventType);
        }

        // Touch the watcher-health watermark (BUG-8). The dashboard's
        // "pipeline healthy" tile can read this and alert if the watcher
        // has been silent for too long (e.g. the D-Bus connection died
        // and nobody noticed). Best-effort: a write failure here is logged
        // and swallowed so the watcher keeps running.
        try
        {
            await _store.SetStatusAsync("ta_last_known_os_event_at", DateTime.UtcNow.ToString("O"), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SystemEventWatcher: failed to touch ta_last_known_os_event_at watermark");
        }
    }
}