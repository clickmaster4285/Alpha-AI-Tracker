# Windows Power-Off Detection Fix Plan

## Problem

On Linux, `power_off` events (shutdown / restart) are detected perfectly via D-Bus `PrepareForShutdown`. On Windows, `power_off` is **never** written during shutdown or restart, while `power_on`, `screen_lock`, and `screen_unlock` all work correctly.

## Root Cause Analysis

### Linux (working)
Linux uses a **two-layer** approach for power-off detection:

1. **Primary (early):** D-Bus `org.freedesktop.login1.Manager / PrepareForShutdown` — fires BEFORE the system begins shutting down, giving the process time to write `power_off` to SQLite synchronously (`.GetAwaiter().GetResult()`).
2. **Fallback:** `ShutdownSentinel` catches `IHostApplicationLifetime.ApplicationStopping`, Avalonia `Exit`, `Ctrl+C`, and `Dispose` — guarantees `power_off` even if D-Bus fails.

### Windows (broken)
Windows `SystemEventWatcher` only subscribes to:
- `SystemEvents.PowerModeChanged` → handles `Resume` and `Suspend` (sleep/hibernate only, NOT shutdown/restart)
- `SystemEvents.SessionSwitch` → handles logon, logoff, lock, unlock (NOT shutdown/restart)

**Missing:** `SystemEvents.SessionEnding` — the .NET event that fires when Windows is shutting down or restarting (`SessionEndReasons.SystemShutdown`). This event is NOT currently subscribed to anywhere in the codebase.

As a result:
- **GUI mode:** Windows sends `WM_QUERYENDSESSION` → Avalonia handles it → host stops → `ApplicationStopping` fires → `ShutdownSentinel` writes `PowerOff`. This path WORKS in GUI mode.
- **Background mode (`--background`):** No top-level window exists. Windows may force-kill the process before `ApplicationStopping` fires. Without `SessionEnding` handler, `power_off` is never written.

## Fix Applied

### File: `client/Services/Watchers/SystemEventWatcher.Windows.cs`

**Added `SystemEvents.SessionEnding` subscription** in `SubscribeWindows()`:
- Fires for both **shutdown** and **restart** (`SessionEndReasons.SystemShutdown`)
- Records `PowerOff` immediately with source `"systemevents_session_ending"`
- Uses fire-and-forget (`_ = SafeRecordAsync(...)`) so Windows doesn't think the app is hung
- `SessionEndReasons.Logoff` is intentionally skipped to avoid duplicating the existing `SessionSwitch` → `os_logout` path

**Added unsubscription** in `UnsubscribeWindows()`:
- Prevents memory leaks when the service stops

**Added private field:**
```csharp
private SessionEndingEventHandler? _winSessionEnding;
```

## Why This Works

- `SystemEvents.SessionEnding` fires for both **shutdown** and **restart** (both carry `SessionEndReasons.SystemShutdown`).
- It fires in both GUI mode and background mode because .NET's `SystemEvents` creates a hidden message-only window that receives `WM_QUERYENDSESSION`.
- The fire-and-forget pattern mirrors the Linux `PrepareForShutdown` handler and returns immediately so Windows doesn't think the app is hung.
- `ShutdownSentinel` remains as the fallback for cases where `SessionEnding` doesn't fire.
- Logoff (`SessionEndReasons.Logoff`) is intentionally skipped to avoid duplicating the existing `SessionSwitch` (SessionLogoff) → `os_logout` path.

## Verification

1. Build: `dotnet build` in `client/` — **passed** (0 warnings, 0 errors)
2. Install and run the app in background mode (`--background`)
3. Trigger Windows shutdown or restart
4. Check DB: `SELECT * FROM session_events WHERE event_type = 'power_off' ORDER BY event_at DESC LIMIT 5;`
5. Confirm a new `power_off` row appears with `source = "systemevents_session_ending"`
6. Confirm `ShutdownSentinel`'s fallback `power_off` (if any) is idempotent (no duplicate rows due to 5s dedup)

## Real-World Test Required

This fix must be verified on a real Windows machine:
1. Build installer: `bash publish/build-installer.sh -b win`
2. Install and run in background mode
3. Trigger Windows shutdown
4. Restart and check DB for `power_off` event
