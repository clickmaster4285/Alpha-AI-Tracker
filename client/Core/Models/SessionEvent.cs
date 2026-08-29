namespace client.Core.Models;

/// <summary>
/// Session event row — single source of truth for ALL client telemetry about
/// OS-level lifecycle events (power, lock/unlock, login/logout, sleep/wake).
///
/// Every event_type string used in the codebase MUST be defined in
/// <see cref="SessionEventTypes"/> below. The contract test (T5 in the
/// finalplan) asserts that the same set of strings exists in
/// server/internal/models/session_event.go and web/src/lib/eventTypes.ts.
/// </summary>
public class SessionEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>One of the <see cref="SessionEventTypes"/> constants. See that class for the
    /// authoritative vocabulary.</summary>
    public string EventType { get; set; } = string.Empty;
    public string OsUsername { get; set; } = string.Empty;
    public DateTime EventAt { get; set; } = DateTime.UtcNow;
    public bool IsSynced { get; set; }
    public string? SyncedAt { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
/// Authoritative vocabulary for session_events.event_type. Every caller that
/// writes a session event MUST go through one of these constants so that:
///   1. Rename/typo drift is caught at compile time.
///   2. The cross-service contract test can assert the C# / Go / TS sets match.
///   3. The dashboard never sees a literal "logon" or "screensaver_on" written
///      by one path and "screen_lock" written by another.
///
/// New event types are added in 3 places: here, server/internal/models/session_event.go
/// (Go mirror), and web/src/lib/eventTypes.ts (web mirror). The contract test greps
/// all three for the literal strings and fails CI if any set is missing an entry.
/// </summary>
public static class SessionEventTypes
{
    // ── Power / OS lifecycle (SystemEventWatcher, A.3) ──
    public const string PowerOn      = "power_on";
    public const string PowerOff     = "power_off";
    public const string Resume       = "resume";

    // ── OS user session (SystemEventWatcher, A.3) ──
    public const string OsLogin      = "os_login";
    public const string OsLogout     = "os_logout";
    public const string ScreenLock   = "screen_lock";
    public const string ScreenUnlock = "screen_unlock";

    // ── Tracker session (LogCollectorService.StartTracking, A.1) ──
    public const string TrackerLogin = "tracker_login";

    // ── UI visibility (App.axaml.cs window.Closing, A.2) ──
    public const string UiHidden     = "ui_hidden";

    // Back-compat alias for rows written before the rename. Reading code
    // (server-side filters, web dashboard) still sees "login" in legacy
    // databases — keep this constant so a server-side JOIN/filter can use it.
    [Obsolete("Use TrackerLogin for new code; this alias only exists for back-compat with rows written before 2026-08-28.")]
    public const string Login        = "login";
}
