using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using client.Core.Models;
using System.Runtime.Versioning;

namespace client.Services.Watchers;

/// <summary>
/// Windows half of <see cref="SystemEventWatcher"/>. Uses the .NET
/// <see cref="SystemEvents"/> wrapper around the Win32 message pump for:
///   - <c>PowerModeChanged</c>  - power status changes (Resume / Suspend)
///   - <c>SessionSwitch</c>     - logon / logoff / lock / unlock / remote connect
///
/// The SystemEvents class is a managed wrapper around the Win32 hidden message
/// window that the framework creates on first use. It works in both GUI and
/// (post-2026-08-18) background mode because .NET's host wires the pump on a
/// dedicated thread.
///
/// TODO A.3.2: a future commit will add WTSRegisterSessionNotification on a
/// custom HWND window for fast Remote Desktop session-change detection. For
/// Phase 1 the SystemEvents path is sufficient.
/// </summary>
public sealed partial class SystemEventWatcher
{
    [SupportedOSPlatform("windows")]
    private PowerModeChangedEventHandler? _winPowerModeChanged;

    [SupportedOSPlatform("windows")]
    private SessionSwitchEventHandler? _winSessionSwitch;

    [SupportedOSPlatform("windows")]
    private void SubscribeWindows()
    {
        _logger.LogInformation("SystemEventWatcher: subscribing to SystemEvents (PowerModeChanged, SessionSwitch)");

        _winPowerModeChanged = (sender, e) =>
        {
            try
            {
                switch (e.Mode)
                {
                    case PowerModes.Resume:
                        _ = SafeRecordAsync(SessionEventTypes.Resume, "systemevents_power_resume", default);
                        break;
                    case PowerModes.Suspend:
                        _ = SafeRecordAsync(SessionEventTypes.PowerOff, "systemevents_power_suspend", default);
                        break;
                    // StatusChange (battery/AC) intentionally ignored.
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SystemEventWatcher: PowerModeChanged handler failed");
            }
        };
        SystemEvents.PowerModeChanged += _winPowerModeChanged;

        _winSessionSwitch = (sender, e) =>
        {
            try
            {
                switch (e.Reason)
                {
                    case SessionSwitchReason.ConsoleConnect:
                    case SessionSwitchReason.RemoteConnect:
                    case SessionSwitchReason.SessionLogon:
                        _ = SafeRecordAsync(SessionEventTypes.OsLogin, "systemevents_session_logon", default);
                        break;
                    case SessionSwitchReason.ConsoleDisconnect:
                    case SessionSwitchReason.RemoteDisconnect:
                    case SessionSwitchReason.SessionLogoff:
                        _ = SafeRecordAsync(SessionEventTypes.OsLogout, "systemevents_session_logoff", default);
                        break;
                    case SessionSwitchReason.SessionLock:
                        _ = SafeRecordAsync(SessionEventTypes.ScreenLock, "systemevents_session_lock", default);
                        break;
                    case SessionSwitchReason.SessionUnlock:
                        _ = SafeRecordAsync(SessionEventTypes.ScreenUnlock, "systemevents_session_unlock", default);
                        break;
                    // SessionRemoteControl and SessionUnknown intentionally ignored.
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SystemEventWatcher: SessionSwitch handler failed");
            }
        };
        SystemEvents.SessionSwitch += _winSessionSwitch;
    }

    /// <summary>
    /// Windows-only: unsubscribe from the .NET SystemEvents. Called from
    /// the base class's StopAsync. Implementation here (rather than the
    /// base) because the delegate fields are private to this partial.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal void UnsubscribeWindows()
    {
        try
        {
            if (_winPowerModeChanged != null)
                SystemEvents.PowerModeChanged -= _winPowerModeChanged;
            if (_winSessionSwitch != null)
                SystemEvents.SessionSwitch -= _winSessionSwitch;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SystemEventWatcher: Windows unsubscribe failed");
        }
    }
}
