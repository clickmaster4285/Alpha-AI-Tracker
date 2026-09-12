# Release Notes — v1.1.5

## Overview

v1.1.5 is a major feature release spanning Time & Attendance, Dynamic RBAC, Employee Journey UX, Hours Insights, Windows lifecycle hardening, and a critical orphan-data fix. The release includes 24 files changed on the server, 18 on the web, and 15 on the client, with migrations 024–035 applied.

---

## 1. Time & Attendance (Phase 1) — Client + Server

**Client**
- New `SystemEventWatcher` (cross-platform `BackgroundService`) emits `session_events` via the existing `IEventRecorder`:
  - **Linux**: D-Bus `UPower` (PrepareForSleep), `login1` (Lock/Unlock), `org.gnome.ScreenSaver` (ActiveChanged).
  - **Windows**: `Microsoft.Win32.SystemEvents` PowerModeChanged + SessionSwitch + SessionEnding.
  - **macOS**: NSWorkspace notifications + CGSessionCopyCurrentDictionary polling.
- New `IdleDetector`: OS idle source (Mutter.IdleMonitor on Wayland, X11 XScreenSaver fallback, Windows GetLastInputInfo) polls every 30 s and emits `idle_start` / `idle_end` markers.
- New `ScheduleCacheService`: pulls `GET /api/v1/schedules/me` on login + every 6 h.
- New `LocalTimeSkewService`: client↔server clock skew from HTTP Date header every 15 min.
- New `AttendanceAggregator`: 5-min daily rollup into `daily_attendance_cache` (arrival/departure/active/idle + present|late|off_shift|unknown).
- New `ShutdownSentinel`: writes `power_off` on SIGTERM / Ctrl+C / Avalonia Exit.
- SQLite WAL mode + `synchronous=NORMAL`; a second ReadOnly connection prevents deadlocks from 6 hosted services.
- New tables: `employee_schedule`, `company_holidays`, `daily_attendance_cache`, `local_time_skew`.
- New env knobs: `ALPHA_TA_ENABLED`, `ALPHA_IDLE_*`, `ALPHA_TA_LOCK_HYSTERESIS_SEC`.

**Server**
- Migration `028`: `shifts` gains `timezone`, `holidays` table, aggregate event fields on `session_events`.
- New endpoints: `GET /api/v1/schedules/me`, `GET /api/v1/holidays`, `POST /api/v1/attendance/rollup`.
- Shift timezone defaults to server local time (`DEFAULT_SHIFT_TIMEZONE` env); attendance late rule uses `firstLocal.After(shiftStart + grace)` in the shift’s own IANA zone.
- `AttendanceAggregator` understands `mon`…`sun` keys, company holidays, nullable no-event arrival, `absent` / `half_day` / `off_shift`, schedule overlap, and the union of idle + screen-lock intervals without double-counting.

---

## 2. Dynamic RBAC — Server-Driven Roles / Modules / Submodules

- Migration `025` adds `roles`, `modules`, `submodules`, and `role_submodule_permissions`.
- `users.role_id` is the sole role source of truth; legacy `role` / `is_company_admin` dropped.
- Go seeder `RBACService.SeedCatalog` re-ensures the module/submodule catalog + grants idempotently on every boot.
- New protected endpoints: `GET /modules` (full module→submodule tree) and `/roles` CRUD.
- `/settings/user-management` rebuilt on live `/users` API with infinite scroll, create/edit/delete dialogs, and role assignment.
- Web `RouteGuard` resolves the current pathname to a module and redirects unauthorized access to `/unauthorized`.
- Semantics: absent/null permissions array = fail-open (pre-RBAC sessions keep working); present array = strict allow-list; `company_admin` always full.

---

## 3. Self-Service Profile + User-Management Edit Flow

- New `/settings/profile` page (visible to every authenticated user, no permission gate):
  - **Account** tiles: user id, employee link, role + system badge, department, shift.
  - **Modules you can access**: per-module `grantedCount / submoduleCount` cards with system-admin "all" override.
  - **Update details**: name/email/password with confirm-password + eye toggles; name/email reverse-sync to the linked employee on save.
- `/settings/user-management` is now full CRUD:
  - Per-row **Edit** button deep-links via `?edit=1&userId=…`.
  - Identity fields (`name`, `email`, `employeeId`) are locked in both create and edit modes.
  - Role dropdown excludes the system `company_admin` role.
  - Password + confirm-password with eye toggles; on save, name/email propagate to the linked employee.
  - The system `company_admin` row hides the Edit button and a deep-link guard refuses the dialog open.

---

## 4. Rotating Refresh Tokens — Web Admin Session Hardening

- Migration `026_refresh_tokens.sql` adds `refresh_tokens` (user_id FK CASCADE, UNIQUE `token_hash` = hex(sha256(raw)), expires_at, revoked_at, deleted_at).
- Raw token is a 32-byte random base64url string stored ONLY as its SHA-256 hash; it travels ONLY in the httpOnly `refresh_token` cookie.
- JWT config split: `JWT_ACCESS_EXPIRY=15m` (web admin), `JWT_REFRESH_EXPIRY=720h` (30 days), `JWT_EMPLOYEE_ACCESS_EXPIRY=24h`.
- New public endpoint **POST /auth/refresh**: validates cookie hash → mints replacement access+refresh pair FIRST → revokes old row AFTER (crash-safe rotation).
- Login sets BOTH cookies; logout REVOKES the refresh row server-side before clearing cookies.
- Web `api.ts` request wrapper catches 401s → single-flight POST /auth/refresh → replays original request once → on failure redirects to `/login`.
- `checkAuth` thunk also tries refresh + re-check once when `/auth/check` answers `{authenticated:false}`.

---

## 5. Employee Journey + Device Specs Modules

- The old `/users/[id]` detail page is replaced by nine pages behind a shared `EmployeePage` shell.
- **Employee Journey**:
  - Session Timeline (`useInfiniteQuery` on `GET /app-sessions`, 30/page, IntersectionObserver sentinel, `FocusTime` fg/bg stacked bar).
  - App Usage (server-side per-app aggregate via `GET /app-sessions/usage`; expanded rows show per-session detail via `GET /app-sessions/usage/sessions`).
  - Web Activity (`GET /app-items?itemType=browser_tab`, infinite scroll, search-engine query grouping with expand/collapse).
  - Screenshots / Location Trail placeholders (Location UI gated behind `LOCATION_UI_ENABLED=false`).
- **Device Specs**:
  - Hardware Overview (specs + storage + network + app_status).
  - Installed Software (Applications/Packages tabs with search, `InventoryTable`).
  - Peripherals (plugged/unplugged cards + `DeviceClassIcon`).
  - Permissions.
- New aggregate endpoint `GET /employees/:id/detail` returns the full machine picture in a single response.
- New shared helpers: `lib/format.ts`, `EmptyState`, `FocusTime`, `DeviceClassIcon`, `EmployeeSelector`.

---

## 6. Hours Insights Redesign & Time Calculation Fix

**Critical bug fixed**: unclosed `app_items` with `opened_at` in the past but `closed_at = NULL` were being bounded by the filter’s `from_ts` (midnight today), inflating Today’s total to 19+ hours. The fix:
- `app_items` are now bounded by their parent `app_sessions.ended_at` / `last_sync_at` / `last_activity_at` when `closed_at` is NULL.
- `site_usage` filters on `item_type = 'browser_tab'` only (eliminates duplicate `browser_navigation` counts).
- Total active time is derived from true computer/application runtime, not by adding site durations on top of browser runtime.

**New dual-view UX** (URL-synced via `?view=productivity` or `?view=applications`):
- **"From Productivity"**: stacked-area chart (Productive / Neutral / Unproductive) + breakdown table with productivity type, category, duration, and focus score.
- **"From Application Individually"**: per-application stacked-area chart (top apps sorted by total duration, hourly buckets for ≤3 days, daily for longer) + ranked breakdown table with duration, percentage bar, session count, category, and productivity tag.

---

## 7. Chrome Multi-Tab Duration Fix (3-Layer)

**Root cause**: the a11y reader returned a fresh `WindowKey` per tab; the old `ResolveWindowKey` only collapsed tabs when titles matched; transient "Loading…" states slipped through and opened a session per tab. The web page then summed per-row `endedAt - startedAt`.

**Fix**:
- **Layer 1 — client** (`AccessibilityBrowserTracker.ResolveWindowKey`): two new collapse rules — (a) exact URL match (stronger than title), and (b) fresh-key recency window of `poll×2` seconds that attaches a new same-PID key to the FRESHEST tracked window.
- **Layer 2 — web** (`/employee-journey/apps`): the `useMemo` now recomputes duration as `lastClosedAt - firstOpenedAt` regardless of what the server returns.
- **Layer 3 — server**: new `GET /api/v1/app-sessions/usage` returns one row per `(appDisplayName, processName)` with `MIN(started_at)`, `MAX(COALESCE(ended_at, last_sync_at, started_at))`, `SUM(...)` for `totalDurationSeconds`. Composite index `idx_app_sessions_employee_started_name` covers the WHERE + GROUP BY.

---

## 8. 3-State App-Session Lifecycle (ACTIVE / STALE / CLOSED)

- Migration `031` adds `status TEXT NOT NULL DEFAULT 'ACTIVE'`, `last_activity_at TIMESTAMPTZ`, `last_sync_at TIMESTAMPTZ`, plus 2 indexes.
- **State machine** (`session_lifecycle_sweep.go`, every 1 min):
  - `ACTIVE → STALE` when `last_sync_at < NOW() - SESSION_STALE_AFTER_MINUTES` (default 10).
  - `STALE → CLOSED` when `last_sync_at < NOW() - SESSION_CLOSE_AFTER_HOURS` (default 24).
  - Only CLOSED is terminal; the sweeper freezes `ended_at = COALESCE(last_activity_at, last_sync_at, started_at)` at the moment of CLOSE.
- **Upsert recovery**: when a live client re-uploads any row with `ended_at=NULL`, the `ON CONFLICT` CASE flips STALE/CLOSED back to ACTIVE and clears the premature server-side `ended_at`.
- When the client supplies a non-NULL `ended_at`, the row stays CLOSED with the new value (client’s decision wins).
- **Per-row stagnation sweep** (step 4 of `session_lifecycle_sweep.go`): advances any OFFLINE/STALE row whose OWN `last_sync_at` is ≥ CLOSE_AFTER old — even on a machine that is still alive — so a single abandoned session can never be stranded forever.
- **Migrations**: `034` aligns historical rows that already carried `ended_at` to `CLOSED`; `035` freezes stranded OFFLINE/STALE sessions (`status='CLOSED'`, `ended_at=COALESCE(last_activity_at,last_sync_at,started_at)`) older than 24h.

---

## 9. Orphan App-Items Fix (Bug #9)

**Root cause 1**: the orphan preflight returned only a survivor COUNT and the client’s `SendAsync` never read the response body, so the client marked ALL sent ids `is_synced=1` — including rows the server had refused — permanently losing them.

**Fix**:
- `SyncBatchResponse` now carries `rejectedIds []string` (omitempty: the other 11 sync endpoints’ wire format is untouched; absent field = old mark-all behavior, so new-client↔old-server stays compatible).
- `BulkInsertAppItems` returns the refused item ids.
- `SyncAppItems` surfaces them.
- `SyncService.DrainTableAsync` marks ONLY accepted rows (case-insensitive parse; absent/unparseable body ⇒ mark all, idempotent upserts make that safe).

**Root cause 2**: `app_items` INSERT omitted `synced_at` (migration 008 gave it no DEFAULT) so every first insert stored NULL.

**Fix**: All five sync writers that omitted the column (`app_items`, `hardware_devices`, `permission_status`, `storage_devices`, `location_samples`) now stamp `synced_at = NOW()` inline on INSERT. Migration `033` backfills historical NULLs from `created_at`.

**Poison-row quarantine**: rows refused 3 consecutive passes are quarantined **in-memory** — skipped by the drain until process restart. The SQLite row stays `is_synced=0` (a refused row must never masquerade as synced; retention only purges synced rows, so quarantined rows persist as diagnostic evidence; restart resets the counter and gives self-heal a fresh chance).

---

## 10. Server-Side App-Session Accuracy + Web UX

- `GET /api/v1/app-sessions/usage` (server-side per-app aggregate).
- `GET /api/v1/app-sessions/usage/sessions` (paginated per-app session list for chevron expand).
- Web `/employee-journey/apps` columns: **Application | Sessions | Duration | Last Active | Status**.
- Status badge driven by `hasOpenSession` (green "Running" only when truly ACTIVE).
- Expanded-row Closed cell shows `lastSyncAt` for STALE/OFFLINE.
- Edge webviews folded into the browser group (`msedgewebview2` → `msedge`) so Windows Edge renders as one aggregate.
- Web `/employee-journey/timeline`: STALE/OFFLINE rows show `lastSyncAt` in the Closed column instead of an em-dash.
- Web `/employee-journey/web`: an open tab’s duration now uses `now − openedAt` instead of `0`.
- Search-engine queries (Google, Bing, Yahoo, DuckDuckGo) parsed from URL query params and grouped by query text with expand/collapse dropdown arrows.

---

## 11. Windows Power On/Off + Shutdown Finalizer

- `SystemEventWatcher` → `SessionEnding` now calls `CloseAllOpenSessionsOnShutdownAsync()` (`CloseSessionsAndAppItemsAsync`) so every open session’s `ended_at` is stamped (with `is_synced=0`) in SQLite before the process is killed.
- Linux already had the equivalent `PrepareForShutdown` path.
- Windows `SessionEnding` with `SessionEndReasons.SystemShutdown` is now subscribed (fire-and-forget, mirrors Linux’s synchronous handler); `Logoff` is intentionally skipped to avoid duplicating the existing `SessionSwitch` → `os_logout` path.

---

## 12. URL-Synced Filters + No-Clear Date Modal

- New generic `useUrlQueryState<T>` hook syncs any typed filter record to the browser URL (debounced, preserves unrelated keys, strips empty values).
- New `useUrlActivityFilter` bridges `ActivityFilter` to compact URL keys (`?q=&preset=today|7d|all|custom&from=YYYY-MM-DD&to=YYYY-MM-DD`).
- Wired into 13 pages: employees (search + department), shifts (search), timesheets (employee + from + to), attendance (date + status), employee-journey/timeline (employeeId via shell), employee-journey/apps + web (search + date preset), device-specs/software (tab + per-tab search), configuration/apps + websites (search + type + category + status), logs/comprehensive (employee + search + page), settings/user-management (search).
- `ActivityFilters` lost its search Clear (X) button — clearing now means select "All time" or empty-the-input.
- Every page using `useSearchParams` is now wrapped in `<Suspense>`.

---

## 13. Attendance Late/Present Timezone Fix

- Migration `028` defaulted `shifts.timezone` to `UTC` while session events and admins sit in local time (e.g. PKT +05) — a 9:08 arrival looked Present in the UI but was compared against 09:00 UTC on the server.
- New `DEFAULT_SHIFT_TIMEZONE` env (`server/.env.example`): on boot, `ShiftService.ApplyDefaultTimezone` rewrites every active shift still on `UTC`.
- Web `/shifts` new-shift form defaults to `Intl.DateTimeFormat().resolvedOptions().timeZone`.
- Web `/attendance` + `/timesheets` format first/last active via `formatDateTimeInZone(iso, record.timezone)`.

---

## 14. Configuration Pages — Applications + Websites Classification

- The old static `apps/page.tsx` is deleted.
- Migration `023` adds `monitoring_types` (seeded Productive / Unproductive / Neutral), `monitoring_categories`, classification columns on `installed_applications`, and `monitoring_sites` (auto-synced from distinct `app_items.domain`).
- New `MonitoringRepo` / `MonitoringService` / `MonitoringHandler` + protected `/api/v1/monitoring/{types,categories,apps,websites}` routes.
- Web: **Applications** and **Websites** use the shared `ClassifiedItemsTable` component (infinite scroll, debounced search, Type/Category/unclassified filters, inline per-row selects).
- **Categories & Types** is a two-tab CRUD (cards + dialogs, color picker for types, kind select for categories).

---

## 15. Employee List — Server-Side Infinite Scroll + Excel Import/Export

- Employees page replaced Previous/Next buttons with `useInfiniteQuery` + IntersectionObserver sentinel (10/page).
- **Export** downloads `employees-<date>.xlsx` (Employee ID / Name / Email / Department / Shift).
- **Import** reads `.xlsx/.xls/.csv` client-side, normalizes rows, and posts to `POST /api/v1/employees/import`. Server imports in ONE transaction: departments are get-or-created by name, employees are upserted by exact `employee_id`. Per-row outcomes returned to the UI.

---

## 16. Browser Registry — All Hardcoded Names Removed

- New `IBrowserRegistry` interface + `BrowserRegistry` implementation source browser classification from `installed_applications.is_browser` (set via `.desktop` `Categories=WebBrowser`, Windows registry `URLAssociations`, macOS `CFBundleURLSchemes`) and caches for 5 minutes.
- Every reader, tracker, history fallback, session label resolver, and the main collector loop now resolves browsers through the registry instead of hardcoded name lists.
- `StripBrowserSuffix` is now dynamic — it strips ` - {appDisplayName}` using the registry’s display name.
- The Linux embedded Python probe’s `BROWSER_HINTS` tuple is deleted; the probe uses `_browser_exes` (already built from `.desktop` files) for both AT-SPI browser detection and Firefox sessionstore discovery.

---

## 17. Embedded-Webview Journeys — Zero Hardcoded App Names

- A window carries web content iff its accessibility tree contains a `DOCUMENT_WEB` node (AT-SPI role 95) whose `DocURL` is an http(s) URL.
- App chrome excludes itself by scheme (`vscode-webview://`, `file://`, `about:` never match).
- The host app (process name, e.g. `code`) rides in metadata `"source":"webview"` so the web dashboard shows it as the source badge.
- The tracker **caches webview windows and re-emits them every poll** so sessions stay stable and the missing-window close never fires between scans.

---

## 18. Windows Installed-Software + Hardware Detection Overhaul

- **Start Menu .lnk** is the Windows analog of Linux `.desktop`; `InstalledAppDetector.ScanStartMenuShortcuts` enumerates .lnk files (user + common) via WScript.Shell and resolves each target exe.
- **Registry scan** reads all 3 Uninstall nodes (HKLM + WOW6432Node + HKCU); `PackageDetector.ScanRegistrySoftware` captures dev runtimes/tools and drivers.
- **winget is the source of truth** for packages; MSI-installed dev tools + drivers now appear in `installed_packages`.
- **PE Subsystem** (`IMAGE_SUBSYSTEM_WINDOWS_GUI` vs `IMAGE_SUBSYSTEM_WINDOWS_CUI`) decides app vs package structurally — no name lists.
- **C:\Windows tree** exclusion: anything under the Windows dir is an OS component.
- All Windows name blocklists (`WindowsNonAppProcesses`, `JunkRegistryNamePatterns`, `IsDevToolRegistryName`, `ClassifyRegistryPackage`, `CliKnownPackages`) are DELETED.
- `HardwareDeviceWatcherService` is no longer Linux-only: new Windows PnP implementation polls `Get-CimInstance Win32_PnPEntity` every 30 s.

---

## 19. Sync Engine Decoupled — Collection Never Blocks on the Network

- New dedicated `SyncService` (BackgroundService) drains unsent SQLite rows on its own loop.
- Chunks bounded by BOTH row count (`ALPHA_SYNC_MAX_ROWS`, 1000) and serialized payload bytes (`ALPHA_SYNC_MAX_BYTES`, ~1 MB).
- ~150 ms politeness pause between chunks; 5-min per-pass budget (`ALPHA_SYNC_MAX_DURATION_SEC`).
- Exponential backoff on failure (5 s → 10 s → … → `ALPHA_SYNC_BACKOFF_MAX_SEC` 5 min).
- Request bodies are gzip-compressed; server side adds `middleware.BodyLimit("20M")` + `middleware.Decompress()`.
- SQLite mark-sent is now batched (`UPDATE … WHERE id IN (…)`, 400 ids/statement).
- New env knobs: `ALPHA_SYNC_INTERVAL_SEC / MAX_ROWS / MAX_BYTES / CHUNK_DELAY_MS / MAX_DURATION_SEC / BACKOFF_MAX_SEC / COMPRESSION`.

---

## 20. Headless Boot + Six-Page GUI + Runtime Branding

- Boot is now fully headless (`--background`): tracking starts at boot via `ExecuteAsync` calling `StartTracking()` right after `RefreshEmployeeInfo`.
- The GUI is strictly login-only — it cannot start or stop tracking.
- GUI rebuilt as six pages under `client/Views/Pages/`: Splash, Login, PermissionSetup, Dashboard, System Specs, Installed Applications.
- `Core/AppInfo.cs` makes every visible brand string and version derive from `APP_IDENTIFIERS` + `VERSION` at runtime — no XAML or C# literal names anywhere.
- New `SingleInstanceService` named-pipe activation: a second user launch signals the running instance to raise its window; `--background`/`--minimized` relaunches exit quietly.

---

## 21. Self-Update from GitHub Releases

- New `AppUpdateService` (ObservableObject + IHostedService singleton) checks `https://api.github.com/repos/{repo}/releases/latest`, normalizes the tag, picks the platform installer asset, and compares against `AppInfo.Version`.
- Quiet auto-check loop every 30 min (configurable via `ALPHA_UPDATE_AUTO_CHECK_HOURS`, default 24 h).
- With `ALPHA_UPDATE_AUTO_INSTALL=true` (default) it auto-downloads and installs with no click.
- GUI top bar gains **Check updates**, **Update to vX.Y.Z**, **Restart to apply** buttons + update banner.
- Config: new `ALPHA_UPDATE_REPO / ENABLED / AUTO_CHECK_HOURS / AUTO_INSTALL` keys in `.env` + `.env.example` + `AppConfig`.

---

## 22. Client Retention + 4 New Server Sync Surfaces

- The client now DELETES synced data the server already has: `app_items` / `app_sessions` older than `ALPHA_SYNC_RETENTION_HOURS` (24 h default; OPEN sessions are NEVER deleted).
- Four previously local-only tables now sync to the server (migration `017`): `app_status`, `hardware_devices`, `permission_status`, `storage_devices`.
- `permission_status` duplication fixed: keyed on the stable `"{platform}_{method}"` with `ON CONFLICT(check_id) DO UPDATE` (one row per permission method).
- Instant sync on login: `SyncService.RequestImmediateSync()` wakes the drain pass so the full machine picture lands on the server the moment an employee logs in.

---

## 23. Instant Sync on Login

- `SyncService` gained a wake-up signal (`SemaphoreSlim(0,1)` released by the caller; the inter-pass wait is now `WaitAsync(wait, ct)` so a release ends the wait at once).
- `MainViewModel` injects the singleton `SyncService` and fires it in BOTH login paths: `LoginAsync` and `InitializeAsync` (session restore on launch).
- The instant pass drains every unsent table — `device_hardware_info`, `installed_applications`, `installed_packages`, `network_info`, `storage_devices`, `hardware_devices`, `session_events`, `permission_status`, `app_status`, `app_sessions`, `app_items`.

---

## 24. Foreground / Background Focus Time Per Session

- The collector already knew the OS foreground PID each cycle but never persisted it.
- Client: new `foreground_seconds` / `background_seconds` on SQLite `app_sessions` (idempotent MigrateSql ALTERs), accumulated per cycle for every open session, flushed every 10 cycles + on close with `is_synced=0`.
- Server: migration `020` adds the two columns, model/DTO/`SyncAppSessions`/`BulkInsertAppSessions` upsert + `ListAppSessions` SELECT.
- Web: Activity tab rebuilt with server-side pagination + infinite scroll and columns **Application, Status, Opened, Closed, Duration, Foreground/Background** (green/gray stacked bar + `fg / bg` readout).

---

## 25. Employee Disconnect Removed

- The **Disconnect** button was removed from the client nav rail.
- `LogoutCommand` / `LogoutAsync` and `LogCollectorService.StopTracking()` are gone from the client.
- Server endpoint `POST /api/v1/auth/employee-disconnect` (handler + DTO + route) was deleted.
- Employees can no longer disconnect themselves; the client tracks until the process stops.

---

## 26. File Explorer Journey Tracking

- Three watchers: `ATSPIEventWatcher` (D-Bus AT-SPI focus/window events), `FileSystemEventWatcher` (FileSystemWatcher on 7 user directories), `RecentFilesWatcher` (XBEL monitor).
- `EventCoordinator` deduplicates (3 s), correlates (500 ms), normalizes raw→business events.
- `JourneyEngine` resolves `AppSession`, creates `AppItem` rows with 9 journey fields.
- Windows: new `WindowsExplorerWatcher` polls the shell’s registry via Shell COM (`Shell.Application → Windows()`) and reads each Explorer window’s `file://` `LocationURL`.

---

## 27. Browser Journey Overhaul — All Browsers + Incognito

- `LinuxAtSpiBrowserReader` rewritten to merge THREE sources — AT-SPI (Chrome & co.), Firefox sessionstore (`recovery.jsonlz4` / `sessionstore.jsonlz4`, decompressed by an embedded pure-python LZ4 block decoder), and the WM window list.
- `AccessibilityBrowserTracker.ResolveWindowKey` now re-keys by page-title match or single-window count guard — stealing is impossible.
- `ALPHA_BROWSER_CAPTURE_INCOGNITO=true` (was false).
- Main `LogCollectorService` skips browsers when browser tracking is enabled (no more double sessions per window).
- New `BrowserHistoryReader` reads each browser’s own profile history database while the browser runs (Chrome `History`, Firefox `places.sqlite`) — torn snapshots can’t corrupt anything.
- URL-less title changes no longer rotate tabs until history catches up.
- Per-page `browser_tab` records: each page visit CLOSES the previous tab root and OPENS a fresh `browser_tab` record.

---

## 28. Private/Incognito Window URLs Captured

- Firefox: `LinuxAtSpiBrowserReader` now reads the `DOCUMENT_WEB` (role 95) node’s `DocURL` attribute — the EXACT private-window URL.
- Chrome 136+: the a11y tree has ZERO children below the window frame in basic mode; the tree is only built when Chrome is launched with `--force-renderer-accessibility`. Applied a user-level `~/.local/share/applications/google-chrome.desktop` with the flag on ALL Exec lines.

---

## 29. Windows Auto-Update Fix

- `installer-windows.iss` `KillRunningInstance` ran `taskkill /F /IM client.exe /T` — the `/T` is a TREE-kill. The self-updater launches the installer as a DESCENDANT of the running app, so that tree-kill terminated the updater’s OWN cmd script AND the installer itself.
- Fix: `/T` removed from `KillRunningInstance`; `AppUpdateService.InstallAsync` (Windows) rewritten so the updater does NOT depend on the installer killing the app — it writes a detached `.cmd` to `%TEMP%` that polls `tasklist` until the app exits, launches the silent installer, waits for it to finish, and relaunches `{exe} --restart`.

---

## 30. Downloaded Installers Cleanup

- `CleanupUpdatesDirectoryAsync` force-deletes every file in `updates/` then the folder itself.
- Wired into three places: Linux (after successful `pkexec dpkg -i`), Windows (inside the detached PowerShell update script), and startup sweep (prunes the folder on every boot).

---

## 31. Focus Totals Fix

- The periodic flush was OVERWRITING the DB row with the in-memory delta instead of accumulating.
- Fix: the flush is now ADDITIVE (`foreground_seconds = COALESCE(foreground_seconds, 0) + $foreground_seconds`).
- The main loop now calls `FlushSessionFocusAsync` every 2 cycles (~60 s at the default 30 s interval).
- `SyncService` failure backoff is now **capped at 60 s**.

---

## 32. Foreground/Background Focus Time Fix

- **Cause 1 — Linux/Wayland foreground detection was completely dead**: at-spi2-core ≥ 2.50 returns a PACKED 64-bit bitmask instead of a list of state ids. `IsAtSpiActiveState` / `is_active_window` decode BOTH formats and test ACTIVE(1)/FOCUSED(12).
- **Cause 2 — browser sessions never earned focus time at all**: Browsers are owned by `AccessibilityBrowserTracker` (excluded from the main loop’s `resolvedLogs`), and the tracker never wrote foreground/background. Fix: new `AccessibilitySnapshot.IsActive` set per-platform and the tracker now accumulates the poll interval into `foreground_seconds` / `background_seconds` per open window every poll.

---

## 33. Duplicate Linux Lock/Unlock Events Fixed

- Root cause: a manual desktop launch and the systemd user service ran concurrently against the same SQLite database.
- `SingleInstanceService.MutexName` now uses .NET’s machine-wide `Global\` namespace with `AppInfo.AppMutex` from `APP_IDENTIFIERS`.
- `Program.cs` consumes it for all launch modes.

---

## 34. LocalStorage Mock Data Removed

- `web/src/lib/store.ts` (the 328-line mock factory) is deleted, as is the legacy localStorage permission-matrix editor.
- Seven no-backend pages became honest empty states.
- `/dashboard` is fully live-API: Total Employees (+ tracked/untracked split), Departments count, App Sessions ·24h, Web Pages ·24h.
- `app/page.tsx` runs a one-time sweep that purges every orphaned `alpha_ai_tracker_*` key from localStorage.

---

## 35. Employee List Infinite Scroll + Excel Import/Export

- Employees page replaced Previous/Next buttons with `useInfiniteQuery` + IntersectionObserver sentinel (10/page).
- **Export** downloads `employees-<date>.xlsx`.
- **Import** reads `.xlsx/.xls/.csv` client-side, normalizes rows, and posts to `POST /api/v1/employees/import`. Server imports in ONE transaction.

---

## 36. Web UI for OFFLINE Filter + Productivity Filters

- Web UI for OFFLINE filter added.
- New filter status: `all`, `classified`, `unclassified`.
- Employee selection + search/date filters now persist together in the URL (no more deselection on filter change).
- Attendance page deep-links: clicking an employee name on `/attendance` deep-links to `/timesheets?employeeId=<uuid>&from=<date>&to=<date>`.

---

## 37. Server-Side Date Filters on App Sessions + App Items

- `GET /app-sessions` and `GET /app-items` now accept `dateFrom`/`dateTo` (RFC3339 or date-only).
- App-items search also matches `url`/`domain`.

---

## 38. Client Version Column on Employees Page

- Employees page now shows a **Version** column sourced from `employee_devices.client_version`.
- Added `clientVersion` to `EmployeeResponse` DTO + employee repo/service.

---

## 39. Histogram Shows Individual Application Usage

- When **Application** mode is selected in Hours Insights, the histogram now shows per-application usage time/hour.
- Most-usage applications are sorted to the top.

---

## 40. Total Session Time Moved to Server Side

- `GET /app-sessions/usage` now computes `totalDurationSeconds` server-side.
- Web `/employee-journey/apps` consumes the server-computed value directly.

---

## 41. Critical Productivity/Unproductivity Fix

- Bounded unclosed `app_items` to their parent session’s end time.
- Corrected `site_usage` to filter on `item_type = 'browser_tab'` only.
- Total active time is derived from true computer/application runtime, not by adding site visit durations on top of browser application durations.

---

## 42. Windows Power On/Off Perfectly Running

- `SystemEventWatcher.Windows.cs` now subscribes to `SystemEvents.SessionEnding` (fire-and-forget).
- `SessionEnding` with `SessionEndReasons.SystemShutdown` calls `CloseAllOpenSessionsOnShutdownAsync()` so every open session’s `ended_at` is stamped before the process is killed.
- Linux already had the equivalent `PrepareForShutdown` path.

---

## 43. Snap Firefox AppArmor AT-SPI Fix

- New `publish/firefox-a11y-apparmor.sh` loads a surgical copy of the snap profile adding ONE rule — `dbus (receive)` — that lets the AT-SPI bridge call INTO Firefox while the sandbox stays fully enforcing.
- Installs a systemd oneshot (`alpha-ai-firefox-a11y.service`, enabled) that re-applies the override at boot and after `snap refresh firefox`.

---

## 44. Structural Process Name Resolution (Flatpak / snap)

- `resolve_app_name(pid)` in the Python probe checks `FLATPAK_ID` in `/proc/<pid>/environ` and `snap` path in `/proc/<pid>/exe`.
- Falls back to `/proc/comm` for native apps.
- The resolved name is used for browser detection: new structural browser detection scans `.desktop` files for `Categories=WebBrowser`.

---

## 45. Download Installer Cleanup

- `CleanupUpdatesDirectoryAsync` force-deletes every file in `updates/` then the folder itself.
- Wired into Linux (after successful install), Windows (inside detached update script), and startup sweep.

---

## 46. Headless Boot — Tracking Starts Without GUI

- `ExecuteAsync` now calls `StartTracking()` right after `RefreshEmployeeInfo` when persisted employee credentials exist.
- The GUI is strictly login-only — opening/closing it never starts or stops tracking.
- Background mode now waits with `host.WaitForShutdownAsync()` instead of an uncancellable infinite delay.

---

## 47. Single-Instance Activation + Tray UX

- A second launch now restores the running window (`client/SingleInstance.cs` named-event signaling).
- The tray menu gains **Quit**.
- Tray-less desktops (no StatusNotifierWatcher) quit on window-close instead of stranding an invisible process.

---

## 48. App Display Name from Installed Applications

- `AppDisplayName` now uses the real app name from `installed_applications.app_name` (e.g., "Visual Studio Code"), not the process name ("code") or window title.

---

## 49. Windows Journey Fixes — Real Incognito Flag + Junk-Data Sweep

- `WindowsUiaBrowserReader` now also scans the window’s accessibility tree for the dedicated "Incognito"/"InPrivate" toggle button Chrome and Edge expose.
- `SearchApp` was treated as a BROWSER because the `"arc"` hint substring-matched `SearchApp` — `IsBrowserProcess` now matches hints as STANDALONE WORDS only.
- Runtime auto-registration disabled on Windows — the factory for every junk `installed_applications` row.
- New boot sweep `CleanupWindowsJunkSessionsAsync` deletes empty-desktop_id app rows and closes their sessions.

---

## 50. Cross-Platform Analyzer Safety

- All platform-specific method bodies guard with `OperatingSystem.IsWindows/Linux/MacOS()` early-return.
- Narrowly scoped `#pragma warning disable CA1416` allowed only in the platform file and only when every entry point has the explicit runtime guard.

---

## 51. Branding Single-Source Rule

- `Core/AppInfo.cs` reads `APP_IDENTIFIERS` + `VERSION` at runtime — no XAML or C# literal names anywhere.
- Editing either file re-brands both the app and the installers.

---

## 52. Installer Parity Rule (Codified)

- Every feature/modification must ALSO be wired into the installer functionality and verified from an installed build.
- `dotnet run` success is NOT a valid release test.

---

## Bug Fixes Summary

| # | Issue | Fix |
|---|-------|-----|
| 1 | Orphan `app_items` silently lost | Client reads `rejectedIds` + `missingSessionIds`; server preflights parent session IDs |
| 2 | Chrome multi-tab duration wrong | 3-layer fix: client collapse rules, server usage aggregate, web `lastClosed - firstOpened` |
| 3 | OFFLINE/STALE sessions labeled "Running" | `has_open_session = BOOL_OR(status='ACTIVE' AND ended_at IS NULL)` |
| 4 | Windows shutdown never emitted `power_off` | `SessionEnding` → `CloseAllOpenSessionsOnShutdownAsync()` |
| 5 | Focus totals frozen at 0 | Additive flush SQL; foreground/background now accumulated per poll |
| 6 | App-session tracking not starting headlessly | `ExecuteAsync` calls `StartTracking()` after `RefreshEmployeeInfo` |
| 7 | Journey noise flood (AppData churn) | `FileSystemEventWatcher` excludes appdata/programdata/program files trees |
| 8 | Duplicate Linux lock/unlock events | Machine-wide `Global\` mutex |
| 9 | Unclosed sessions duration runaway | Bounded to parent session `ended_at` / `last_sync_at` / `last_activity_at` |
| 10 | Windows software detection hardcoded | PE Subsystem + C:\Windows tree + registry flags + winget |
| 11 | Windows installer tree-kills itself | `/T` removed from `KillRunningInstance`; detached `.cmd` waits then installs |
| 12 | Stale installers linger in `updates/` | `CleanupUpdatesDirectoryAsync` after install + startup sweep |
| 13 | Snap Firefox invisible to AT-SPI | Surgical AppArmor override |
| 14 | Flatpak/snap browsers show proxy name | `resolve_app_name(pid)` walks PPID chain + checks `FLATPAK_ID` / `snap` path |
| 15 | Employee disconnect breaks tracking | Disconnect button + endpoint removed; client tracks until process stops |
| 16 | Attendance late rule in wrong timezone | Shift timezone defaults to server local time; web formats in shift’s IANA zone |
| 17 | URL-synced filters lose employee selection | Unified URL state via `useUrlQueryState` |
| 18 | Productivity total includes browser runtime | Site usage bounded by parent session; `item_type = 'browser_tab'` only |
| 19 | Windows inventory shows CLI tools as apps | PE Subsystem gate + C:\Windows tree + Start Menu `.lnk` presence |
| 20 | `dpkg-query` startup noise | Format string double-quoted so tab/newline escapes reach dpkg-query as one argument |

---

## Verification

- `go build` / `go vet` clean
- `npx tsc --noEmit` clean
- `next build` passes
- `dotnet build` 0 warnings / 0 errors

---

## Deploy Sequence

1. **Server first** — applies migrations 024–035 on startup.
2. **Web** — rebuild and deploy.
3. **Client** — ships in the next installer build (Windows shutdown path, AppArmor snap override, and headless boot require a new installer).

---

## Known Gaps / Follow-ups

- Grafana dashboard/alert provisioning is out-of-repo; the SQL alerts above are the runbook target.
- A Windows power-off smoke test (real shutdown) is the remaining client verification.
- Server-side permission enforcement (RBAC grants) is still not implemented in API middleware.
- No rate limiting on login or sync endpoints.
- No Redis fallback for employee secret generation/validation.
