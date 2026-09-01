# Time & Attendance — Cross-OS Verification Checklist (finalplan T4 / A.12)

> Live-event checklist for the Phase 1 Time & Attendance client. Run each item on a REAL
> machine (not `dotnet run`) after building the installer (R1 Installer-Parity). A failing
> item blocks the release.
>
> DB inspection: the client DB lives at `~/.local/share/alpha-ai-tracker/data/alpha_tracker.db`
> (Linux) / `%LocalAppData%\AlphaAITracker\data\alpha_tracker.db` (Windows). Use
> `sqlite3 <db> "SELECT event_type, event_at FROM session_events ORDER BY event_at DESC LIMIT 20;"`.

## Build + install (every OS)

- [ ] `cd client && dotnet build` → 0 warnings / 0 errors.
- [ ] `bash publish/build-installer.sh -b <linux|win|mac>` succeeds.
- [ ] Install the artifact on a CLEAN machine (not a dev one).
- [ ] `/usr/share/alpha-ai-tracker/client --print-config` shows `ALPHA_TA_ENABLED=true` (config.enc baked).
- [ ] `journalctl --user -u alpha-ai-tracker -f` (Linux) shows
      `SystemEventWatcher subscribing to UPower, login1, ScreenSaver` and `IdleDetector starting`.
- [ ] SQLite journal mode is WAL: `sqlite3 <db> "PRAGMA journal_mode;"` → `wal`.

## Boot / shutdown (BUG-1)

- [ ] After a clean boot, `session_events` has a `power_on` row with the boot time.
- [ ] After a graceful OS shutdown (Start→Shut down), a `power_off` row exists.
      Power off the machine, boot it, and confirm `power_off` was written before boot.
- [ ] `systemctl stop alpha-ai-tracker` (SIGTERM) writes a `power_off` row.

## Screen lock / unlock (A.3)

- [ ] **Linux:** `loginctl lock-session` → `screen_lock` row; `loginctl unlock-session` → `screen_unlock`.
- [ ] **Linux (GNOME):** a lock via the GNOME lock key also writes `screen_lock` (via GNOME ScreenSaver).
- [ ] **Windows:** `Win+L` → `screen_lock` row; unlock → `screen_unlock`.
- [ ] Rapid lock/unlock (flaky screensaver) writes a bounded number of rows (dedup + 30s hysteresis),
      not 50.

## Sleep / wake (A.3)

- [ ] Suspend the OS → `power_off` (or pause); wake → `resume` row. The process keeps running.

## Idle detection (A.4)

- [ ] Leave the PC idle past `ALPHA_IDLE_THRESHOLD_SEC` (default 120 s) → an `idle_start` row.
- [ ] Resume typing → an `idle_end` row.
- [ ] The `daily_attendance_cache` row for today shows `idle_seconds` growing between the two.

## Schedule mirror (A.6)

- [ ] With the Phase 2 server (`/schedules/me`) present, the pull populates
      `employee_schedule` + `company_holidays`.
- [ ] With the endpoint absent (404), the service logs a Debug no-op and retries every 6 h (no crash).

## Clock skew (A.7)

- [ ] After a run with a reachable server, `local_time_skew` has a row for the server URL.
- [ ] Immediately after waking from sleep, the first skew pass is skipped (post-resume stabilization).

## Window tray (A.3 / BUG-3)

- [ ] Clicking the window X (hide-to-tray, GUI mode) writes a `ui_hidden` row.

## Watcher health watermark (BUG-8)

- [ ] `app_status` has a `ta_last_known_os_event_at` value that updates after real OS events.

## Session-event sync aggregation (A.9 / A.10)

- [ ] `--print-config` shows `EventAggregationWindowSec=300` and `TaMaxLocalRows=50000`.
- [ ] Drive 3+ `screen_lock` events in the same 5-min window → local SQLite has 3 rows
      (`is_synced=0`), but Postgres receives **one** row with `event_count=3` after the bucket closes.
- [ ] `bash test/contract-event-types.sh` passes (13 event types across client/server/web).
- [ ] Optional S6 stress: set `ALPHA_TA_MAX_LOCAL_ROWS=100`, backlog >100 unsynced rows →
      an `old_data_dropped` sentinel appears locally and syncs with `count` = dropped rows.
