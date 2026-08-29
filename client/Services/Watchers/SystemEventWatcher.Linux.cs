using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;
using client.Core.Models;

namespace client.Services.Watchers;

/// <summary>
/// Linux half of <see cref="SystemEventWatcher"/>. Subscribes to the OS's own
/// D-Bus system-bus signals for power, sleep, lock, and login events. NEVER
/// reads /dev/input/* (root-required, planv1 gap #1).
///
/// Three signal sources, best-to-worst availability:
///   1. org.freedesktop.UPower / PrepareForSleep(bool) - PRIMARY power/sleep.
///   2. org.freedesktop.login1.Session / Lock + Unlock - screen lock fallback.
///   3. org.gnome.ScreenSaver / ActiveChanged(bool)    - GNOME-only tertiary.
///
/// Tmds.DBus.Protocol API notes (0.94.2):
///   - sender is a nullable string (service name), NOT a Sender object.
///   - bool values read via Reader.ReadBool().
///   - Signals with no body use the non-generic WatchSignalAsync.
/// </summary>
public sealed partial class SystemEventWatcher
{
    private DBusConnection? _dbusConnection;
    private readonly List<IDisposable> _dbusSubscriptions = new();

    private async Task SubscribeLinuxAsync(CancellationToken ct)
    {
        _logger.LogInformation("SystemEventWatcher: subscribing to UPower, login1, ScreenSaver");

        DBusConnection conn;
        try
        {
            conn = new DBusConnection(DBusAddress.System!);
            await conn.ConnectAsync();
            _dbusConnection = conn;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SystemEventWatcher: D-Bus system bus connection failed");
            return;
        }

        // ── UPower: PrepareForSleep(true)=sleeping, (false)=resumed ──
        try
        {
            var sub = await conn.WatchSignalAsync<bool>(
                sender: "org.freedesktop.UPower",
                path: "/org/freedesktop/UPower",
                @interface: "org.freedesktop.UPower",
                signal: "PrepareForSleep",
                reader: (Message m, object? s) => m.GetBodyReader().ReadBool(),
                handler: (Notification<bool> n) =>
                {
                    if (n.IsCompletion) return;
                    try
                    {
                        _ = SafeRecordAsync(
                            n.Value ? SessionEventTypes.PowerOff : SessionEventTypes.Resume,
                            "upower", default);
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "UPower handler failed"); }
                },
                null,
                ObserverFlags.None,
                state: null);
            _dbusSubscriptions.Add(sub);
            _logger.LogDebug("SystemEventWatcher: UPower OK");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UPower subscription failed");
        }

        // ── login1: Lock / Unlock (no body → non-generic watcher) ──
        try
        {
            var lockSub = await conn.WatchSignalAsync(
                sender: "org.freedesktop.login1",
                path: null,
                @interface: "org.freedesktop.login1.Session",
                signal: "Lock",
                handler: (Notification n) =>
                {
                    if (n.IsCompletion) return;
                    try { _ = SafeRecordAsync(SessionEventTypes.ScreenLock, "login1", default); }
                    catch (Exception ex) { _logger.LogWarning(ex, "login1 Lock handler failed"); }
                },
                null,
                ObserverFlags.None,
                state: null);
            _dbusSubscriptions.Add(lockSub);

            var unlockSub = await conn.WatchSignalAsync(
                sender: "org.freedesktop.login1",
                path: null,
                @interface: "org.freedesktop.login1.Session",
                signal: "Unlock",
                handler: (Notification n) =>
                {
                    if (n.IsCompletion) return;
                    try { _ = SafeRecordAsync(SessionEventTypes.ScreenUnlock, "login1", default); }
                    catch (Exception ex) { _logger.LogWarning(ex, "login1 Unlock handler failed"); }
                },
                null,
                ObserverFlags.None,
                state: null);
            _dbusSubscriptions.Add(unlockSub);
            _logger.LogDebug("SystemEventWatcher: login1 OK");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "login1 subscription failed");
        }

        // ── GNOME ScreenSaver: ActiveChanged(bool) - best-effort, GNOME only ──
        try
        {
            var sub = await conn.WatchSignalAsync<bool>(
                sender: "org.gnome.ScreenSaver",
                path: "/org/gnome/ScreenSaver",
                @interface: "org.gnome.ScreenSaver",
                signal: "ActiveChanged",
                reader: (Message m, object? s) => m.GetBodyReader().ReadBool(),
                handler: (Notification<bool> n) =>
                {
                    if (n.IsCompletion) return;
                    try
                    {
                        _ = SafeRecordAsync(
                            n.Value ? SessionEventTypes.ScreenLock : SessionEventTypes.ScreenUnlock,
                            "gnome_screensaver", default);
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "ScreenSaver handler failed"); }
                },
                null,
                ObserverFlags.None,
                state: null);
            _dbusSubscriptions.Add(sub);
            _logger.LogDebug("SystemEventWatcher: GNOME ScreenSaver OK");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GNOME ScreenSaver subscription skipped (probably non-GNOME)");
        }
    }

    internal void UnsubscribeLinux()
    {
        foreach (var sub in _dbusSubscriptions)
        {
            try { sub.Dispose(); } catch { /* best-effort */ }
        }
        _dbusSubscriptions.Clear();

        if (_dbusConnection != null)
        {
            try { _dbusConnection.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "D-Bus dispose failed"); }
            _dbusConnection = null;
        }
    }
}
