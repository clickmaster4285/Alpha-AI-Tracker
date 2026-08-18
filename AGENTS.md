# Alpha AI Tracker — Project Map

> **Last audited:** 2026-08-18
> **Changelog:**
>
> - 2026-08-18: **Web: Employees list now uses server-side infinite scroll — Next/Previous buttons banned; rule codified in the docs.**
>   The Employees page's `page` state + Previous/Next buttons were replaced with the same
>   `useInfiniteQuery` + IntersectionObserver-sentinel pattern as the Session Timeline / Web Activity
>   journey pages (`employeesApi.list` unchanged — server pagination via `page`/`perPage`, 10/page;
>   filters change the query key and restart at page 1; "Loading more…" inline + "Showing all N" footer).
>   **New mandatory rule — *Web Infinite-Scroll Rule*:** every web list/table page MUST use server-side
>   infinite scroll; Next/Previous buttons are forbidden. Codified in `AGENTS.md` §6 (conventions table +
>   rule subsection), `web/ARCHITECTURE.md` §4 (Data Fetching Strategy table + rule subsection), and
>   `WORKFLOW.md` §1 (Web note). Verified: `tsc --noEmit` clean, `next build` passes.
> - 2026-08-18: **Web: Employees Excel import/export.** The Employees page gains **Import** / **Export**
>   buttons. **Export** downloads `employees-<date>.xlsx` (Employee ID / Name / Email / Department / Shift)
>   from the new `GET /api/v1/employees/export` (all non-deleted employees, `EmployeeRepo.ListAll`).
>   **Import** reads an `.xlsx/.xls/.csv` in the browser (`xlsx` client-side), extracts ONLY the columns
>   whose headers match `userid|user_id|user id|employeeid|employee_id|employee id|name|employee name|
>   username|email|department` (case/whitespace-insensitive — the header map in `employees/page.tsx`),
>   and posts normalized rows to `POST /api/v1/employees/import`. The server imports in ONE transaction
>   (`EmployeeRepo.Import`): departments are **get-or-created by name** (created first, then attached via
>   `department_id`; soft-deleted depts are revived), and each employee is **upserted by the exact
>   employee_id from the spreadsheet** (the `RETURNING (xmax = 0)` trick distinguishes insert vs update;
>   soft-deleted employees revive; email may only be reused by the same employee_id; empty department
>   → Engineering, empty shift → Day). Per-row outcomes (`imported`/`updated`/`skipped` + reason) return
>   to the UI as a result dialog with a skipped-rows table. `employee_id` must be ≤20 chars and unique in
>   the file. Verified: `go build`/`go vet` clean, `tsc --noEmit` clean, `next build` passes. Server + web
>   only — no client/installer change.
> - 2026-08-18: **Embedded-webview journeys — sites opened INSIDE apps (VS Code Simple Browser, Slack, Teams, any Electron/embedded browser) now tracked as web activity, with ZERO hardcoded app names.**
>   Previously only real browser processes were tracked (a hardcoded `BrowserProcessHints` gate in the
>   readers), so a website opened inside VS Code's Simple Browser never reached `/employee-journey/web`.
>   The gate is replaced with **structural detection — the No-Hardcoded-Names Rule applied**: a window
>   carries web content iff its accessibility tree contains a DOCUMENT_WEB node (AT-SPI role 95) whose
>   DocURL is an **http(s)** URL. App chrome excludes itself by scheme (`vscode-webview://`, `file://`,
>   `about:` never match), so no product-name list is needed anywhere — any app with an embedded http
>   document is captured, and the HOST APP (process name, e.g. `code`) rides in metadata `"source":"webview"`
>   so the web dashboard shows it as the source badge. Linux (`LinuxAtSpiBrowserReader`): the probe now
>   scans ALL a11y apps (structurally skipping `--type=` Chromium/Electron child processes and `--headless`)
>   — browsers keep the full path (omnibox + DocURL + incognito), non-browsers must PROVE an http DocURL
>   or they are never emitted. The expensive non-browser walk runs every 5th poll (~15s) with a 400-node
>   budget, and the reader **caches webview windows and re-emits them every poll** so sessions stay stable,
>   focus accounting works, and the tracker's time-based missing-window close (5×interval) never fires
>   between scans (cache entries expire ~60s after a rescan stops producing them). Windows
>   (`WindowsUiaBrowserReader`): non-browser windows are scanned for a descendant Document/Edit whose
>   Value/Name is an http(s) URL (the UIA analog of DocURL) — Electron webviews expose this on demand;
>   browsers unchanged. Tracker: `HydrateTrackedWindowsAsync` now hydrates browser_tab-rooted sessions
>   (webview sessions survive fast relaunches), metadata `source` is `webview` for webview windows, and
>   **the main loop (`LogCollectorService`) now skips sessions whose root item is `browser_tab`** — without
>   that, a webview session (process `code`) hydrated under the SAME key as the host app's own session and
>   was closed as a duplicate one cycle later. Web: the source badge on Web Activity shows the host app
>   process name for webview-source items (data-driven). Verified: `dotnet build` 0/0, `tsc` clean; the
>   ACTUAL embedded probe run live on the dev PC captures Chrome (chatgpt.com, active) with zero junk
>   webview rows, and the poll-4 throttle returns browsers only. ⚠️ Known Linux limitation (Chromium,
>   NOT our code): Electron/Chromium apps only expose their content a11y tree when launched with
>   `--force-renderer-accessibility` (the same documented Chrome limitation) — the webview DocURL is
>   invisible otherwise; Windows/macOS UIA/AX expose webview trees on demand, so the feature works there
>   out of the box. Ships only in a new installer build.
> - 2026-08-18: **Structural process name resolution — Flatpak/snap browsers (Floorp, LibreWolf, etc.) no longer show "xdg-dbus-proxy" as their browser name.**
>   The AT-SPI PID for Flatpak apps belongs to the `xdg-dbus-proxy` IPC broker, not the app itself,
>   so `/proc/<pid>/comm` returned the proxy name. New `resolve_app_name(pid)` in the Python probe
>   checks **FLATPAK_ID** in `/proc/<pid>/environ` (extracts the short app name from the Flatpak app ID,
>   e.g. `org.mozilla.Floorp` → `floorp`) and **snap path** in `/proc/<pid>/exe` (e.g.
>   `/snap/firefox/...` → `firefox`). Falls back to `/proc/comm` for native apps. No name lists —
>   Flatpak and snap are the only sandboxing systems that inject proxy PIDs, and the metadata they
>   expose is structural (OS-defined environment variable / filesystem path). The resolved name is
>   also used for browser detection: new **structural browser detection** scans `.desktop` files for
>   `Categories=WebBrowser` (cached 5 min in `~/.cache/alpha-ai-tracker/browser_exes.json`) and
>   matches against the resolved process name — any browser installed on the system is detected
>   without a hardcoded hints list. Flatpak `.desktop` files with `Exec=flatpak run <app-id>` are
>   parsed to extract the app ID's short name. C# reader: new `_pidNameCache` (populated from AT-SPI
>   probe results each poll) lets `ReadComm()` resolve WM-only Flatpak/snap windows too.
>   `StripBrowserSuffix` gains Floorp/LibreWolf/Waterfox. Verified: `dotnet build` 0/0.
> - 2026-08-18: **Downloaded installers no longer linger in `updates/` — the folder is force-deleted after every successful update (Windows + Linux) + a startup sweep.**
>   Root cause: `AppUpdateService` downloaded the platform installer into the user data dir
>   (`~/.local/share/alpha-ai-tracker/updates` / `%LocalAppData%\AlphaAITracker\updates`) and handed it to
>   the OS installer, but nothing ever deleted it — every updated machine kept every installer it had
>   ever downloaded. Fix: new `CleanupUpdatesDirectoryAsync` force-deletes every file (retry loop, `.part`
>   leftovers included) then the folder itself. Wired into three places: (1) **Linux** — after a successful
>   `pkexec dpkg -i` in `InstallAsync` (dpkg is synchronous, so the app is still alive and can wipe the
>   consumed `.deb` immediately); (2) **Windows** — inside the detached PowerShell update script, after the
>   installer finishes and the app is relaunched (`Remove-Item` on the installer, every file in `updates/`,
>   then the folder — the app can't do it itself because on Windows `InstallAsync` returns BEFORE setup
>   runs); (3) **startup sweep** — `StartAsync` prunes the folder on every boot, so installers already
>   sitting on disk from past updates (including the two PCs affected by this report) are wiped without
>   waiting for the next update. Failures are tolerated (transient AV lock → logged, re-swept on next
>   pass; the next download prunes its own dest anyway). macOS untouched (dmg is opened, not consumed).
>   Verified: `dotnet build` 0/0. ⚠️ ships only in a new installer build.
> - 2026-08-18: **Focus totals were still frozen (~0s on the web) — the periodic flush OVERWROTE the DB row with the in-memory delta instead of accumulating. Fixed + guaranteed 1-minute push.**
>   Live evidence after the previous fix shipped in v1.2.4: Chrome stuck at exactly `fg=30.0`, VS
>   Code/Calendar/Help at exactly `300.0`, byte-identical across 65s of running — so the collector WAS
>   detecting foreground, but the flush SQL (`UpdateAppSessionFocusSql`) did `SET foreground_seconds =
>   $foreground_seconds` and then cleared the counter — each flush rewrote only the last ~10-cycle window
>   (300s main loop / 30s browser tracker), never the session total, and the web App Usage page showed
>   those tiny deltas as 0s. Fix: the flush is now **ADDITIVE** (`foreground_seconds = COALESCE(foreground_seconds,
>   0) + $foreground_seconds`) — the in-memory dictionary stays a per-flush DELTA (cleared after each write,
>   verified both loops), so the SQLite row is the true cumulative session total, survives restarts, and is
>   re-sent verbatim to the server (whose upsert overwrites with it). Close paths were audited for
>   double-counting: `CloseSessionsAndAppItemsAsync`/browser closes flush FIRST then close with NULL focus
>   (`UpdateAppSessionEndedSql` is `COALESCE($fg, fg)` — NULL keeps the flushed total). **User rule
>   2026-08-18 — every minute, force-push all `is_synced=0` rows:** (1) the main loop now calls
>   `FlushSessionFocusAsync` every 2 cycles (~60s at the default 30s interval, was every 10/~5min), so
>   growing totals re-queue every minute; (2) `SyncService` failure backoff is now **capped at 60s** — the
>   backoff only stretches a single retry gap, never the guaranteed cadence, so a drain pass (and thus every
>   `is_synced=0` row) reaches the server within a minute even during repeated failures (previously a 5-min
>   max backoff could starve unsent rows). Verified: `dotnet build` 0/0; the exact additive SQL executed
>   twice against a copy of the live DB accumulates 30→60 fg / 310→320 bg and re-queues `is_synced=0`.
>   ⚠️ ships only in a new installer build.
> - 2026-08-18: **Foreground/background focus times were stuck at 0 — two root causes fixed (live-DB + OS-probe root-caused on the Linux/Wayland dev PC, cross-checked against Windows EMP-10005/EMP-10006).**
>   **Cause 1 — Linux/Wayland foreground detection was completely dead.** Every method in `ProcessCollector.GetActiveWindowInfo` failed on this machine: `xprop _NET_ACTIVE_WINDOW` returns `0x0` (Wayland), the GNOME portal has no `org.freedesktop.portal.Window` interface (GNOME backend doesn't implement it), GNOME Shell Introspect `GetWindows` is `AccessDenied`, xdotool is X11-only, and — the core bug — **the AT-SPI probe could not read `GetState`**: at-spi2-core ≥ 2.50 returns a **PACKED 64-bit bitmask** (`[uint32 1124073730, 0]` — low word first, bit N = state N) instead of a list of state ids, and the check `if 8 not in state` (python) / `Contains("8")` (gdbus fallback) looked for state **8 = ENABLED** (every window has it) in the wrong format. Verified live: the focused Chrome window is the ONLY one whose bitmask has bit **1 = STATE_ACTIVE** set (`0x43000102` vs `0x43000100`); VS Code/Nautilus/etc. don't. Fix: `IsAtSpiActiveState`/`is_active_window` decode BOTH formats and test ACTIVE(1)/FOCUSED(12) — the old fallback heuristic (newest-started process) never matched a tracked session, so `fgKey` was always empty → every session earned only `background_seconds`. **Cause 2 — browser sessions never earned focus time at all.** Browsers are owned by `AccessibilityBrowserTracker` (excluded from the main loop's `resolvedLogs`), and the tracker never wrote foreground/background — so Chrome/Firefox/Edge rows (the majority of usage!) always showed 0/0, and when a browser held focus even non-browser apps got only background (fgKey empty). Fix: new `AccessibilitySnapshot.IsActive` set per-platform (Linux AT-SPI ACTIVE/FOCUSED bit, Windows `GetForegroundWindow` HWND match, macOS frontmost process) and the tracker now accumulates the poll interval into `foreground_seconds`/`background_seconds` per open window every poll (active window → foreground, rest → background), flushing every 10 polls + on close through the existing `UpdateAppSessionFocusAsync` (re-syncs via `is_synced=0`). Verified: `dotnet build` 0/0; the exact new decode run live on the dev PC returns exactly one active window. ⚠️ ships only in a new installer build.
> - 2026-08-18: **Client: app-session tracking now starts headlessly at boot — the GUI can no longer start/stop tracking.**
>   Root cause: `_trackingEnabled` was set ONLY by `StartTracking()`, which was called exclusively from the
>   Avalonia GUI (`MainViewModel.InitializeAsync` / `LoginAsync`). Since 2026-08-15 boot runs `--background`
>   (services only, no window), a powered-on PC restored the login (`LogCollectorService.RefreshEmployeeInfo`)
>   but the main loop spun on "waiting for login" forever: browser journeys kept flowing (`AccessibilityBrowserTracker`
>   restores the login from SQLite itself) while ZERO app sessions were collected — the web dashboard showed Web
>   Activity but empty Session Timeline/App Usage after every reboot (Linux AND Windows). Fix: `ExecuteAsync` now
>   calls `StartTracking()` right after `RefreshEmployeeInfo` when persisted employee credentials exist — the same
>   restore the GUI performed, moved into the service so it runs in `--background` mode at power-on. The GUI is now
>   strictly login-only (first-time identity + permission wizard); opening/closing it never starts or stops tracking
>   (window-close hides to tray; only an explicit tray Quit / process stop exits the tracker). `StartTracking()` is
>   idempotent (guarded login-event dedup), so the GUI restoring a second time is harmless. Verified: `dotnet build`
>   0/0.
> - 2026-08-18: **Web: Server-side date filters + expandable session groups + structural browser badges + UX fixes on App Usage and Web Activity.**
>   Server: `GET /app-sessions` and `GET /app-items` now accept `dateFrom`/`dateTo` (RFC3339 or date-only),
>   filtering `started_at` / `opened_at`; app-items search also matches `url`/`domain`. New shared
>   `ActivityFilters` component (debounced search, Today-default date presets with real local-day bounds,
>   custom range via Calendar popover) wired into both pages. **App Usage**: Duration column replaces
>   Foreground/Background (computed from `endedAt - startedAt` / `now - startedAt`); Active Time tile =
>   sum of durations; apps with >1 session get chevron → expandable nested per-session table (Opened/
>   Closed/Duration/Details). **Web Activity**: pages grouped by domain ("Visisted Sites") with expandable
>   groups; browser badge (Chrome/Floorp/etc.) from `metadata_json.processName` on every row + distinct
>   browsers per site group; search switches to flat "Matching Pages" view (exact URL visible). Both pages
>   use `keepPreviousData` so filter changes never swap the whole content to a spinner; `ActivityFilters`
>   is always mounted (search field never loses focus, filters never disappear on empty results).
>   `next.config.ts` gains `allowedDevOrigins` (suppresses cross-origin dev warning). Verified: `tsc`
>   clean, `next build` passes.
> - 2026-08-18: **Web: Employee Journey + Device Specs modules — the `/users/[id]` detail page was replaced by nine pages behind a shared shell.**
>   The old 820-line `web/src/app/(app)/employees/[id]/page.tsx` is deleted. New shared `EmployeePage` shell
>   (`web/src/components/employees/EmployeePage.tsx`) provides the page header, a searchable `EmployeeSelector`
>   picker (deep-linkable via `?employeeId=`), and loading/error/no-selection states; the page body is a
>   render-prop handed `{employee, detail?, detailLoading}`, and pages needing the aggregate machine picture pass
>   `fetchDetail` to load `GET /employees/:id/detail` (`hooks/use-employee-detail.ts`). **Employee Journey** —
>   Session Timeline (`useInfiniteQuery` on `GET /app-sessions`, 30/page, IntersectionObserver sentinel,
>   `FocusTime` fg/bg stacked bar), App Usage (aggregates the most recent 5×100 app sessions into per-app
>   fg/bg totals), Web Activity (`GET /app-items?itemType=browser_tab`, infinite scroll), plus Screenshots /
>   Location Trail placeholders (the client collects neither yet). **Device Specs** — Hardware Overview (specs +
>   storage + network + app_status), Installed Software (Applications/Packages tabs with search,
>   `InventoryTable`), Peripherals (plugged/unplugged cards + `DeviceClassIcon`), Permissions. The employees
>   table action menu now offers **View Journey** (`/employee-journey/timeline?employeeId=`) and **Device
>   Specs** (`/device-specs?employeeId=`) instead of the old detail link. `AppItem` gained the journey fields
>   in `api.ts` (`url`, `domain`, `processId`, `objectType`, `action`, `journeyId`, `sequence`, `previousPath`,
>   `currentPath`, `windowId`, `tabId`, `metadataJson`); new permission modules `employee-journey` +
>   `device-specs`; new shared helpers `lib/format.ts` + `EmptyState`. **Hook-order crash fixed while landing:**
>   hooks (`useQueries`/`useInfiniteQuery`/`useMemo`…) were being called INSIDE the `EmployeePage` render-prop,
>   so selecting an employee changed the shell's hook count and React threw "Rendered more hooks than during the
>   previous render" — each page body was extracted into a real inner component (`AppUsageBody`/`TimelineBody`/
>   `WebBody`/`SoftwareBody`). Verified: `tsc --noEmit` clean.
> - 2026-08-17: **Web sidebar restructure — Device Specs became a collapsible section.** `AppSidebar.tsx`: the
>   single top-level "Device Specs" item is now a parent with four children — Hardware Overview
>   (`/device-specs`), Installed Software (`/device-specs/software`), Peripherals (`/device-specs/peripherals`),
>   Permissions (`/device-specs/permissions`) — all under the existing `device-specs` permission module;
>   "Employee Journey" stays a collapsible with Session Timeline / App Usage / Web Activity / Screenshots /
>   Location Trail. The rest of the commit is whitespace alignment of the nav config.
> - 2026-08-15: **Duplicate sessions fixed — one session per logical window on Windows/macOS (root-PID grouping).** Root cause: on Linux, systemd cgroups (`app-*.scope`) collapse VS Code's ~8 same-named `Code.exe` processes into ONE session; on Windows/macOS there are no cgroups, so the session key fell back to per-PID and every `Code.exe` process (main window + renderer + GPU + utility + extension hosts) created its OWN session row — the web detail page showed 8 identical "Visual Studio Code" rows for one window. Fix: `LogCollectorService.ResolveRootPid` walks the PPID chain up to the TOP-MOST same-binary process (the app's main window process — a different binary is a hard boundary, so two VS Code windows still get two sessions) and every `BuildSessionKey`/`ProcessId`/root-item call site keys on it; boot hydration now dedups legacy per-PID open sessions that collapse to one key (keeps the earliest, closes the rest so they stop showing "running"). Also: a title-less child creating the session root first no longer leaves the root stuck on the process name — `UpdateActivityContextAsync` upgrades the open root in place when a window-bearing process arrives.
> - 2026-08-15: **Foreground/background focus time per session (client + server + web).** The collector already knew the OS foreground PID each cycle (`ActivityLog.IsForeground`) but never persisted it. Client: new `foreground_seconds`/`background_seconds` on SQLite `app_sessions` (idempotent MigrateSql ALTERs), accumulated per cycle for every open session (mapped through the same root-PID grouping so the whole window counts), flushed every 10 cycles + on close with `is_synced=0` (rule 2026-08-12) so SyncService re-sends; payload sends the values. Server: migration **020** adds the two columns, model/DTO/`SyncAppSessions`/`BulkInsertAppSessions` upsert (EXCLUDED overwrite — the client re-sends the totals) + `ListAppSessions` SELECT. Web: employee detail **Activity tab rebuilt with server-side pagination + infinite scroll** (`useInfiniteQuery` on `GET /app-sessions`, 30/page, IntersectionObserver sentinel) and columns **Application, Status (Running/Closed), Opened, Closed, Duration, Foreground/Background** (green/gray stacked bar + `fg / bg` readout).
> - 2026-08-15: **Boot is now fully headless — GUI only opens on manual launch.** Auto-start (Windows Run key + ONSTART scheduled task, Linux `.desktop` autostart, macOS plist) switched from `--minimized` (hidden-to-tray, UI still created) to `--background` (services only — no window, no tray at boot). `Program.cs` background mode now lazily creates the Avalonia UI ONCE on a dedicated thread when a manual launch sends the SHOW signal (new `App.LaunchedHidden` flag replaces the args check in `App.axaml.cs`), so "open the GUI" works any number of times without stopping the tracking process. The background guard migrates stale `--minimized` autostart entries to `--background` automatically (`AutoStartService.EnsureBackgroundAutoStartFlag`).
> - 2026-08-13: **Server: `employees.department` name column removed — department_id is the sole source of truth.**
>   The `employees` table stored the department NAME as a denormalized VARCHAR next to the `department_id` FK.
>   Migration **019** drops `employees.department` (column + `idx_employees_department` index, mirroring the 018
>   role-drop pattern). The department name is now resolved ONLY at read time via the existing
>   `LEFT JOIN departments d ON e.department_id = d.id` (`COALESCE(d.name, '')` in all SELECT/RETURNING clauses),
>   so API responses still carry a `department` name; nothing writes it anymore. Go: `Department` removed from
>   `CreateEmployeeRequest`/`UpdateEmployeeRequest` and from the UPDATE allowed-fields map + service branches
>   (the model/`EmployeeResponse` keep the derived name for the web table/display); repo List filter now matches
>   `d.name` (and the count query gained the departments JOIN), INSERT writes `department_id` only. Web:
>   `department?` dropped from `CreateEmployeePayload`/`UpdateEmployeePayload` (the value was never sent — the UI
>   always used `departmentId`). `users` admin table is unrelated and unchanged. Verified: `go build`/`go vet`
>   clean. **2026-08-13: Client `ALPHA_FILE_JOURNEY_ENABLED` — file-journey master switch.** New `.env` knob
>   (`.env` + `.env.example`, default `true`) controls the **Desktop Event Bus** (file-manager navigations +
>   file create/rename/delete/recent-file journeys — AT-SPI on Linux, Shell COM on Windows, FileSystemWatcher +
>   recent-files on every platform). `false` → the entire event bus (`EventCoordinator`, `JourneyEngine`,
>   `ATSPIEventWatcher`, `WindowsExplorerWatcher`, `FileSystemEventWatcher`, `RecentFilesWatcher`,
>   `DesktopEventService`) is NOT registered in DI, so zero file-journey rows are produced or synced. Browser
>   journey tracking is governed separately by `ALPHA_BROWSER_TRACKING_ENABLED` (unchanged). `AppConfig`
>   property + `--print-config` line added. Verified: `dotnet build` 0/0.
>
> - 2026-08-13: **Employees rename + employee role removed.** Web: the HR sidebar item "List of Users" is now
>   **"Employees"** and the routes `/users`, `/users/[id]`, `/users/activity` moved to `/employees`,
>   `/employees/[id]`, `/employees/activity` (permission module keys stay `users`/`users/activity` so stored
>   permission configs in localStorage survive). Employee `role` was removed end-to-end on the employee surface:
>   server migration **018** drops `employees.role` (column + its index), and `role` is gone from the Go employee
>   model/`EmployeePublic`/DTOs (`CreateEmployeeRequest`, `UpdateEmployeeRequest`, `EmployeeResponse`), the repo
>   (list filter, all SELECTs, INSERT, UPDATE allowed-fields, scans), the service, the handler, and the
>   `POST /auth/employee-login` response. Web: `role` removed from `Employee`/`CreateEmployeePayload`/
>   `UpdateEmployeePayload`, the employees table (Role column), the Add-Employee dialog, and the employee detail
>   page role badge. Desktop client: the Role line is gone from the dashboard hero and the nav-rail identity
>   block (`EmployeeRole` VM props + XAML bindings); the local `employee_info.role` cache column is left intact
>   (no client migration risk — the server simply no longer sends a value). **Admin-user RBAC is unrelated and
>   unchanged** (`users.role`, `UserRole`, permissions, settings/user-management, onboarding invites). Verified:
>   `go build`/`go vet` clean, `tsc --noEmit` clean, `next build` registers `/employees`,
>   `/employees/[id]`, `/employees/activity`, `dotnet build` 0/0.
> - 2026-08-12: **Windows auto-update "downloaded but never installed" root-caused — the installer was TREE-KILLING itself.**
>   Linux updated fine, Windows did nothing after the download finished. Root cause: `installer-windows.iss`
>   `KillRunningInstance` ran `taskkill /F /IM client.exe /T` — the **`/T` is a TREE-kill**. The self-updater
>   launches the installer as a DESCENDANT of the running app (`client.exe → cmd.exe update script → setup.exe`),
>   so that tree-kill terminated the updater's OWN cmd script AND the installer itself in `InitializeSetup` —
>   before a single file was written, with the relaunch script dead too → old version stays. Linux works because
>   `pkexec dpkg` is a separate elevated process, never in the app's kill-tree. Fix, both sides:
>   **(1)** `installer-windows.iss` — `/T` removed from `KillRunningInstance` (kills by image name only as a
>   manual-install safety net); exe name is no longer hardcoded either (`{#MyAppExeName}`) and `AppMutex` is now
>   `{#APP_MUTEX}` — both from APP_IDENTIFIERS via `windows_vars.iss`. **(2)** `AppUpdateService.InstallAsync`
>   (Windows) rewritten so the updater does NOT depend on the installer killing the app: it writes a detached
>   `.cmd` to `%TEMP%` that (a) polls `tasklist /FI "IMAGENAME eq {exeName}"` (exit code 0 = running, 1 = gone —
>   no `find` pipe, so the 25-char tasklist display truncation that broke the old `find`-based check is moot)
>   until the app exits (max ~60s, then falls through — the installer's image-name taskkill is the net), (b)
>   launches the silent installer (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`), (c) waits for setup to APPEAR
>   (UAC approval — setup.exe is only created AFTER the consent prompt is approved; ~5 min), (d) waits for it to
>   finish (~10 min), (e) relaunches `{exe} --restart` and (f) deletes itself. The app shuts itself down
>   gracefully via `Dispatcher` (Program.cs finally releases the mutex) right after spawning the script. **Slow-UAC
>   self-heal:** if setup never appears (UAC denied / launch failed) the script relaunches the old app but KEEPS
>   watching — a slow consent click can still spawn setup after that relaunch (setup force-closes the app it
>   finds), so it waits that setup out and relaunches the NEW build once more. Bounded counters everywhere
>   (60/300/600/60/600) — no loop can run away. Verified: `dotnet build` 0/0; two code reviews walked every
>   batch line against real-Windows `tasklist`/UAC/Inno semantics; the exact rendered `.cmd` was extracted from
>   the source and audited (every `goto` resolves, all five loops bounded, no executable `/T` anywhere). Wine
>   cannot smoke-test the flow (its `tasklist` returns 0 for no-match, unlike Windows' 1), so the real test is a
>   Windows machine: click Update → app closes → UAC → silent install → app reopens on the new version. Note:
>   the previous 2026-08-12 release/installer-cleanup fix (no stale assets) must ship TOGETHER with this —
>   rebuild the Windows installer so the new `.iss` (no `/T`) actually reaches machines.
>
> - 2026-08-12: **Auto-update "still shows old version" root cause fixed — stale installers can never be published or picked.**
>   Live evidence: the GitHub `v1.1.1` release carried BOTH `alpha-ai-tracker_1.1.0_amd64.deb`/`AlphaAITracker-Setup-1.1.0.exe`
>   AND the 1.1.1 ones, and the client's `updates/` dir held the downloaded **1.1.0** deb — because `release.sh` uploaded
>   EVERY file left in `client/installers/` from the previous release, and `ResolvePlatformAsset` used `FirstOrDefault`,
>   so the OLD installer won. "Update to v1.1.1" re-installed 1.1.0 over 1.1.0. Two-part fix: **(1)** `release.sh` and
>   `build-installer.sh` now `rm -rf "$INSTALLER_DIR"` at the start (after arg parsing in build-installer so `-h` doesn't
>   wipe; release.sh cleans before anything else so even a failed build can never upload stale artifacts) — stale assets are
>   unrepresentable; **(2)** `AppUpdateService.ResolvePlatformAsset` now prefers the asset whose name embeds the RELEASE
>   version (boundary-aware regex `(^|[\-_.])1\.1\.1(?=$|[\-_.])` — "1.1.10"/"1.11.1" can never match a request for "1.1.1"),
>   falling back to the old arch/extension matching for well-formed releases. Verified: `bash -n` both scripts, `dotnet build`
>   0/0, regex unit-checked against the real asset names (stale skipped / new matched / prefix-collisions rejected).
>   Note: the ALREADY-published v1.1.1 release still contains the stale 1.1.0 assets — future releases won't, and the client
>   now picks 1.1.1 even if they linger.
>
> - 2026-08-12: **Self-update from GitHub Releases — auto-update + GUI "Check updates".** New
>   `client/Services/AppUpdateService.cs` (ObservableObject + IHostedService singleton, same
>   register-singleton-and-hosted pattern as SyncService): checks
>   `https://api.github.com/repos/{repo}/releases/latest`, normalizes the tag (`v1.1.0` → `1.1.0`),
>   picks the platform installer asset (Linux `_amd64.deb` by runtime arch, Windows `.exe`, macOS
>   `.dmg`) and compares against `AppInfo.Version` (numeric three-part compare, `-beta`/`+sha`
>   ignored). Quiet **auto-check loop** every 30 min that only fires when the persisted
>   `update_last_check_at` (app_status) is older than `ALPHA_UPDATE_AUTO_CHECK_HOURS` (24h); with
>   `ALPHA_UPDATE_AUTO_INSTALL=true` (default) it **auto-downloads and installs** with no click:
>   Linux `pkexec dpkg -i` (the only human step is the polkit password dialog — the install dir is
>   root-owned), Windows runs the Inno installer with `/VERYSILENT` (Inno `CloseApplications=force`
>   terminates the app itself, so a detached `.cmd` waits then relaunches with `--restart`), macOS
>   `open`s the dmg (manual drag). Downloads stream into the user data dir
>   (`~/.local/share/alpha-ai-tracker/updates` / `%LocalAppData%\AlphaAITracker\updates`) — never the
>   install dir. GUI: top bar gains **Check updates** (ghost), a **"Update to vX.Y.Z"** install
>   button, a **Restart to apply** button (Linux dpkg replaces the binary while running) and a status
>   line; the dashboard shows an **update banner** (version + release notes + progress bar + Later
>   which persists `update_dismissed_version`). `Program.cs` handles the new `--restart` arg by
>   retrying the single-instance mutex for 8s (post-update relaunch vs signal-and-exit). Config: new
>   `ALPHA_UPDATE_REPO/ENABLED/AUTO_CHECK_HOURS/AUTO_INSTALL` keys in `.env` + `.env.example` +
>   `AppConfig`. The repo ALWAYS comes from `.env` — `ALPHA_UPDATE_REPO`, falling back to the
>   pre-existing `REPO=` key (now actually read); there is NO hardcoded repo anywhere, and when
>   neither key is set the updater is disabled with a clear "No update repository configured"
>   message on manual checks. Verified: `dotnet build` 0/0;
>   live smoke test hit the real GitHub API (`You're up to date (1.0.0).` against the v1.0.0
>   release with `alpha-ai-tracker_1.0.0_amd64.deb` + `AlphaAITracker-Setup-1.0.0.exe` assets).
>   **Note:** `AppUpdateService` deliberately uses EXPLICIT properties + `RelayCommand` fields
>   (no `[ObservableProperty]`/`[RelayCommand]` source generators) — generated members made IDEs
>   show phantom "name does not exist" errors until the analyzer re-ran, even though the CLI build
>   was always clean. Manual install/auto-install share one download path behind an atomic
>   `_installGate` so the shared `.part` file can never be written by two threads at once.
>
> - 2026-08-12: **Client rule — any UPDATE on an already-synced row resets `is_synced=0` (server always learns changes).**
>   Audited every write path in the client SQLite layer and found 8 that mutated rows without re-queueing them:
>   (1) `UpdateAppSessionEndedSql` (session close) — CRITICAL: a session synced as OPEN never told the server it
>   ended; the web dashboard showed it open forever. (2) `CloseHardwareDeviceAsync` — CRITICAL: a plugged-in device
>   synced as connected never told the server it was unplugged. (3) `InsertAppSessionSql` + (4) `InsertAppItemSql`
>   `ON CONFLICT` DO-UPDATE clauses — a re-stored session/item now re-syncs. (5–8) all four `network_info`
>   mutators (`MarkNetworkInfoNotCurrentAsync`, `TouchNetworkInfoAsync`, `MarkAllNetworkInfoNotCurrentAsync`,
>   `TouchCurrentNetworkInfoAsync`) + the `MigrateSql` current-row backfill — demoted/touched rows re-sync so the
>   server can never show a superseded row as current. Server-side consumption verified: `app_sessions`/`app_items`/
>   `hardware_devices` upsert on conflict (DO UPDATE), so re-sent rows propagate. ⚠️ `network_info` sync is
>   `ON CONFLICT (id) DO NOTHING` server-side and has no `is_current`/`last_seen_at` columns — re-sent network rows
>   are inert no-ops today (kept for rule consistency + future-proofing; the `TouchCurrentNetworkInfoAsync` reset
>   re-queues one tiny row ~every 5 min on stable networks). All other update paths already reset `is_synced`.
>   Verified: `dotnet build` 0/0, `go build`/`go vet` clean, all 8 edited statements executed cleanly against a
>   copy of the real client DB.
>
> - 2026-08-11: **Web user-detail page + employee detail endpoint.** The HR → List of Users page
>   dropdown was clipped by the table's `overflow-x-auto` scroll container whenever the list was
>   short (the custom absolute-positioned menu) — replaced with the Radix `DropdownMenu` (portal-
>   rendered, can never be clipped), and each row now links to a new **`/users/[id]`** page.
>   Server: new aggregate endpoint **`GET /employees/:id/detail`** (protected) returns one
>   employee's full machine picture in a single response — employee record, latest
>   `device_hardware_info`, `storage_devices`, latest `network_info`, currently-installed
>   applications/packages (active `employee_installed_*` junction links joined with the catalog
>   rows), `hardware_devices` peripherals, `permission_status` checks, `app_status` key/value map
>   and activity stats (session/item counts + last activity). UUID route param is resolved to the
>   `EMP-XXXXX` id first (sync tables are keyed by it). New repo read methods in `new_schema_repo.go`
>   (`GetLatestDeviceHardware`, `ListStorageDevices`, `GetLatestNetworkInfo`,
>   `ListEmployeeApplications`, `ListEmployeePackages`, `ListHardwareDevices`, `ListAppStatus`,
>   `ListPermissionStatus`, `GetEmployeeActivityStats`), `NewSchemaService.GetEmployeeDetail`,
>   handler + route. The page shows identity/status, six stat tiles, and tabbed sections for
>   Hardware (specs + storage + network + device status), Applications, Packages, Peripherals and
>   Permissions — all real API data, with loading/empty/error states and a Generate-Secret dialog.
>   Verified: `go build`/`go vet` clean, `tsc --noEmit` clean, `next build` succeeds with
>   `/users/[id]` registered as a dynamic route.
> - 2026-08-11: **Instant sync on login — full machine picture lands on the server the moment an employee logs in.**
>   `SyncService` gained a wake-up signal (`RequestImmediateSync()` — `SemaphoreSlim(0,1)` released by the caller;
>   the inter-pass wait is now `WaitAsync(wait, ct)` so a release ends the wait at once and runs a full drain pass
>   instead of waiting out the 60s idle tick). `MainViewModel` injects the singleton `SyncService` (now registered
>   `AddSingleton` + `AddHostedService(GetRequiredService)` — same pattern as `LogCollectorService`) and fires it in
>   BOTH login paths: `LoginAsync` (after `SaveEmployeeInfoAsync`, so the token is persisted before the sync reads it)
>   and `InitializeAsync` (session restore on launch). The instant pass drains every unsent table —
>   `device_hardware_info`, `installed_applications`, `installed_packages`, `network_info`, `storage_devices`,
>   `hardware_devices`, `session_events`, `permission_status`, `app_status`, `app_sessions`, `app_items` — in the same
>   byte-bounded/gzip/backoff pipeline as the idle passes. `employee_info` itself needs no sync: the login endpoint
>   response already carries the employee record server-side (the client table is its offline cache). Semantics: a
>   request while a pass is running or while one is already pending is a single no-op release (one drain covers it),
>   so login never stacks passes. Verified: `dotnet build` 0/0, code review clean (no races; credentials persisted
>   before signal; DI resolves the same singleton).
> - 2026-08-11: **Client retention + 4 new server sync surfaces.** The client now DELETES synced
>   data the server already has: `app_items`/`app_sessions` older than `ALPHA_SYNC_RETENTION_HOURS`
>   (24h default; OPEN sessions are NEVER deleted, and a session is only deleted once all its
>   app_items are gone), `installed_applications`/`installed_packages` rows with `is_installed=0`
>   (closed install cycles), and superseded `network_info` rows (`is_current=0`). Everything else
>   is retained forever. Runs in `SyncService` after each clean drain pass, one SQLite transaction.
>   **Four previously local-only tables now sync to the server** (migration 017): `app_status`
>   (key/value; a value change resets is_synced so the server learns it next roundtrip),
>   `hardware_devices`, `permission_status`, `storage_devices` — new DTO/service/repo/handler/routes
>   (11 sync endpoints total). **permission_status duplication root-caused & fixed:**
>   `SetPermissionStatusAsync` minted a FRESH GUID per check (~every 5 min) and inserted new rows
>   instead of updating → ~2,800 rows/day; now keyed on the stable `"{platform}_{method}"` with
>   `ON CONFLICT(check_id) DO UPDATE` (one row per permission method). Client: `is_synced`/`synced_at`
>   columns added to `app_status`/`permission_status` (idempotent MigrateSql ALTERs);
>   `MarkSentCoreAsync` generalized to a configurable id column. Server DB wiped as requested
>   (all data tables emptied; users/employees/departments/schema_migrations kept — verified).
>   Verified: `dotnet build` 0/0, `go build`/`go vet` clean, migrations 001–017 applied.
> - 2026-08-11: **Sync engine decoupled — collection NEVER blocks on the network; 50k+ backlogs drain in minutes.**
>   The old inline sync ran inside the collection loop every 10 cycles (~5 min) and fetched a FIXED 500 rows/table/cycle
>   (50k queued rows → ~8 h to drain, while collection paused and CPU spiked). New dedicated `client/Services/SyncService.cs`
>   (BackgroundService, registered in `Program.cs`) drains unsent SQLite rows on its own loop: chunks bounded by BOTH row
>   count (`ALPHA_SYNC_MAX_ROWS`, 1000) and serialized payload bytes (`ALPHA_SYNC_MAX_BYTES`, ~1 MB, oversized slices
>   auto-split by binary halving), ~150 ms politeness pause between chunks, a 5-min per-pass budget
>   (`ALPHA_SYNC_MAX_DURATION_SEC`) so a huge backlog never monopolizes CPU, and **exponential backoff** on failure
>   (5s→10s→…→`ALPHA_SYNC_BACKOFF_MAX_SEC` 5 min). Request bodies are **gzip-compressed** (`ALPHA_SYNC_COMPRESSION`,
>   server side: `middleware.BodyLimit("20M")` + `middleware.Decompress()` added in `server/internal/router/router.go` —
>   Echo's default 2 MB body cap could reject big raw batches). SQLite mark-sent is now batched
>   (`UPDATE … WHERE id IN (…)`, 400 ids/statement — was one UPDATE per row), and existing DBs gained
>   `(is_synced, started_at)` / `(is_synced, opened_at)` indexes on the two big tables (fresh DBs already had them).
>   Server: `SyncInstalledApps`/`SyncInstalledPackages` now use **ONE transaction per request** (was 1 tx per entry =
>   500 tx per batch). New env knobs: `ALPHA_SYNC_INTERVAL_SEC / MAX_ROWS / MAX_BYTES / CHUNK_DELAY_MS /
>   MAX_DURATION_SEC / BACKOFF_MAX_SEC / COMPRESSION` (`.env` + `.env.example` + `AppConfig`). The collection loop now
>   only does `StorePermissionStatus` + heartbeat. Docs: `client/ARCHITECTURE.md` §13 + env table. Verified: `dotnet build`
>   0/0, `go build`/`go vet` clean. ⚠️ Installer-Parity: not "done" until verified from an installed build (re-bake
>   `config.enc` — the new vars must be in `.env` before `encrypt-config.sh`).
> - 2026-08-11: **Inventory GUI shows ONLY currently-installed software — uninstall history hidden.** The Installed Applications page (Applications/Packages tabs) no longer lists closed install cycles: `InstalledAppsViewModel.ApplyFilter` skips `IsInstalled == false` rows, and the table's **UNINSTALLED column was removed** (header + cell) along with the dimmed `RowOpacity` history treatment — the `RowOpacity` property is gone from `InventoryRow` (closed-cycle rows still live in SQLite and sync upstream unchanged; page + dashboard badges already counted only installed cycles). The lifecycle data model is untouched — this is display-only, client-only (no server/web change).
> - 2026-08-11: **Package install/uninstall is now real-time too — `InstalledSoftwareWatcher` gained package-manager watch locations.** The round-3 watcher only watched APP evidence (`.desktop` dirs + `/var/lib/dpkg` on Linux), so `npm install -g`/`npm uninstall -g` (e.g. cline) changed nothing the watcher saw — the DB only updated when the GUI Rescan button manually called `RescanInventoryAsync`. New `GetPackageWatchDirectories()` watches the trees where PACKAGE installs leave filesystem evidence: **Linux** — npm global root (resolved at runtime via a time-boxed `npm root -g` probe, since the root varies per setup: nvm/fnm/user-prefix/deb node, plus `/usr/local/lib/node_modules` + `/usr/lib/node_modules` fallbacks; a package dir is a DIRECT child, so one non-recursive watcher sees install/uninstall instantly), `~/.local/lib` (PEP 370 pip `--user` site, recursive — the versioned site-packages dir is a grandchild), `/var/lib/snapd/db` (snap state, rewritten in place → LastWrite), flatpak runtime roots; **Windows** — `%APPDATA%\npm\node_modules` (npm global); **macOS** — Homebrew Cellars. node_modules/Program Files trees are watched top-level only (recursion inside package trees only adds noise); `RescanInventoryAsync` already re-scans apps AND packages, so one event covers both. Verified live on this DB, one process lifetime, no GUI: `npm install -g cline` → new open `installed_packages` row in **~2 s**; `npm uninstall -g cline` → that cycle closed in **~3 s**; rescan count stays flat while idle (2 total, no loop); cline restored to its pre-test (uninstalled) state; build 0/0, client healthy.
> - 2026-08-10 (round 3): **Install/uninstall detection is now 100% EVENT-DRIVEN — no minute-based polling (user rule 2026-08-10: no periodic inventory scan).** New `InstalledSoftwareWatcher` (BackgroundService) watches the OS install locations — Linux `.desktop` dirs (user + system + flatpak + snap exports) and `/var/lib/dpkg`; Windows Start Menu (user + common) and Program Files (top-level only); macOS `/Applications` — and triggers an instant `LogCollectorService.RescanInventoryAsync` whenever software is installed/uninstalled through terminal, software center, control panel, cmd/powershell or manual file delete. Events are debounced (1.5s anchored to the FIRST event of the burst, so a constant event stream can never starve the rescan), min-gapped (5s) and coalesced (an in-flight rescan is never stacked; a change arriving mid-scan re-arms the burst so nothing is lost). The collector's `_cycleCount % 30/60` periodic app/package scans were REMOVED entirely — the one-time startup scan is the only other scan — and `ALPHA_INVENTORY_SCAN_MINUTES` was deleted (only `ALPHA_INVENTORY_WATCH_ENABLED` remains). The GUI Installed Apps page runs a 5s `DispatcherTimer → PollAsync` (pure SQLite re-read, no OS scan) while visible, so changes appear live without clicking Rescan or restarting. **Two root-cause bugs were found and fixed live:** (1) **probe deadlock froze the watcher** — the old `StandardOutput.ReadToEnd() → WaitForExit(ms)` pattern blocked forever when a grandchild inherited the stdout pipe (observed `wait_for_partner`/`anon_pipe_read`); because the rescan never returned, its in-flight flag stayed set and EVERY later install/uninstall event was coalesced away — the DB looked frozen until an app restart. All 11 CLI probes (10 in `PackageDetector`, 1 in `InstalledAppDetector`) now go through the new `ProcessFilter.RunProbe`: concurrent stdout+stderr drain (a chatty stderr can never fill its pipe), a hard time-box on BOTH exit and stream drain, and `Kill(entireProcessTree)` on timeout; (2) **uninstall was invisible after the first rescan** — `InstalledAppDetector.ForceRecheck()` cleared `_knownApps` but NEVER `_installedApps`, and the platform scanners only APPEND, so an uninstalled app stayed in every scan result forever and the lifecycle pass never closed its cycle (it only looked correct in earlier tests because each test cycle restarted the client). `ForceRecheck` now clears `_installedApps` + `_binaryToDisplayName` (PackageDetector already cleared its `_packages`). Verified live on this DB, one process lifetime, no restart: copyq install → new open row in **~2–5 s**; copyq uninstall → that row closed in **~2–5 s**; rescan count stays flat while idle (no loop); client healthy, build 0/0. Installer note: the watcher is compiled into `client.dll` (no packaging change); the `ALPHA_INVENTORY_WATCH_ENABLED` knob ships via the normal `config.enc` pipeline (`.env` before `encrypt-config.sh`).
> - 2026-08-10 (round 2): **Install/uninstall lifecycle REDESIGNED — ONE ROW PER INSTALL CYCLE (v1 install_count design replaced).** The user's rule: install date is the *honest* OS-reported date — pre-existing software the tracker never saw installed shows **NULL / “Unknown”** on the GUI (no `detected_at` backfill faking a date), and only **newly detected installs** (copyq while the tracker runs) get the current time stamped. And no install counter: install→uninstall→reinstall yields **one record per cycle** — `record 1 (install → uninstall)`, `record 2 (install → …)`. Client SQLite tables were rebuilt into the rows-per-cycle shape: `app_name` and the package `(package_name, source_manager)` fingerprint are **no longer unique** (a UNIQUE constraint would block reinstall history), `install_count` is dropped, and `install_date` is reset to NULL for all pre-existing rows (legacy detection via `install_count` column presence → `RebuildInventoryTablesIfLegacyAsync` drops+recreates both tables in one tx with `PRAGMA foreign_keys=OFF` — the parent tables are FK-referenced by `app_sessions`, and the idempotent migration must also NOT re-create the old UNIQUE fingerprint index, which it previously did on every launch and which would have broken reinstall inserts). Store upserts now open/close cycles: an open cycle (`uninstall_date IS NULL`) is updated in place (install_date kept), otherwise a NEW cycle row is inserted (install_date = OS date, or NULL on the baseline scan, or now for a runtime-detected install). `ApplyInventoryLifecycleAsync` is close-only (missing open rows → `is_installed=0` + `uninstall_date`); reinstalls open new rows in the store pass. Mappers/lookups prefer the open cycle; the GUI lists **only currently-installed cycles** — closed history rows are filtered out of the table and there is no Uninstalled column (display-only change, 2026-08-11); page + dashboard badges count only currently-installed cycles. Two migration bugs found & fixed on the live DB: the rebuild deadlocked startup (`SemaphoreSlim` gate re-acquired inside the gated `InitializeAsync` — now gate-free, documented) and then failed with `SQLite Error 19 FOREIGN KEY constraint failed` on `DROP TABLE` — fixed with `PRAGMA foreign_keys=OFF/ON` around the rebuild. Verified live on this DB (build 0/0): copyq install → `install_date=12:19:00` while VS Code/Firefox/Chrome stay NULL/Unknown; copyq uninstall → `is_installed=0` + `uninstall_date=12:20:05`; copyq reinstall → **NEW row** `install_date=12:21:03` (2 records for 2 cycles, exactly as specified); cline reinstall likewise opens a new row; migration zero false uninstalls; client healthy.
> - 2026-08-10: **Employee disconnect removed.** The **Disconnect** button was removed from the
>   client nav rail, along with the entire employee-disconnect flow: `LogoutCommand`/
>   `LogoutAsync` and `LogCollectorService.StopTracking()` are gone from the client, and the server
>   endpoint `POST /api/v1/auth/employee-disconnect` (handler + `EmployeeDisconnectRequest` DTO +
>   route) was deleted. The web admin `POST /api/v1/auth/logout` (httpOnly-cookie flow) is
>   unrelated and unchanged. Employees can no longer disconnect themselves; the client tracks until
>   the process stops. Contract table updated below.
> - 2026-08-10: **Client GUI rebuilt as six pages + runtime branding pipeline.** The Avalonia UI was a
>   single `MainWindow.axaml` monolith (login → wizard → profile, dark theme). It is now a **router**
>   plus six `UserControl` pages under `client/Views/Pages/` — Splash, Login, PermissionSetup, Dashboard,
>   SystemSpecs, InstalledApps — behind a 246px nav rail. Three page ViewModels
>   (`DashboardViewModel`, `SystemSpecsViewModel`, `InstalledAppsViewModel`) joined `MainViewModel` in DI
>   as Transient; `Styles/AppTheme.xaml` became a **light design-token dictionary** (palette, rail tokens,
>   badge surfaces, 4 shadows, 5 radii, 9 vector icon geometries) and `App.axaml` carries ~30 style
>   classes + 5 animations. Two previously-unsurfaced data sets got real screens: **System Specs** (CPU,
>   RAM, GPU, storage, network, hot-plugged peripherals) and **Installed Applications** (the
>   `installed_applications` + `installed_packages` inventory, searchable, virtualized for
>   multi-thousand-row lists). New `client/Core/AppInfo.cs` makes **every visible brand string and version
>   derive from `client/APP_IDENTIFIERS` + `client/VERSION` at runtime** — `APP_IDENTIFIERS` is embedded
>   into `client.dll` as `client.APP_IDENTIFIERS` and parsed with a strict regex (read as data, never
>   executed); `VERSION` flows through `InformationalVersion` and is read back with any `+sha` stripped.
>   Change either file, rebuild, and the rail, window title, splash, footer, tray tooltip and installer
>   filenames all follow — see §6 → *Branding-Single-Source Rule*. No installer-script change was needed:
>   the embedded resource and the `Assets/**` glob both ship inside `client.dll`. New docs:
>   [FILE_HIERARCHY.md](./FILE_HIERARCHY.md), [WORKFLOW.md](./WORKFLOW.md),
>   [client/UI_ARCHITECTURE.md](./client/UI_ARCHITECTURE.md).
> - 2026-08-08 (round 2): **Journey noise flood fixed — AppData churn + feedback loop.** Fresh-DB
>   test showed 374 junk rows appearing with NO user file ops: Chrome/Brave/Edge rewrite
>   `Local State`, `Cookies-journal`, `History-journal`, `Breadcrumbs`, `Network Persistent
>   State`, `Cache_Data\f_*` via tmp+rename every few seconds, and a recursive journey watcher
>   had been attached to `C:\Users\pc005\AppData\Local` (browsed while checking the DB). Fixes:
>   (1) `FileSystemEventWatcher` excludes the Windows app-data/package trees structurally —
>   `appdata/`, `programdata/`, `program files/` (slash-normalized, no product names) — plus
>   the user-profile root and drive roots are never watched recursively. (2) `EnsureWatching`
>   now requires a real existing DIRECTORY, returns a success bool, and only logs on success —
>   extensionless cache FILES ("Local State") can no longer be mistaken for folders and spawn
>   watchers. (3) `EventCoordinator.InferObjectType` on Windows: extensionless paths that no
>   longer exist classify as **File** (browser config stores), not Folder — killed the
>   misclassification that fed the loop. (4) `DesktopEventService` calls `EnsureWatching` ONLY
>   for `navigate` actions, never for file create/rename/delete/modify — file-op events can no
>   longer spawn new watchers (the feedback loop). Verified: 90s idle with zero file ops → 13
>   rows, all legitimate (4 Explorer navigations for actually-open windows + 5 browser tabs),
>   0 cache-churn rows; journey watchers only on real browsed folders (`C:\tetsting`).
> - 2026-08-08: **Windows file-explorer journey — Explorer navigation + journey-driven watching.**
>   The journey pipeline was Linux-shaped: `ATSPIEventWatcher` (D-Bus AT-SPI) is Linux-only, so on
>   Windows nothing reported which folder the user was browsing, and `FileSystemEventWatcher` only
>   covered the 6 fixed user folders — create/rename/delete in any other folder (e.g. `C:\project`)
>   was invisible, and `RecentFilesWatcher` watched the Linux `recently-used.xbel` path. Fixes:
>   (1) NEW `WindowsExplorerWatcher` — polls the shell's OWN registry via Shell COM
>   (`Shell.Application → Windows()`), reads each Explorer window's `file://` `LocationURL` as its
>   exact browsed folder, and emits `navigate`/`close` events per window (verified live: a window on
>   `C:\tetsting` exposed `file:///C:/tetsting`). No UIA tree walking, no product-name lists.
>   (2) `FileSystemEventWatcher.EnsureWatching(folder)` — journey-driven watching: the folder the
>   user navigates to gets a recursive watcher even when it is outside the fixed 6 (bounded set of
>   24, pruned after 15min idle; drive roots and `C:\Windows` tree excluded). (3) `EventCoordinator`
>   now attributes raw `filesystem` events (which carry no window identity) to the Explorer window
>   browsing the containing folder via `IExplorerWindowProvider.TryGetWindowForPath` — the file op
>   joins the SAME journey (same WindowId key) as the navigation. (4) `RecentFilesWatcher` Windows
>   source: watches `%APPDATA%\Microsoft\Windows\Recent\*.lnk` and resolves each shortcut's target
>   via WScript.Shell COM (the OS's own LNK reader) → `open` events. Windows shell `explorer` is a
>   platform constant (like `C:\Windows`), mapped to "File Explorer" for display. Verified:
>   `dotnet build` 0/0, client healthy, create/rename/delete now stored with `WindowId` matching the
>   browsing Explorer window.
> - 2026-08-08 (round 5): **DE-HARDCODED Windows software detection — pure OS metadata, zero product-name lists.**
>   The user's rule (now §6 *No-Hardcoded-Names Rule*): never hardcode software names for detection — every employee PC
>   has a different OS (Win 7/10/11, Ubuntu versions), department (dev/SEO/marketing/IT) and toolset, so name lists
>   silently break detection everywhere except the machine that generated them. All Windows app-vs-package decisions are
>   now OS metadata: (1) **PE Subsystem** — the OS's own statement of GUI (IMAGE_SUBSYSTEM_WINDOWS_GUI=2) vs console/CLI
>   (CUI=3); new `client/Core/ExecutableMetadata.cs` reads it for the Start Menu scan (Node.js/Git Bash/Command
>   Prompt/Python shortcuts are dropped as CUI targets — the exact .desktop analog), the registry scans (DisplayIcon/
>   InstallLocation exes decide app vs package), the classifier, and the runtime session gate; (2) **C:\Windows tree** —
>   anything under the Windows dir (System32/SystemApps/WinSxS) is an OS component (RuntimeBroker, GameBar, Video.UI,
>   conhost…); `LogCollectorService` resolves the running exe once per process name (5-min memoized) and rejects it —
>   the four Windows name blocklists (WindowsNonAppProcesses/Suffixes/Prefixes/WindowsCliOrRuntimeBinaries, ~120
>   names) are DELETED; (3) **Driver Store** — drivers never appear in the Uninstall registry or package managers; new
>   `PackageDetector.ScanDriverStore` reads
>   `HKLM\SYSTEM\CurrentControlSet\Control\Class\{class-GUID}\<instance>` — the data Device Manager shows
>   (DriverDesc/ProviderName/DriverVersion, e.g. "Realtek Audio"/"Realtek Semiconductor Corp."/6.0.9175.1), works
>   Win 7→11, replaces the Realtek/NVIDIA/Intel/AMD name blocks; (4) **winget is the source of truth** —
>   `IsWingetFrameworkRow` deleted, so `.NET SDK`, VC++ redists and runtimes land in installed_packages like dpkg
>   lists every apt package (the classifier dedups GUI-app collisions); (5) **registry flags only** —
>   `JunkRegistryNamePatterns`/`IsDevToolRegistryName`/`ClassifyRegistryPackage` (~60 names) deleted;
>   `IsSystemComponentEntry`/`IsSystemOrUpdateRow` use SystemComponent/ParentKeyName/ReleaseType + Windows Update
>   conventions (KB, Security Update, LocalServiceComponents, * Uninstaller); entries without a GUI exe are apps only
>   when the OS says so (URL associations or a matching Start Menu shortcut), otherwise packages. Installer
>   bootstrappers (C:\ProgramData\Package Cache), uninstallers (unins*/uninstall*) and *setup* executables are
>   structurally excluded from BOTH tables (they are installers, not software); (6) `CliKnownPackages` (~60 names)
>   deleted — `IsKnownPackage` was dead code (no consumers). **Critical bug found while validating:** the first PE
>   reader read the optional-header magic at peOffset+4 — that is the COFF `Machine` field (0x8664 = AMD64), so EVERY
>   x64 exe failed the magic check and read as subsystem 0 ("unknown") — the magic lives at peOffset+24 after the
>   20-byte COFF header; the Subsystem field is at optional-header offset 68 in BOTH PE32 and PE32+. Answer to "why
>   is dotnet missing": `.NET SDK 10.0.302` IS in installed_packages (winget); the `.NET Runtime` rows are
>   SystemComponent=1 internal churn, correctly skipped. Honest metadata outcomes documented: Git Bash/git-gui stay
>   apps (git-bash.exe is a GUI-subsystem mintty terminal, like gnome-terminal on Linux), GNS3 lands in packages on
>   Windows (its gns3.exe is a genuine console launcher stub — subsystem 3 — verified byte-level). Allowed exceptions
>   (documented in §6): OS-shell constructs only — Linux GNOME daemon prefixes, Windows shell display names
>   (explorer→File Explorer), the KB-prefix update convention, and unins*/uninstall/*setup installer naming.
>   Verified live: build 0/0, full re-scan: 76 apps (VS Code one clean row, browsers, Office, WPS, WinRAR, Wireshark…),
>   55 packages (Node.js, .NET SDK, Python, Realtek, freebuff@0.0.142, drivers…), zero bootstrapper rows, zero
>   RuntimeBroker/GameBar/Video.UI leaks.
> - 2026-08-08 (round 4): **Windows packages now include MSI-installed dev tools + drivers.** Root-caused from the live
>   DB: Node.js (v24.19.0) was missing entirely, and Realtek audio components vanished too. The registry dev-tool
>   filter correctly dropped them from installed_applications (not GUI), but NO source ever added them to
>   installed_packages — winget/npm/pip only see package-manager installs, and MSI installers exist ONLY in the
>   registry Uninstall keys (the Windows analog of dpkg listing libs/firmware). New `PackageDetector.ScanRegistrySoftware`
>   reads all 3 Uninstall nodes (HKLM + WOW6432Node + HKCU) and captures: dev runtimes/tools (Node.js, Eclipse Temurin
>   JDK, .NET SDK, OpenSSL, Npcap, plus Git/Go/Python/PostgreSQL/Redis/Nmap with `IsPackageAlreadyKnown` fuzzy dedup
>   so nothing duplicates winget) → category runtime/tool/library, and driver/system components (Realtek Audio COM
>   Components, Realtek High Definition Audio Driver, NVIDIA/Intel/AMD patterns) → category `driver`
>   (`SoftwareCategoryResolver.ResolveForPackage` now preserves the driver category — it previously fell through to
>   `tool`). SystemComponent=1 rows (the .NET/VC++/Python sub-component churn) and GUI apps are excluded; names are
>   cleaned ("Eclipse Temurin JDK with Hotspot 17.0.19+10 (x64)" → "Eclipse Temurin JDK"). Verified live: 7 new
>   installer packages, zero duplicates, Video.UI leak fixed (`WindowsNonAppProcesses` had `videoui` but the real
>   process is `Video.UI` — dotted name never matched). Build 0/0.
> - 2026-08-08 (round 3): **Windows inventory accuracy — Start Menu .lnk = the .desktop analog.** Linux decides
>   GUI-vs-package by `.desktop` presence; Windows had no equivalent, so registry DisplayNames came through raw
>   ("Microsoft Visual Studio Code (User)" while the binary is Code) and global CLIs were invisible. Four fixes landed
>   together, verified live on a Windows 10 machine: (1) `InstalledAppDetector.ScanStartMenuShortcuts` enumerates Start
>   Menu .lnk files (user + common) via WScript.Shell (base64 `-EncodedCommand` PowerShell — no quoting bugs), resolves
>   each target exe → clean display name + binary map (`Visual Studio Code.lnk` → Code.exe) — the exact Windows analog
>   of `Exec=` in a `.desktop`; AppX/URL/folder targets and junk names (Uninstall/Getting Started/…) are skipped.
>   (2) Registry scan now prefers the shortcut name over the registry DisplayName and merges registry metadata
>   (version/publisher) into the SAME row (the table is keyed on `app_name`), so "Microsoft Visual Studio Code (User)"
>   dedups to "Visual Studio Code"; `CleanRegistryDisplayName` strips (User)/(64-bit) noise; junk filter extended
>   (LocalServiceComponents, * Uninstaller). (3) New `SoftwareClassifier.CollapseWindowsDuplicateApps` pre-pass:
>   same-binary rows collapse to the shortest clean name, registry-only stragglers match their shortcut by
>   version-stripped name ("Advanced IP Scanner 2.5.1" → "Advanced IP Scanner"), and CLI/runtime launchers (node,
>   git-bash, git-cmd, git-gui, python, pythonw, cmd, pg_ctl, wsl…) are dropped from apps — they belong in
>   installed_packages, exactly like Linux tools without a .desktop. (4) `PackageDetector.BuildCliStartInfo` — npm/scoop
>   are `.cmd` batch shims that `Process.Start`/CreateProcess can't launch, so `ScanNpmGlobal`/`ScanScoop` died silently
>   in the catch and global npm tools never appeared (freebuff@0.0.142, opencode-ai@1.18.15 missing). Now invoked via
>   `cmd.exe /c` on Windows. Also: the empty-`Categories` GUI gate in `LogCollectorService.ResolveAppInfoInner` is now
>   Linux-only (Windows registry/lnk apps only get Categories for browsers, so the gate wrongly rejected every
>   non-browser). Verified live: 81 clean apps (VS Code one row, binary Code, v1.132.0 + publisher), 13 packages
>   (freebuff npm, Git, Go, Nmap, PostgreSQL, Python, Redis, Ubuntu, WSL, pip — zero GUI apps), zero system-component
>   leaks. Note: old polluted installed_* rows linger until wiped — the scan upserts by app_name but never deletes
>   stale rows (wipe keeping employee login rebuilds clean, as on Linux).
> - 2026-08-08 (round 2): **Fresh-Windows-DB "missing tables" + hardware_devices fix.** A brand-new Windows DB showed empty
>   installed_* tables for the first minutes because the app/package scan only ran every 30 collection cycles (~15 min)
>   — the periodic cadence is invisible on long-lived Linux DBs. `LogCollectorService.ExecuteAsync` now also runs
>   `CollectInstalledApplicationsAsync` immediately at startup. `hardware_devices` stayed at 0 on Windows because the
>   PnP probe used `Win32_PnPEntity`, which has NO `Class` property (only ClassGuid) — the class filter silently matched
>   nothing. Switched to `Get-PnpDevice -PresentOnly` (real Class values), dropped `PrintQueue` (virtual printer queues
>   like "Microsoft Print to PDF"/". AnyDesk Printer"), required USBSTOR/USB InstanceIds for DiskDrive and USB for
>   NetworkAdapter (internal SATA/NVMe/NIC excluded), and verified live: 7 clean peripherals (keyboard, mouse, headset,
>   monitor, USB composite) recorded on the test machine. Blocklist extended for the remaining WindowsApps components
>   (GameBarFTServer, olk, GameBar, gamingservices, xboxstatsserver…) and Git-Bash/Windows CLI tools (tail, grep, sed,
>   ipconfig, tasklist…) so they never auto-register as applications.
> - 2026-08-08: **Windows installed-software + hardware detection overhaul (root-caused from the live Windows DB).**
>   `installed_packages` was flooded with GUI apps (77/78 rows from `winget list`) and `installed_applications`
>   with runtimes/system components (node, dotnet, RuntimeBroker, tray helpers) — both fixed:
>   (1) `PackageDetector.ScanWinget` rewritten — the space-padded console table is now sliced by column start
>   offsets read from the header (multi-word names like "Google Chrome" are no longer shredded), ARP\/MSIX rows
>   (the actual GUI apps) and MS Store apps are dropped, .NET/VC++ framework rows are filtered, and the version
>   column is sliced against `Available`. `SoftwareClassifier` gained fuzzy name suppression ("Microsoft Visual
>   Studio Code (User)" vs registry "Visual Studio Code", ≥4 chars both sides so Git/Go are never over-suppressed);
>   (2) `InstalledAppDetector.DetectInstalledWindows` now scans ALL three registry Uninstall nodes
>   (HKLM, WOW6432Node, HKCU — the old code read only HKLM, which is why only 3 apps were ever found) with a
>   junk filter (SystemComponent, updates, runtimes/redists/drivers, dev tools Git/Go/Python/Node/PostgreSQL→
>   packages) and an explorer→"File Explorer" display-name override; (3) `LogCollectorService` gained a Windows
>   non-app blocklist (RuntimeBroker, tray/helper/updater suffixes, WindowsApps components), a CLI-runtime skip
>   set (node/dotnet/git/go/python… never auto-register as apps), auto-detected browsers now get IsBrowser+
>   WebBrowser categories, real RAM via `GlobalMemoryStatusEx` P/Invoke (was a hardcoded fake) and GPU via
>   PowerShell `Win32_VideoController` (was always empty on Windows); (4) `HardwareDeviceWatcherService` is no
>   longer Linux-only — new Windows PnP implementation polls `Get-CimInstance Win32_PnPEntity` every 30s,
>   keys open rows by `PNPDeviceID`, closes rows when a device disappears, and maps PnP classes
>   (DiskDrive→storage, KeyBoard/Mouse/HID→input, Camera/Monitor→display, Media/AudioEndpoint→audio,
>   Bluetooth/USB→usb). Verified: `dotnet build` 0/0 and the new winget parser was validated against the real
>   machine output (125 of 153 rows correctly skipped).
> - 2026-08-08: **Windows journey fixes — real incognito flag + junk-data sweep.** (1) **Incognito was mis-detected on Windows:** Chrome/Edge never put "incognito" in the window TITLE (the title heuristic only ever matched Firefox's "Private Browsing"), so incognito journeys were stored with `"incognito":false`. The `WindowsUiaBrowserReader` now also scans the window's accessibility tree for the dedicated **"Incognito"/"InPrivate" toggle button** Chrome and Edge expose (verified live via UIA) — the flag is now truthful and the URL is still captured (incognito capture is on). (2) **Junk-session root cause fixed:** `SearchApp` was treated as a BROWSER because the `"arc"` hint **substring-matched** `SearchApp` — `IsBrowserProcess` now matches hints as STANDALONE WORDS only (`arc` no longer matches `SearchApp`; `edge` no longer matches `msedge`-unrelated names). (3) **Runtime auto-registration disabled on Windows** — it was the factory for every junk `installed_applications` row (svchost → "Windows Services", dllhost → "Windows DCOM Host", conhost, Video.UI, SearchApp, M365Copilot…): `ResolveAppInfoInner` now refuses rows with an **empty `desktop_id`** (real inventory rows always carry a Start Menu shortcut / registry key / .desktop id) and the Windows structural gate (C:\Windows tree + PE CUI subsystem) now runs BEFORE the fuzzy matcher (powershell.exe was fuzzy-matching the "Windows PowerShell ISE" row → 7 junk console sessions). New boot sweep `CleanupWindowsJunkSessionsAsync` deletes empty-desktop_id app rows and closes their sessions. Tracking is now strictly driven by the `installed_applications` inventory.
> - 2026-08-07: **Analog audio device tracking** — `HardwareDeviceWatcherService` now monitors
>   headset/headphone analog audio jacks via `amixer` polling in addition to USB devices. Detects
>   plug/unplug events for 3.5mm audio jacks and stores them as `audio`/`headphone` class devices
>   in `hardware_devices` table. Fixes issue where headsets/earphones connected via analog jack
>   were ignored. Linux-only (requires `amixer`/ALSA).
> - 2026-08-07: **Private/incognito window URLs captured — Firefox via AT-SPI `DocURL`, Chrome needs one launch flag.**
>   Live-DB root-causing showed private journeys rotated only when the profile-History fallback happened to match a URL;
>   when it didn't (private visits, direct-opened sites) rotation froze and URLs stayed empty. Three fixes landed together:
>   (1) `AccessibilityBrowserTracker` now rotates on title change after a bounded ~25s historyLag timeout even with no URL
>   (was: held forever waiting for history) — same-tab navigation in private windows always creates the next record;
>   (2) `LogCollectorService` main loop no longer closes browser sessions owned by the accessibility tracker — it hydrated
>   ALL open sessions but only browser processes were in its current-key set, so browser sessions were silently closed
>   4-40s after opening while the windows stayed up; browser-owned sessions are now excluded from the close phase
>   (verified: sessions stay open 60s+); (3) URL sources for private windows: **Firefox builds its full AT-SPI tree on
>   demand**, so `LinuxAtSpiBrowserReader` now reads the `DOCUMENT_WEB` (role 95) node's `DocURL` attribute — the EXACT
>   private-window URL (verified live: pakprivatehire + wikipedia rows with `metadata_json
>   {"source":"accessibility","incognito":true,"processName":"firefox",...}`); normal Firefox gets exact URLs from
>   this source too (no more history false-positives). **Chrome 136+ is the hard case:** verified on Chrome 151 that a
>   running instance's a11y tree has ZERO children below the window frame (basic mode only), and BOTH runtime wake-up
>   signals (org.a11y.Bus `ScreenReaderEnabled` PropertiesChanged + legacy signal) are ignored — the tree is only built
>   when Chrome is launched with `--force-renderer-accessibility` (process-wide). With the flag, the omnibox ENTRY node
>   (`"Address and search bar"`, role 79) exposes the exact URL for EVERY window — including incognito windows opened
>   normally via Ctrl+Shift+N (verified: `https://en.wikipedia.org/wiki/Incognito` with `"incognito":true`).
>   Normal-mode Chrome tabs also fall back to the profile History DB; incognito writes nothing to disk so the flag is the
>   only source. Applied a user-level `~/.local/share/applications/google-chrome.desktop` with
>   `--force-renderer-accessibility` on ALL Exec lines (main + NewWindow + NewIncognitoWindow actions); Chrome must be
>   relaunched once for it to take effect. Also fixed the installed `--background` systemd service crash on Wayland:
>   `Program.cs` no longer initializes the Avalonia/X11 UI in background mode (the unit hardcodes a stale
>   `XAUTHORITY=~/.Xauthority`; the real Xwayland auth is `/run/user/<uid>/.mutter-Xwaylandauth.*`), so the installed
>   service stays active instead of dying at startup. Verified end-to-end in the INSTALLED build: service active, Chrome
>   incognito journey (title + exact URL + incognito flag) in `app_items`, Firefox private journeys intact.
> - 2026-08-07: **Killed the `dpkg-query: no packages found matching ${Version}` startup noise** — the apt package scan's
>   `-f` format string contained literal TAB characters, and .NET's Unix argument parser splits argv on tabs too, so
>   `${Version}`/`${Maintainer}`/`${Section}` reached dpkg-query as package-name PATTERNS (3 stderr errors per scan, 6
>   per startup). The format is now double-quoted so it arrives as ONE argument with `\t`/`\n` escapes for dpkg-query to
>   interpret, and stderr is drained. Verified: `dpkg-query -W -f='${Package}\t...'` returns proper rows and the log is
>   clean.
> - 2026-08-07: **Quiet terminal: per-event browser-journey logs demoted to Debug** — `AccessibilityBrowserTracker`
>   emitted "Browser journey opened / window closed / idle-closed / download recorded" at Information level, flooding the
>   terminal during `dotnet run` (the DB is the source of truth for journeys). All four are now LogDebug (visible only
>   with `ALPHA_LOG_LEVEL=debug`); the startup banner stays at Information.
> - 2026-08-06: **snap Firefox private-window capture — surgical AppArmor AT-SPI fix.** Why private
>   Firefox was invisible even after the journey overhaul: Ubuntu's snap Firefox AppArmor profile
>   denies EVERY inbound D-Bus call from outside the sandbox — including the AT-SPI accessibility bus
>   (`AppArmor policy prevents this sender... label="snap.firefox.firefox (enforce)"` verified live).
>   And Firefox itself never writes private windows to sessionstore or places.sqlite, so no disk source
>   exists. New `publish/firefox-a11y-apparmor.sh` (idempotent, `--undo` supported) loads a *surgical*
>   copy of the snap profile adding ONE rule — `dbus (receive)` — that lets the AT-SPI bridge call INTO
>   Firefox while the sandbox stays fully enforcing (verified `/sys/kernel/security/apparmor/profiles`
>   shows `(enforce)`, NOT complain; peer-agnostic because the tracker's sender label can be
>   `unconfined` OR `vscode (unconfined)` depending on how it's spawned). Installs a systemd oneshot
>   (`alpha-ai-firefox-a11y.service`, enabled) that re-applies the override at boot and after
>   `snap refresh firefox` (snapd regenerates the base profile). Firefox must be restarted once after
>   applying (AppArmor mode is fixed at process exec). Verified end-to-end on this machine: the tracker
>   now opens `PRIVATE — Mozilla Firefox Private Browsing` with `metadata_json {"source":"accessibility",
>   "incognito":true,"processName":"firefox",...}` stored in `app_items` — private-window presence,
>   title, and flag captured; the URL is still not exposed by Firefox itself on Linux (no location-bar
>   node in the a11y tree, no sessionstore entry) — an extension would be the only source, which the
>   project has rejected. Note: deb/non-snap Firefox needs no override (already AT-SPI reachable).
> - 2026-08-06: **Browser-journey overhaul — all browsers incl. Firefox + incognito, duplication eliminated.**
>   Root causes found in the live DB (52x duplicate `browser_tab` rows, 316 items written into already-
>   closed sessions, sessions opening/closing every 5-13s): (1) window identity was the AT-SPI registry
>   path, which churns on navigation, and the PID-based re-key **stole** one window's session for another
>   when 2+ windows shared one PID (Chrome/Edge/Firefox all do); (2) snap Firefox is AppArmor-blocked from
>   AT-SPI entirely, so it was never captured. Fixes: `LinuxAtSpiBrowserReader` rewritten to merge THREE
>   sources — AT-SPI (Chrome & co.), **Firefox sessionstore** (`recovery.jsonlz4`/`sessionstore.jsonlz4`,
>   decompressed by an embedded pure-python LZ4 block decoder — no external module, no installer asset;
>   gives exact per-tab URLs that survive the snap sandbox), and the **WM window list** (xprop + GNOME Shell
>   introspect) for stable window ids. `AccessibilityBrowserTracker.ResolveWindowKey` now re-keys by
>   page-title match (multi-window) or single-window count guard (macOS/wayland navigation) — stealing is
>   impossible; missing-window grace raised 3→5 polls. `ALPHA_BROWSER_CAPTURE_INCOGNITO=true` (was false).
>   Main `LogCollectorService` skips browsers when browser tracking is enabled (no more double sessions per
>   window; hints-based skip covers the post-wipe window before the catalog scan). DB wiped keeping login.
>   Verified live: 3 Chrome windows (incognito flagged) + 2 snap-Firefox windows with exact URLs.
> - 2026-08-06: **Hybrid URL fallback — browser profile History reader (no restart, all browsers).**
>   Chrome 136+ (verified on Chrome 151) ignores every AT-SPI enablement switch on Linux, so a11y
>   gave titles but empty URLs unless the browser was relaunched with `--force-renderer-accessibility`
>   (restart = rejected). NEW `Core/BrowserAccessibility/BrowserHistoryReader.cs` reads each browser's
>   own profile history database — Chromium-family `History` (Chrome/Edge/Brave/Opera/Vivaldi/
>   Chromium) and Firefox `places.sqlite` — WHILE the browser runs: DB + `-wal`/`-shm`/`-journal`
>   sidecars copied to a temp dir and opened read-only (torn snapshots can't corrupt anything;
>   failures are swallowed and retried next poll), change signatures throttle re-reads, and URL
>   resolution is STRICTLY title-match (no unconditional newest-visit fallback — multi-window setups
>   never get a misattributed URL). Generic scans of `~/.config` and `~/.mozilla` (plus per-platform
>   roots on Windows/macOS) catch brand-new browsers — install→use→uninstall-in-5-min journeys are
>   captured before the uninstaller deletes the diary. Verified live on this machine: running Chrome
>   151 (`Web analytics - Wikipedia` → exact URL), snap Firefox (AppArmor blocks D-Bus but NOT file
>   reads → URL recovered from `places.sqlite`), a fresh brand-new profile, and a full tracker run
>   (DB rows show title + full URL + domain with `metadata_json.source="history"`).
>   `AccessibilityBrowserTracker.EnrichUrlsFromHistoryAsync` fills empty URLs before the tab logic;
>   URL-less title changes no longer rotate tabs until history catches up (kills the spurious
>   empty-URL rotation). New `AccessibilitySnapshot.UrlSource` (`a11y`|`history`), shared
>   `StripBrowserSuffix` helper, config knobs `ALPHA_BROWSER_HISTORY_ENABLED` (default true) +
>   `ALPHA_BROWSER_HISTORY_POLL_SECONDS` (default 10); `.env.example` + `--print-config` updated.
>   Pure code — no installer asset changes (config flows through the existing `config.enc` pipeline).
> - 2026-08-06: **Per-page browser_tab records (no more title overwrite)** — `AccessibilityBrowserTracker`
>   used to UPDATE the single `browser_tab` root item in place on every navigation (ON CONFLICT DO
>   UPDATE replaces `title`), so "YouTube - Google Chrome" silently became the video page. Now each
>   page visit CLOSES the previous tab root — via a dedicated `CloseAppItemAsync` that also resets
>   `is_synced=0` so the server learns the close even for already-synced rows — and OPENS a fresh
>   `browser_tab` record; `browser_navigation` children still record transitions. Title-only changes
>   rotate only after a ~10s stability window (badge/timer flicker is filtered); real URL changes
>   rotate immediately. The Linux/Chrome empty-URL gap is RESOLVED below (2026-08-06 history-reader
>   fallback fills URLs from the browser's own profile History DB while it runs — no restart).
> - 2026-08-06: **Crash-loop fix: duplicate-PID open sessions in `SessionHierarchyResolver`** — the
>   accessibility browser tracker writes one `app_sessions` per browser window (ProcessId = browser main
>   pid) while the process collector writes its own session for the same browser process, so two open
>   sessions can share one `process_id`. The resolver's `existingOpen.ToDictionary(r => r.ProcessId)`
>   threw `ArgumentException` on every collection cycle and killed the whole tracking loop whenever a
>   browser was open. Now dedupes per PID (keeps the earliest record, logs a warning).
> - 2026-08-06: **Installer config refresh fix (stale `config.enc` shadowing)** — installed builds load
>   `~/.config/alpha-ai-tracker/config.enc` (the machine-key copy) BEFORE the freshly baked `config.enc`
>   next to the binary, and previously never refreshed it — so rebuilding the installer with a changed
>   `.env` (e.g. a new server IP) had no effect on machines that already ran once. `dotnet run` always
>   looked fine because it reads the plaintext `.env`. `EnvLoader.Load()` now compares the DECRYPTED
>   content of the shipped copy vs the user copy and replaces the user copy when they differ (then
>   re-migrates to the machine key); corrupt user copies self-heal from the shipped copy; non-writable
>   user dirs load the shipped copy directly. Added `--print-config` headless CLI to print which config
>   an installed build resolves (e.g. `client/publish/linux/client --print-config`).
> - 2026-08-05: **Option B — accessibility-tree browser journey (debugger pipeline DELETED).**
>   Chrome 136+ real-profile debugging is closed by every mechanism (verified on this machine:
>   same-path `--user-data-dir` fails headless + GUI; `RemoteDebuggingAllowed`/`DevToolsAvailability`
>   policy fails too). With extensions ruled out, the entire debugger pipeline (`client/Core/Browser/*`
>   — 16 files) was REMOVED and replaced by `client/Core/BrowserAccessibility/*`: the tracker reads the
>   OS accessibility tree — the same tree screen readers use — so the employee's REAL browser journey
>   is captured on every platform and every Chrome version, with no debugger, no extension, and no
>   catalog dependency (install→use→uninstall-in-5-min browsers are captured; headless instances are
>   skipped). Linux: python3+AT-SPI D-Bus probe (validated live on Chrome 151 — the omnibox ENTRY node
>   `"Address and search bar"` exposes the exact URL/query; Name/Role are D-Bus properties, text via
>   `org.a11y.atspi.Text`). Windows: UIA via `Interop.UIAutomationClient` (netstandard2.0, API verified
>   by reflection). macOS: osascript/System Events (best-effort; Accessibility grant).
>   `AccessibilityBrowserTracker` polls every `ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS` (3), writes one
>   `app_sessions` per window + `browser_tab`/`browser_navigation` `app_items` (exact URL + domain),
>   closes vanished windows after 3 polls, idle-closes after `ALPHA_BROWSER_JOURNEY_IDLE_MINUTES` (15),
>   closes gracefully on shutdown (no orphans), records Downloads as `browser_download` items, and gates
>   incognito URLs behind `ALPHA_BROWSER_CAPTURE_INCOGNITO=false` (legal-safe default; incognito windows
>   are still detected + flagged in metadata). Dead code removed: shell-command collectors (interface +
>   3 impls + `ShellCommand` model), server dead DTOs (`BrowserContext*`, `FileExplorerContext*`, `Url*`,
>   `UrlVisit*`, `ShellCommand*`), `publish/install-extensions.sh` + the `extensions/` bundling step.
>   Fixed the `SqliteLogStore` fuzzy-match AND/OR precedence bug (a junk `chrome` row beat `Google Chrome`;
>   now prefers `is_browser` rows + shortest binary). Env knobs are now a11y-only (`.env` + `.env.example`
>   updated per installer parity). Verified: `dotnet build` 0 errors / 27 warnings, `go build`/`go vet`
>   clean, and a live runtime test captured a real Chrome journey into SQLite (exact URL, graceful close).
> - 2026-08-03: **Installer-Parity rule + single-instance/tray UX fix** — codified a MANDATORY rule (see §6 below): every feature or modification must ALSO be wired into the installer functionality, and verified in an installed build — `dotnet run` alone is NOT a valid release test, because installed builds ship the publish output plus only what the `publish/*` scripts bundle (extensions/, publish/*.sh, config.enc) and run from a root-owned dir. Client fix behind the rule: a second launch now restores the running window (`client/SingleInstance.cs` named-event signaling), the tray menu gains **Quit**, and tray-less desktops (no StatusNotifierWatcher — `client/Services/TrayAvailability.cs` D-Bus check) quit on window-close instead of stranding an invisible process.
> - 2026-08-01: **Docs audit (AGENTS.md + client/server ARCHITECTURE.md)** — removed all stale `activity-logs/sync` and `shell-commands/sync` references (both endpoints/tables/code are deleted from the product). Fixed the mermaid diagram + cross-service contracts to show the real 7 sync endpoints. Server: migration inventory corrected to 001–016 (15 files), added `jobs/staleness_sweep.go`, `employee_installed_applications`/`employee_installed_packages` junctions, and all 12 Postgres tables; API surface now lists `GET /app-sessions` + `GET /app-items` and drops the removed `activity-logs` section. Client: corrected SQLite schema (11 tables incl. `storage_devices`, `permission_status`, `app_items`), documented the `MigrateSql` migration strategy, the real sync batch size (500), the file logger (`dotnetrunlog.txt`), `install-extensions.sh`, and flagged `IShellCommandCollector`/`ShellCommand` as dead code. Branch updated to `restructureClient`.
> - 2026-07-31: **Cross-service payload + employee↔catalog dedup (migrations 013–016)** — tracking tables were washed out (backup at `server/bin/backup/alpha_ai_tracker_20260731_134750.dump`) and server + client landed together. 013 adds `installed_app_id`/`installed_package_id`/`grouped_by`/`cgroup_scope`/`context_label` to `app_sessions`; 014 adds `process_id` + 9 journey fields to `app_items`; 015/016 add company-global app/package catalogs (`app_fingerprint=desktop_id|binary_name`, `package_fingerprint=package_name|source_manager`) with junction tables `employee_installed_applications`/`employee_installed_packages` (per-install version/path/date + `first_seen_at`/`last_seen_at`/`is_active`; FKs → `employees(employee_id)` VARCHAR per convention). `SyncInstalledApps`/`SyncInstalledPackages` rewritten to upsert-catalog-then-link in one tx. Hourly `jobs/staleness_sweep.go` deactivates links idle > `LINK_STALE_DAYS=7`. Client: installed-apps mapper sends `binaryName`/`isBrowser`/`desktopId`/`categories`; app-sessions sends `groupedBy`/`cgroupScope`/`contextLabel`; app-items sends `processId` + 9 journey fields; apps conflict-update resets `is_synced=0`; packages dedup to `ON CONFLICT(package_name, source_manager)` + unique index (fixes 6,530 duplicate package rows from the old `ON CONFLICT(id)`). Smoke-tested: happy path, two-employee-same-app → 1 catalog + 2 links, malformed/partial payloads → 4xx/200 not 500.
> - 2026-07-31: **Atomic cascade-close of app_items with sessions** — the main collection loop closed sessions but never closed their `app_items` (11 orphaned open items on already-closed sessions in the live DB). New composite `CloseSessionsAndAppItemsAsync()` store method acquires the connection gate ONCE and closes sessions + items in ONE transaction (a crash mid-close can no longer leave orphans). Wired into all 4 close paths: main-loop normal close (was missing entirely — root cause of the orphans), `ReconcileStaleSessionsOnBootAsync`, garbage cleanup, and non-GUI cleanup. Per-tab closes in `NativeMessageService`/`JourneyEngine` still use `CloseAppItemsBySessionIdsAsync`.
> - 2026-07-31: **Terminal classification + PPID walk logging** — `TerminalEmulators` set expanded (`gnome-terminal-server`, `foot`) so flatpak/snap-packaged terminals resolve correctly without `Categories=` metadata. `SessionHierarchyResolver` now takes an optional `ILogger` and logs `ResolveParent` PPID walks at Debug level (wired from `LogCollectorService`).
> - 2026-07-30: **Software classification pipeline** — Added 3 new files: `SoftwareCategoryResolver.cs` (metadata-driven category resolution from .desktop Categories, macOS bundle IDs), `SoftwareClassifier.cs` (joint dedup pipeline — GUI apps from `InstalledAppDetector` win over matching package entries from `PackageDetector`), `SoftwareIdentityResolver.cs` (stable SHA-256 identity for cross-source dedup). Refactored `AppProcessClassifier.cs` — `FileManagerProcesses`/`IdeProcesses` renamed to `FileManagerFallbacks`/`IdeFallbacks`, added `ResolveCategory()` with metadata-first approach, added `ResolveRootItemType()` overload accepting `categories`/`desktopId` params. Upgraded `InstalledAppDetector.cs`: Linux .desktop scanning now follows `$XDG_DATA_DIRS` (snap/flatpak dirs), macOS bundle inspection returns `CFBundleIdentifier` + browser detection via `CFBundleURLSchemes`, browser detection for Linux via `Categories=WebBrowser`, Windows via `URLAssociations` http/https. Added server migration 012 (`desktop_id`, `categories`, `is_browser` columns on `installed_applications`).
> - 2026-07-30: **File logger for dotnet run** — Added `FileLoggerProvider.cs` (writes `ILogger` output to `dotnetrunlog.txt`) and registered it in `Program.cs`.
> - 2026-07-30: **GUI-apps-only tracking gate** — New tracking rule: only processes that resolve to `installed_applications` rows or are detected as GUI apps (have .desktop files on Linux, .app bundles on macOS, Start Menu/Program Files entries on Windows) are tracked. Removed shell-process-always-tracked, build-tool auto-registration, runtime auto-registration, and package fallback tracking from `ResolveAppInfoInner`. Added `IsGuiApplication()` to `IInstalledAppDetector`/`InstalledAppDetector` with `CheckGuiPath()` (cross-platform). Simplified main loop filter. Removed `_knownPackageNames`, `CloseStalePackageSessionsAsync`, `runningPackageIds`. Renamed `AutoDetectInstalledApp` → `AutoDetectInstalledGuiApp`. Historical data preserved as-is. 4 files changed.
> - 2026-07-30: **Fixed app_display_name resolving to raw GUID in NativeMessageService** — `ResolveBrowserAppIdAsync` cached and returned `app.Id` (GUID) which the caller stored as `AppDisplayName`. Fixed: `_browserAppCache` now stores `(id, displayName)` tuple; method renamed to `ResolveBrowserAppAsync` with `(string? id, string? displayName)` return; caller uses `browserDisplayName` for `AppDisplayName`.
> - 2026-07-30: **Chromium/Electron headless subprocess filter (cross-platform)** — Added `GetProcessCommandLine()` via PowerShell (Windows) and `ps -o command=` (macOS). Centralized `ChromiumSubprocessFlags` array + `IsHeadlessSubprocess()` in `AppProcessClassifier` for all 3 platforms. Linux renamed `IsChromeSubprocess()` → `ReadProcessCmdline()` using shared check. Startup cleanup: `CleanupGarbageSessionRowsAsync()` closes old rows with `--type=` flags or long process names.
> - 2026-07-30: **Dynamic browser suffix stripping** — Removed hardcoded `BrowserSuffixes` array (12 entries) from `ActivityContextParser`. Added `appDisplayName` parameter to `Parse()` → threaded to `ParseBrowserContext()`. Suffix stripped dynamically using the real app name from `installed_applications`. No generic regex fallback — only strips when `rootItemType == "browser_tab"`.
> - 2026-07-30: **SemaphoreSlim concurrency gate on SqliteLogStore** — Added `SemaphoreSlim(1,1)` guarding all public methods using `_connection`. Private ungated helpers (`SetStatusCoreAsync`, `GetEmployeeInfoCoreAsync`) avoid reentrancy deadlock from composite methods. Added `PRAGMA busy_timeout = 5000;`. Added `GatedTransaction` wrapper releasing gate on `DisposeAsync`. Fixed `BeginTransactionAsync` gate-leak edge case.
> - 2026-07-30: **FileSystemEventWatcher exclusion list + per-process resilience** — Added `ExcludedDirectoryPrefixes` (Waydroid, Flatpak, Snap, cache, trash, containers, Steam, per-platform temp/cache). Removed `UserProfile` from `WatchDirectories`. Added inner try/catch around each `ResolveAppInfo` call so one bad process can't abort the entire collection cycle.
> - 2026-07-29: **Fixed false positive "Connected" status in browser extension detection** — `IsExtensionActiveAsync()` was purely process-based (`pgrep -f native-host.py` + `pgrep ^chrome$`), producing false `ExtensionActive` when orphaned `native-host.py` processes or unrelated Chrome instances were running. Replaced with socket-level heartbeat: extension's background.js already sends `{action:"ping"}` every ~27s via `chrome.alarms`; `native-host.py` now forwards pings to the tracker socket (was swallowing them); `NativeMessageService` records `_lastHeartbeatAt` timestamp; `BrowserExtensionService.IsExtensionActiveAsync()` queries `NativeMessageService.IsExtensionConnected()` (60s threshold). Added `KillOrphanedNativeHostProcesses()` startup cleanup. `NativeMessageService` registered as singleton+hosted for DI injection into `BrowserExtensionService`. Three files changed: `native-host.py`, `NativeMessageService.cs`, `BrowserExtensionService.cs`, `Program.cs`.
> - 2026-07-29: **Fixed GNOME daemon contamination via Xwayland empty binary_name** — Xwayland `.desktop` file has no `Exec=` line, so `InstalledAppDetector` stored it with `binary_name=""`. `GetInstalledAppByBinaryNameFuzzyAsync()` SQL `$name LIKE '%%'` (empty binary) matched every process — causing all GNOME services + Chrome to resolve to the Xwayland entry. Fixed by: (1) `WHERE binary_name != ''` in fuzzy match SQL, (2) `NonAppProcesses` expanded with 16 GNOME daemons + prefix-matching array, (3) `KernelNamePrefixes` in `ProcessFilter.cs` for first-stage filter, (4) `NoDisplay=true` + `Type!=Application` gate in `AddAppFromDesktopFile`. DB cleaned: Xwayland entry patched with proper `binary_name`, orphaned sessions closed, Chrome display names restored.
> - 2026-07-29: **Added File Explorer journey tracking** — Full event-driven desktop event bus for file manager operations (Nautilus, Dolphin, Thunar, Nemo, etc.). Three watchers: `ATSPIEventWatcher` (Tmds.DBus.Protocol → AT-SPI `focus:`/`window:` events, detects foreground file manager via `xdotool` + `/proc/cwd`), `FileSystemEventWatcher` (FileSystemWatcher on 7 user directories), `RecentFilesWatcher` (XBEL monitor at `~/.local/share/recently-used.xbel`). `EventCoordinator` deduplicates (3s), correlates (500ms), normalizes raw→business events. `JourneyEngine` resolves `AppSession`, creates `AppItem` rows with 9 journey fields (`object_type`, `action`, `journey_id`, `sequence`, `previous_path`, `current_path`, `window_id`, `tab_id`, `metadata_json`). `IObservableEventSource` interface for all watchers — future IDE/terminal/Office integrations plug in the same way. Coexistence: `item_type` preserved, auto-derived from `object_type`+`action`; browser pipeline untouched. NuGet: `Tmds.DBus.Protocol` v0.94.2.
> - 2026-07-28: **Added browser extension journey tracking** — Chrome MV3 extension (`extensions/chrome/background.js`) captures real-time tab navigations (URL, title, tabId, windowId) via `chrome.tabs.onUpdated/onActivated/onCreated/onRemoved`. Sent through native messaging (`chrome.runtime.connectNative`) → `native-host.py` (Native Messaging stdio bridge) → `NativeMessageService` (Unix socket listener) → SQLite `app_items` as `browser_tab`/`browser_navigation` entries with `url`/`domain` fields.
> - 2026-07-28: **Added `NativeMessageService`** — `BackgroundService` listening on Unix domain socket (`~/.local/share/alpha-ai-tracker/native-messaging.sock`) for browser navigation events. Maintains `_tabSessionCache` mapping `browser:tabId` → `AppSession.Id`. Stores `browser_tab` root items + `browser_navigation` child items per navigation.
> - 2026-07-28: **Added `BrowserExtensionService`** — Detects installed browsers (Chrome, Chromium, Edge, Brave, Opera, Vivaldi, Firefox). Two-strategy extension installation: (1) `--load-extension` with `--no-first-run` for Chromium-based browsers, (2) profile injection via Python SHA256→extension-ID → `Preferences.json` edit as fallback for branded Chrome 150+. Async-safe with `Task.Delay` polling. Extension detection via NativeMessageService socket-level heartbeat (replaced process-based `pgrep` on 2026-07-29).
> - 2026-07-28: **Added `url`/`domain` columns to `app_items`** — client SQLite schema extended with `url TEXT` and `domain TEXT`. Server-side migrations and DTOs updated. NativeMessageService stores parsed URLs with proper domain extraction.
> - 2026-07-28: **Added `ComputeExtensionId` helper** — runs Python SHA256→a-p alphabet to compute Chrome extension ID from directory path. Used for native messaging manifest `allowed_origins`.
> - 2026-07-28: **Fixed `InstallNativeHostManuallyAsync`** — now computes and includes the extension ID in `allowed_origins` (was empty array, breaking native messaging).
> - 2026-07-28: **Fixed extension active detection** — replaced `IsExtensionConnectedAsync` (socket-based `fuser` which failed because native-host.py only holds ephemeral connections) with `IsExtensionActiveAsync` (process-based `pgrep native-host.py` + `pgrep chrome`). Reliable on all platforms. *(Note: replaced again 2026-07-29 with socket-level heartbeat for precision.)*
> - 2026-07-28: **Removed `--enable-automation` from Chrome launch args** — was triggering GCM errors (`QUOTA_EXCEEDED`, `DEPRECATED_ENDPOINT`) and "controlled by automation" banner. Unnecessary: `--load-extension` works without it on Chromium/Brave/Edge, and branded Chrome 150+ blocks it regardless.
> - 2026-07-28: **Added crash-safe session ended_at tracking** — heartbeat persisted every cycle (`last_heartbeat_at` in `app_status`), stale heartbeat detection on boot, and automatic reconciliation of orphaned sessions with the last heartbeat time as approximate crash time. Includes cross-platform `GetSystemUptime()` for diagnostic logging. Handles poweroff, process crash, and fast restart scenarios.
> - 2026-07-27: Full activity hierarchy engine — PID-persisted sessions, `SessionHierarchyResolver` (node→terminal→IDE), browser `browser_navigation` URL items, file manager `folder`/`file` path items, 30s context dedup cooldown.
> - 2026-07-27: Added `process_id` / `parent_process_id` to `app_sessions` (client SQLite + server migration 010).
> - 2026-07-27: Added `binary_name` to `installed_applications` model/SQLite table for process→display-name mapping.
> - 2026-07-27: Added `installed_app_id` / `installed_package_id` FK columns to `app_sessions` (client SQLite only).
> - 2026-07-27: Replaced in-memory `IsInstalledApp()` filter with SQLite-backed `ResolveAppInfo()` — processes not in DB get auto-detected via filesystem heuristics and saved before tracking.
> - 2026-07-27: Fixed Linux ProcessCollector `resolvedTitle ??= name` bug (was assigning fake titles to all processes, bypassing window-title filter).
> - 2026-07-27: Added process-tree-based parent-child tracking for terminal shells inside IDE/terminal-emulator sessions (via `ParentItemId` fixup pass).
> - 2026-07-27: Added `waydroid`/`gnome-software` to `NonAppProcesses` blocklist.
> - 2026-07-27: `AppDisplayName` now uses the real app name from `installed_applications.app_name` (e.g., "Visual Studio Code"), not the process name ("code") or window title.
> - 2026-07-27: Removed hardcoded `CommonKnownApps` list from `InstalledAppDetector` — app detection is now 100% dynamic from OS (.desktop files, registry, .app bundles).
> - 2026-07-27: **Added `BuildToolProcesses` set** — `make`, `go`, `npm`, `npx`, `cargo`, `pip`, `tsc`, `rustc`, etc. Auto-registered as `installed_packages` (category=`tool`) on first sight, tracked without window.
> - 2026-07-27: **Fixed process filter bug** — processes with `appId != null` or `IsBuildTool()` are now tracked even without a window title. Wayland-native apps (VSCode, Chrome) don't appear in X11 window list and were silently dropped.
> - 2026-07-27: **Broadened auto-detect paths** — `/home/*` and `/media/*` now accepted as valid install locations (project-local compiled binaries like `alpha-ai-server` in `./bin/`).
> - 2026-07-27: **Fixed file manager path resolution** — folder display names now resolved to absolute paths by checking `~/`, `~/Documents`, `~/Desktop`, `/media/<user>/`.
> - 2026-07-27: **Fixed `SessionHierarchyResolver`** — PPID walk now traverses build tools and runtimes as intermediate steps; `ShouldLinkTo` accepts build tools as children of IDEs and terminals.
>   **Overall completion (honest):** ~50% across all 3 services

> **⚠️ MANDATORY RULE — Installer parity (`dotnet run` ≠ installed build).** `dotnet run` compiles from the source tree, so it always runs the newest code. Installed/released builds (`bash publish/build-installer.sh`, `bash publish/release.sh`) ship the **publish output** plus whatever the build scripts explicitly bundle. **A change is NOT "done" until it works in an installed build** — any new functionality or modification must also be added to the installer functionality. See §6 → *Installer-Parity Rule* for the full checklist.

---

## 1. Project Overview

Alpha AI Tracker is an employee monitoring and productivity analytics system consisting of three services:

1. **Desktop Client** (`client/`) — Installed on employee machines. Collects process activity, window titles, CPU/memory usage, and installed app/package information. Sends data to the central server via REST.
2. **Server** (`server/`) — Go + Echo + PostgreSQL + Redis. Central API hub. Receives and stores client data, exposes admin-facing REST API for the web dashboard. Handles authentication for both web admins and employee desktop clients.
3. **Web Dashboard** (`web/`) — Next.js 15 App Router. Admin-facing UI for viewing employee data, managing departments, generating login secrets, and analytics. Most pages currently render mock localStorage data rather than calling the real API; the employees list, departments, logs, and the Employee Journey + Device Specs modules (2026-08-18) call real APIs.

---

## 2. System Architecture Diagram

```mermaid
flowchart LR
    EMP[Employee Machine\nDesktop Client\n.NET 10 / Avalonia UI] -->|REST / JSON\nPOST /api/v1/{device-hardware,\ninstalled-apps,\ninstalled-packages,network-info,\nsession-events,app-sessions,app-items}/sync| SRV[Go Server\nEcho v4 / PostgreSQL\nPort 8080]
    EMP -->|POST /api/v1/auth/employee-login| SRV

    SRV -->|Query / Store| PG[(PostgreSQL)]
    SRV -->|Store/Validate\nOne-Time Secrets| RD[(Redis\n5-min TTL)]

    WEB[Web Dashboard\nNext.js 15 / React 18\nPort 3000] -->|REST / JSON\nhttpOnly Cookie Auth\nvia Next.js Rewrites proxy| SRV

    note_ws[⚠️ WebSocket / SSE / polling:\nNOT IMPLEMENTED\nWeb dashboard polls no API\nfor real-time updates]
  
    style EMP fill:#2d2a4e,color:#fff
    style SRV fill:#1a3a4a,color:#fff
    style WEB fill:#3a2a1a,color:#fff
    style PG fill:#2d4a2d,color:#fff
    style RD fill:#4a2d2d,color:#fff
    style note_ws fill:#5a3a3a,color:#fff,stroke-dasharray: 5 5
```

> All 11 sync endpoints exist on the server (7 original + app-status/hardware-devices/permission-status/storage-devices, 2026-08-11). `activity-logs/sync` and `shell-commands/sync` were removed from the product entirely (client + server).

---

## 3. Service Breakdown Table

| Service           | Stack                                                                | Responsibility                       | Entry Point                                | Internal Doc                                      |
| ----------------- | -------------------------------------------------------------------- | ------------------------------------ | ------------------------------------------ | ------------------------------------------------- |
| **client/** | .NET 10, Avalonia 12.1, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite | Employee-side data collection & sync | `Program.cs`, `App.axaml.cs`           | [client/ARCHITECTURE.md](./client/ARCHITECTURE.md) |
| **server/** | Go 1.25, Echo v4.15, pgx v5.10, go-redis v9.21                       | Central API hub, data storage, auth  | `cmd/server/main.go`                     | [server/ARCHITECTURE.md](./server/ARCHITECTURE.md) |
| **web/**    | Next.js 15.3.4, React 18, Redux Toolkit, TanStack Query              | Admin dashboard & analytics          | `next.config.ts`, `src/app/layout.tsx` | [web/ARCHITECTURE.md](./web/ARCHITECTURE.md)       |

### Cross-cutting docs

| Doc | Answers |
|---|---|
| [FILE_HIERARCHY.md](./FILE_HIERARCHY.md) | *Where does this file live and who owns it?* — annotated node tree of all three services |
| [WORKFLOW.md](./WORKFLOW.md) | *How do I do X?* — dev loop, adding a GUI page, re-branding, version bump, adding a runtime asset, release |
| [client/UI_ARCHITECTURE.md](./client/UI_ARCHITECTURE.md) | *How is the desktop UI built?* — design tokens, style classes, the 6 pages, router, binding patterns |
| [client/APP_IDENTIFIERS_README.md](./client/APP_IDENTIFIERS_README.md) | *How do I re-brand?* — every consumer of `APP_IDENTIFIERS` |
| [client/VERSION_README.md](./client/VERSION_README.md) | *How do I bump the version?* |
| [client/build.md](./client/build.md) | *How do I build installers?* |

---

## 4. Cross-Service Contracts

### Client ↔ Server

| Direction                                  | Protocol                                      | Auth Method                           | Format                                                   |
| ------------------------------------------ | --------------------------------------------- | ------------------------------------- | -------------------------------------------------------- |
| Employee login (client → server)          | REST POST`/api/v1/auth/employee-login`      | emp_id + secret_key (Redis-validated) | JSON`{employeeId, secretKey}` → `{employee, token}` |
| Device hardware sync (client → server)    | REST POST`/api/v1/device-hardware/sync`     | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Installed apps sync (client → server)     | REST POST`/api/v1/installed-apps/sync`      | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Installed packages sync (client → server) | REST POST`/api/v1/installed-packages/sync`  | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Network info sync (client → server)       | REST POST`/api/v1/network-info/sync`        | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Session events sync (client → server)     | REST POST`/api/v1/session-events/sync`      | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| App sessions sync (client → server)       | REST POST`/api/v1/app-sessions/sync`        | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| App items sync (client → server)          | REST POST`/api/v1/app-items/sync`           | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| App status sync (client → server)          | REST POST`/api/v1/app-status/sync`          | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Hardware devices sync (client → server)    | REST POST`/api/v1/hardware-devices/sync`    | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Permission status sync (client → server)   | REST POST`/api/v1/permission-status/sync`   | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Storage devices sync (client → server)     | REST POST`/api/v1/storage-devices/sync`     | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |

### Server ↔ Web

| Direction         | Protocol                                     | Auth Method                         | Format                                   |
| ----------------- | -------------------------------------------- | ----------------------------------- | ---------------------------------------- |
| Web admin login   | REST POST`/api/v1/auth/login`              | email + password → httpOnly cookie | JSON`{email, password}` → sets cookie |
| All web API calls | REST via Next.js rewrites (`/api/*` proxy) | httpOnly cookie (auto-sent)         | JSON request/response                    |

### Contract Documentation

**No formal contract documentation exists** beyond what is implicit in the Go DTO files (`server/internal/dto/`) and the TypeScript API client (`web/src/lib/api.ts`). These are manually kept in sync by the developer — there is no schema generation, OpenAPI spec, or shared type system.

### Inconsistencies Found

1. **Shell commands REMOVED (resolved 2026-08-05)** — Shell command collection/sync removed from client and server. No endpoint, no table (`shell_commands` dropped by migration 007). All dead-code leftovers (`IShellCommandCollector`, 3 platform impls, `ShellCommand` model, server DTOs) were deleted 2026-08-05.
2. **Legacy child tables removed (resolved)** — `browser_contexts`, `file_explorer_contexts`, `urls`, `url_visits` tables and all Go/C# code replaced by single generic `app_items` table. Remaining dead DTOs for those types deleted 2026-08-05.
3. **`activity_logs` removed (resolved)** — The old `activity_logs` table (server Postgres + client SQLite) and all Go/C# code referencing it have been removed. Migration 006 drops the table with no rollback. Replaced by relational `app_sessions` + `app_items`; the web Logs/Comprehensive page now queries `GET /app-sessions`.
4. **Field naming** — Client and server agree on `employeeId` (camelCase) for auth/sync payloads and camelCase field names throughout. **Consistent by convention, no validation schema enforces it.**
5. **Catalog owner column** — `installed_applications.employee_id` / `installed_packages.employee_id` on the server are legacy "first uploader" markers; dedup is now fingerprint-based (`app_fingerprint`, `package_fingerprint`) with per-employee junction tables holding the real per-install metadata.

---

## 5. Current Completion State

### Server — ~50% complete

**What works:**

- Migrations 001-017 run on startup (16 files; 010 adds `process_id`/`parent_process_id`, 013 adds session identity, 014 adds journey fields, 015/016 add app/package catalogs + junction tables, 017 adds app_status/hardware_devices/permission_status/storage_devices)
- Full CRUD for users, employees, departments
- Web admin auth (email/password → httpOnly cookie with encrypted JWT)
- Employee auth (Redis one-time secret → JWT token)
- 11 sync endpoints: device_hardware, installed_apps, installed_packages, network_info, session_events, app_sessions, app_items, app_status, hardware_devices, permission_status, storage_devices (+ synced_at for all; 2026-08-11 added the last four)
- Catalog dedup: apps keyed by `app_fingerprint` (desktop_id|binary_name), packages by `package_fingerprint` (package_name|source_manager), per-employee junction tables with `first_seen_at`/`last_seen_at`/`is_active`
- Hourly `jobs/staleness_sweep.go` deactivates junction links idle > `LINK_STALE_DAYS=7`
- App sessions + app items listing (`GET /app-sessions`, `GET /app-items`) with filtering/pagination
- Company admin auto-initialization on first run
- Graceful shutdown

**What's missing:**

- **No tests** (0 test files)
- **No rate limiting** on any endpoint (including login)
- **No structured logging** — uses `log.Printf` only
- **No Redis fallback** — if Redis is down, employee secret generation/validation fails (no DB fallback)
- **No observability** — no metrics, tracing, health check depth
- **No request validation library** — manual field checks in handlers
- **No data-pruning job** — app_sessions/app_items accumulate indefinitely (staleness sweep only deactivates catalog links)
- **No listing endpoints** for installed-apps, installed-packages, device-hardware, network-info, session-events (sync-only tables)
- **Dead DTOs** — `BrowserContext*`, `Url*`, `UrlVisit*`, `ShellCommand*` types still in `new_schema_dto.go`

### Client — ~85% complete

**What works:**

- Cross-platform process collection (Win/Linux/macOS)
- **Crash-safe session ended_at tracking** — heartbeat persisted every cycle, stale heartbeat detection on boot closes orphaned sessions with approximate crash time. Handles poweroff, process crash, and fast restart.
- SQLite local storage with relational schema (device_hardware_info, storage_devices, installed_applications, installed_packages, network_info, session_events, app_sessions, app_items, app_status, permission_status, employee_info)
- **PID-based session tracking** with `process_id` / `parent_process_id` on `app_sessions`
- **cgroup-based session dedup**: multi-process GUI apps (VS Code, Chrome) collapse to ONE `app_sessions` row per logical window via systemd scope grouping; `grouped_by` / `cgroup_scope` / `context_label` columns record how each session was grouped + which window/profile it is (VS Code workspace folder, Chrome profile)
- **Atomic cascade-close**: sessions and their `app_items` close in ONE transaction via `CloseSessionsAndAppItemsAsync()` — no orphaned open items when a session ends
- **Hierarchy resolver**: node/bash → terminal → IDE via `parent_item_id` + OS process tree
- **GUI-apps-only tracking**: only GUI apps with .desktop files (Linux), .app bundles (macOS), or Start Menu/Program Files entries (Windows) are tracked. CLI tools, shells, build tools, runtimes, and daemons are skipped entirely
- **Wayland-native app tracking**: known GUI apps (VSCode, Chrome) tracked even without a window title — they don't appear in X11's `_NET_CLIENT_LIST` on Wayland
- **Browser journey (Option B — accessibility)**: `browser_tab` + `browser_navigation` + `browser_download` items with the EXACT active-tab URL + domain, read from the OS accessibility tree (AT-SPI / UIA / AX) — works on every browser and every Chrome version (136+ included), no debugger, no extension, no catalog dependency. Session per window, idle-close, graceful close, incognito gated. Private/incognito URLs: Firefox private via AT-SPI `DocURL` (role 95); Chrome incognito requires Chrome to be launched with `--force-renderer-accessibility` (Chrome 136+ ignores runtime AT-SPI activation — the tracker's installed user-level launcher carries the flag). Same-tab navigation always rotates via a bounded historyLag timeout. Linux reader validated live on Chrome 151.
- **File explorer journey tracking**: 3 watchers (ATSPIEventWatcher, FileSystemEventWatcher, RecentFilesWatcher) → EventCoordinator (dedup/correlate/normalize) → JourneyEngine (resolve session, create AppItem rows with 9 journey fields)
- **IObservableEventSource interface**: common contract for all watchers — IDE, Terminal, Office integrations plug in the same pattern
- **Headless subprocess filtering (cross-platform)**: Chromium/Electron `--type=` flags detected via cmdline on Windows (PowerShell), macOS (`ps`), and Linux (`/proc/pid/cmdline`). Centralized `ChromiumSubprocessFlags` in `AppProcessClassifier`
- Models: DeviceHardwareInfo (with mac/gpu/storage), InstalledApplication (with metadata + binary_name), NetworkInfo (with public IP), SessionEvent, AppSession (with FK to installed_apps/packages), AppItem (self-referencing via parent_item_id)
- Encrypted config system (AES-256-GCM, transport→machine key migration)
- Login flow with server
- **Dedicated sync engine** (2026-08-11) — `SyncService` background loop decoupled from collection: drains unsent rows in byte-bounded chunks (1000 rows / ~1MB), gzip-compressed, ~150ms polite pauses, 5-min per-pass budget, exponential backoff; FK-ordered (sessions before items). Collection never blocks on the network.
- **Device hardware**: now collects mac_address, storage_devices, gpu_model from OS
- **Installed apps**: scans actual OS databases (registry, .desktop files, .app bundles) — GUI apps only, not running processes; binary_name mapping extracted from Exec= line
- **Installed packages**: detects CLI tools/runtimes/libraries from npm/pip/apt/brew/choco/winget/scoop/cargo/snap/flatpak — separate table from installed_applications
- **Network info**: has public IP lookup, dedup by IP change, mac_address removed (in device_hw)
- **Shell commands REMOVED** — no longer collected or synced
- **Generic app_items** replaces browser_contexts + file_explorer_contexts + urls + url_visits
- **Process filtering**: SQLite-backed ResolveAppInfo() replaces in-memory IsInstalledApp() — processes not in DB auto-detected and saved before tracking
- **AppDisplayName**: uses real app name from installed_applications (e.g., "Visual Studio Code"), not process name ("code") or window title
- **Parent-child tracking**: `SessionHierarchyResolver` walks PPID chain + open-session PID registry; links via `parent_item_id` (nullable for standalone terminals)
- **Linux filtering fixed**: removed resolvedTitle ??= name fallback that was bypassing window-title filters
- **GNOME daemon contamination fixed**: Xwayland empty `binary_name` caused fuzzy-match SQL `$name LIKE '%%'` to match every process. Fixed via: `WHERE binary_name != ''` in fuzzy SQL, `NonAppProcesses` + `NonAppProcessPrefixes` blocklist expansion (16 GNOME daemons), `KernelNamePrefixes` in first-stage filter, `NoDisplay=true` + `Type!=Application` gate in `.desktop` file parsing
- Auto-start persistence (all platforms)
- Background guard watchdog
- Tray icon (minimize to tray on close)
- Windows power management (prevents sleep)
- **Headless `--background` service mode** — runs the tracking services with no Avalonia/X11 UI (systemd); skips GUI init so the installed service can't crash on Wayland `XAUTHORITY`. **Tracking starts headlessly at boot** (2026-08-18): `ExecuteAsync` restores the persisted session and calls `StartTracking()` itself, so power-on → full tracking (app sessions + browser journeys + inventory) with no GUI. The GUI is login-only — it cannot start or stop tracking
- **Single-instance activation** — a second user launch signals the running instance (named pipe `alpha-ai-tracker-activation`) to raise its window; `--background`/`--minimized` relaunches exit quietly
- **Six-page GUI** (2026-08-10) — `MainWindow` is a router over four exclusive states; pages live in `Views/Pages/`: Splash (boot checklist), Login, PermissionSetup (stepper), and behind the nav rail Dashboard (identity + status tiles + pipeline health + attached devices), System Specs (machine/compute/network/storage/peripherals) and Installed Applications (searchable apps + packages inventory, virtualized). One VM per page, all Transient in DI. Details: [client/UI_ARCHITECTURE.md](./client/UI_ARCHITECTURE.md)
- **Runtime branding from a single source** — `Core/AppInfo.cs` resolves the product name, tagline, initials, publisher, copyright and version from the embedded `APP_IDENTIFIERS` + `VERSION`; no XAML or C# literal names anywhere in the UI. Editing either file re-brands both the app and the installers (§6 → *Branding-Single-Source Rule*)

**What's missing:**

- **No tests** (0 test files)
- ~~**No auto-update mechanism**~~ (resolved 2026-08-12 — GitHub Releases self-updater with GUI Check updates; see changelog)
- **No crash reporting** — unhandled exceptions crash silently
- **No offline queue analysis** — if server is unreachable, logs buffer locally with no back-pressure handling
- **No encryption at rest** — SQLite encryption (sqlcipher) is commented out
- **macOS CPU measurement** — macOS process collector skips CPU measurement (always 0%)
- **macOS window titles** — only captures foreground window
- **Six-page GUI not yet ship-tested** — it needs no packaging change (hero images and `APP_IDENTIFIERS` both compile into `client.dll`), but per the Installer-Parity Rule it is not "done" until verified from an installed build

### Web — ~20% complete

**What works:**

- ~50 page routes exist with polished UI
- Login page with animated hero section
- Auth check on mount (Redux + server cookie)
- Employees page — real API calls via TanStack Query (CRUD + generate secret, portal-rendered Radix action menu); each row's action menu deep-links to **View Journey** and **Device Specs** (2026-08-18)
- **Employee Journey module** (2026-08-18) — three real-API pages behind the shared `EmployeePage` shell + `EmployeeSelector` picker: Session Timeline (`useInfiniteQuery` on `GET /app-sessions`, 30/page, fg/bg stacked bar), App Usage (per-app foreground/background aggregation of the latest 500 sessions), Web Activity (`GET /app-items?itemType=browser_tab`); Screenshots and Location Trail are shell placeholders (the desktop client collects neither yet)
- **Device Specs module** (2026-08-18) — four real-API pages over the aggregate `GET /employees/:id/detail`: Hardware Overview, Installed Software (Applications/Packages tabs + search), Peripherals, Permissions. This replaced the old `/users/[id]` single-page detail view (deleted)
- Shared building blocks: `EmployeePage` shell (header + picker + loading/error/no-selection states, optional `fetchDetail`), `hooks/use-employee-detail.ts`, `lib/format.ts`, `EmployeeSelector`, `EmptyState`/`InventoryTable`/`FocusTime`/`DeviceClassIcon`
- Departments page — real API calls (CRUD)
- Logs/Comprehensive page — real API calls (now using new app_sessions API)
- Sidebar with permission-based filtering (client-side only); Device Specs and Employee Journey are collapsible sections
- Dashboard shows mock stats and chart

**What's missing (most pages):**

- **~40 of 50 pages use mock localStorage data** — not connected to real API
- **Client-side only permissions** — no server enforcement
- **No error boundaries** — uncaught React errors crash the page
- **No loading/empty/error states** on most mock-data pages
- **No real-time updates** — no polling, WebSocket, or SSE
- **No accessibility testing** — many interactive elements lack aria attributes
- **No unit tests** — 0 test files
- **GitHub release download** fetches from `clickmaster4285/Alpha-AI-Tracker`, not the org repo

---

## 6. Global Conventions

*Extracted from observed patterns, not documented anywhere:*

| Convention                    | Observed Pattern                                                                          |
| ----------------------------- | ----------------------------------------------------------------------------------------- |
| **API versioning**      | All routes under`/api/v1`                                                               |
| **Error responses**     | `{code, message, detail?}` via `dto.APIError` (server)                                |
| **Auth**                | httpOnly cookies for web, JWT in request body for employee clients                        |
| **Naming (Go)**         | PascalCase exports, camelCase JSON fields                                                 |
| **Naming (TypeScript)** | camelCase variables, PascalCase components                                                |
| **Naming (C#)**         | PascalCase for classes/methods,`_camelCase` for private fields                          |
| **Soft delete**         | `deleted_at TIMESTAMPTZ` on all tables, filtered in queries                             |
| **Migrations**          | Sequential numbered SQL files in`server/migrations/`                                    |
| **Go module**           | `github.com/alpha-ai-tracker/server`                                                    |
| **Git branch**          | Currently on`uienhanced` branch — no PR/branch convention visible              |
| **Commit style**        | Descriptive lowercase messages: "now remove the exit btn on the tray on windows", "fixit" |
| **Monorepo tooling**    | No shared tooling (no Turborepo, Nx, etc.). Each service has its own build system.        |
| **Build parity**        | `dotnet run` is NOT a release test — every change must be verified from an installed build; new assets/config/scripts must be bundled by the `publish/*` scripts (see below) |
| **Web list pagination** | List/table pages ALWAYS use server-side **infinite scroll** (`useInfiniteQuery` + IntersectionObserver sentinel) — Next/Previous buttons are forbidden (see *Web Infinite-Scroll Rule* below) |
| **Branding & version**  | Product name and version are written in exactly two files — `client/APP_IDENTIFIERS` and `client/VERSION`. No literal product name or version string anywhere else in C#, XAML, or the build scripts (see below) |

### Web Infinite-Scroll Rule (mandatory)

**Every list/table page on the web dashboard MUST paginate with server-side infinite scrolling — Next/Previous buttons are forbidden.**

- `useInfiniteQuery` with `initialPageParam: 1` and
  `getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined)`; flatten pages
  with `data?.pages.flatMap(p => p.data)`.
- Trigger the next fetch with an **IntersectionObserver sentinel** (`rootMargin: '300px'`, gated on
  `hasNextPage && !isFetchingNextPage`) rendered below the list.
- Inline "Loading more…" while `isFetchingNextPage`; a "Showing all N" footer once `hasNextPage` is false.
- Filter/search inputs change the query key (the infinite query restarts at page 1) — there is NO `page`
  state anywhere.
- Reference implementations: `web/src/app/(app)/employees/page.tsx`, the Session Timeline journey page.

### Installer-Parity Rule (mandatory)

**Why:** `dotnet run` always reflects the source tree, so it can never catch packaging gaps. The installed app runs from a root-owned dir (`/usr/share/alpha-ai-tracker/` on Linux, Program Files on Windows) with only what the `publish/*` scripts bundled. Recent real incidents: installed app crashed at startup (log written to a root-owned dir), browser-journey tracking dead (extensions/ not bundled), stale binary shipped (publish not rebuilt).

**Every feature/modification must be verified end-to-end in an installed build:**

1. **Always ship-test** — `dotnet run` success is NOT sufficient. Build the installer, install the artifact, and run the new functionality from there:
   ```bash
   cd client
   bash publish/build-installer.sh -b linux   # or win / mac
   sudo dpkg -i installers/alpha-ai-tracker_0.2.0_amd64.deb   # filename tracks client/VERSION
   ```
2. **New runtime assets** (icons, JSON, images, fonts) — bundled ONLY if copied by `build-installer.sh` (`bundle_into_publish`) or the platform builders (`build-deb.sh`, `build-dmg.sh`, `installer-windows.iss`). Add the copy step when you add the asset.
3. **New runtime assets** (embedded scripts, JSON, icons) — the accessibility probe is embedded as a C# string in `LinuxAtSpiBrowserReader` (no external file to bundle). Anything file-based must be added to `bundle_into_publish()` or the platform builders.
4. **New scripts under `publish/`** — copied into every publish output automatically. Runtime-referenced scripts must live in `publish/` (or be added to the copy list).
5. **New env vars / config** — must be added to `.env` BEFORE `encrypt-config.sh` runs; installers ship `config.enc` baked at build time. Dev reads `.env` directly — config that works in `dotnet run` is silently missing in the installer. Config changes now auto-propagate: `EnvLoader` replaces a stale user-config copy (`~/.config/alpha-ai-tracker/config.enc`) with the freshly shipped one on next launch when the decrypted contents differ.
6. **Path assumptions** — the installed app's working dir is root-owned / not user-writable. NEVER write files relative to cwd or the exe dir. Use `~/.config/alpha-ai-tracker/` (logs, machine-id) and `~/.local/share/alpha-ai-tracker/` (DB, sockets). `dotnet run` cannot catch this because the dev working dir is writable.
7. **Packaging edits apply to ALL platforms** — when changing build scripts, update `build-installer.sh`, `build-deb.sh`, `build-dmg.sh`, and `installer-windows.iss` consistently.
8. **Stale-binary guard** — `build-installer.sh` aborts if any source file is newer than the published `client.dll`; fix with `dotnet clean && bash publish/build-installer.sh`. This guard covers compiled code only — items 2–6 are the developer's responsibility.

### No-Hardcoded-Names Rule (mandatory)

**Software detection must NEVER use hardcoded product/software names.** Employees run different OSes (Windows 7/10/11, Ubuntu LTS/releases, macOS), different departments (development / SEO / marketing / IT support / IT technician) install different tools, and packages differ per machine — a name list only works on the machine that generated it and silently breaks (missing rows, misclassified software) everywhere else. Classification must come from **genuine OS metadata**:

1. **PE Subsystem** (`client/Core/ExecutableMetadata.cs`) — the OS's own statement of GUI vs console for any Windows `.exe`: `IMAGE_SUBSYSTEM_WINDOWS_GUI` (2) = application, `IMAGE_SUBSYSTEM_WINDOWS_CUI` (3) = CLI tool/runtime. Used by the Start Menu scan, both registry scans, the classifier, and the runtime session gate. Works on every Windows version, every language, every department.
2. **Filesystem structure** — C:\Windows\* (System32/SystemApps/WinSxS…) = OS component; Start Menu `.lnk` presence = user-facing; `.desktop` presence (Linux) / `.app` bundle (macOS) = GUI application. `ExecutableMetadata.IsWindowsSystemTree` + `IsUninstallerFileName` are the structural helpers.
3. **Registry flags** — `SystemComponent`, `ParentKeyName`, `ReleaseType` mark installer churn/updates; `URLAssociations` http/https marks browsers; `DisplayIcon`/`InstallLocation` resolve the exe that decides app-vs-package.
4. **Driver Store** — drivers are inventoried from `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverDatabase\DriverPackages`, never from name patterns.
5. **Package managers are the source of truth** — winget/npm/pip/choco/scoop/apt/snap/flatpak/brew report what they installed; dedup is by identity/fingerprint, not by filtering names.

**Allowed exceptions (OS-shell constructs, NOT user software):** Linux GNOME/session daemon prefixes in `NonAppProcesses`/`NonAppProcessPrefixes` (gnome-*, gsd-*, gvfsd-*, ibus-*, evolution-*), Windows shell display names (`DisplayNameOverrides`: explorer→File Explorer, svchost→Windows Services…), and the Windows Update `KB`-prefix naming convention. These are OS-provided labels for OS processes; user-installed software detection must stay 100% metadata-driven. When a fix is tempting as a name list, it must be implemented as metadata first (probe the OS), and the resulting rule documented here.

### Branding-Single-Source Rule (mandatory)

**Every visible product name and version comes from `client/APP_IDENTIFIERS` + `client/VERSION`.** Neither value is duplicated anywhere else. Edit either file, rebuild, and the rail wordmark, window title, splash, footer, tray tooltip, log banners, installer filenames and package metadata all follow — with no other source edit. This is a structural guarantee, not a convention to remember:

| File                    | Build-time consumers                                                                                    | Runtime consumer                                                          |
| ----------------------- | ------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------- |
| `APP_IDENTIFIERS` | `build-deb.sh`, `build-dmg.sh`, `generate-windows-vars.sh` → `installer-windows.iss`, `release.sh` (sourced as shell) | `client.csproj` embeds it as `client.APP_IDENTIFIERS`; `Core/AppInfo.cs` parses it |
| `VERSION`         | same scripts — package/artifact filenames and installer version fields                                   | `client.csproj` → `Version` / `InformationalVersion`; `AppInfo.Version` reads it back |

**Runtime path.** `Core/AppInfo.cs` reads the embedded resource with `Assembly.GetManifestResourceStream()` and parses it with a **strict regex — the file is read as data, never executed** — exposing `DisplayName`, `Publisher`, `AppUrl`, `PackageName`, `BundleId`, `ExecutableName`, `AppMutex`, `WmClass`, `Tagline`, `Initials`, `Copyright`, `Version`, `VersionDisplay`, `TitleWithVersion`. Every accessor has a fallback default and the loader swallows exceptions — **branding must never take the app down**. `MainViewModel` re-exposes these as `AppDisplayName` / `AppTagline` / `AppInitials` / `AppVersionDisplay` / `AppCopyright` / `AppTitleWithVersion` so XAML binds to them; `App.axaml.cs` uses `AppInfo.DisplayName` directly for the tray tooltip and menu.

**Three consequences worth knowing before you "fix" something:**

1. **Re-branding needs NO installer-script change.** The embedded resource lives inside `client.dll`, and hero images are swept in by the existing `<AvaloniaResource Include="Assets\**" />` glob — both ride the publish output automatically. The Installer-Parity Rule still applies to *new file-based* runtime assets; it does not apply to branding strings or to anything under `Assets/`.
2. **`client.csproj` reads `VERSION` at project-evaluation time, not in a `BeforeBuild` target.** This is deliberate — a target-based read leaves IDE design-time builds silently falling back to `1.0.0`. Trade-off: after editing `VERSION`, run `dotnet clean` (or restart the IDE) so the cached project graph is flushed.
3. ⚠️ **`Core/EncryptedConfigService.cs` `TransportKeySeed` / `MachineKeyPrefix` are NOT branding.** They read like product names (`"AlphaAITracker:TransportKey:v1"`) but are cryptographic key-derivation seeds. Templatizing them from `APP_IDENTIFIERS` would make **every `config.enc` already deployed in the field undecryptable**. Leave them byte-for-byte alone during any re-brand.

**Acceptance proof (re-brand smoke test):** change `DISPLAY_NAME` in `APP_IDENTIFIERS`, bump `VERSION`, `dotnet clean && bash publish/build-installer.sh -b linux`, install the artifact, and confirm the rail, window title, splash, footer, tray tooltip and installer filename all changed with no other edit. Details: [client/APP_IDENTIFIERS_README.md](./client/APP_IDENTIFIERS_README.md), [client/VERSION_README.md](./client/VERSION_README.md).

---

## 7. Known Gaps / Risks

### Cross-Cutting

| Gap                                 | Severity  | Details                                                                                                                                                                          |
| ----------------------------------- | --------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **No tests anywhere**         | 🔴 High   | 0 test files across all 3 projects. Any refactor is blind.                                                                                                                       |
| **No observability**          | 🟠 Medium | No structured logging, metrics, tracing. Debugging production issues requires SSH + log scraping.                                                                                |
| **Client-side only RBAC**     | 🟠 Medium | Permissions are enforced only in the web frontend localStorage. A malicious user can trivially bypass them.                                                                      |
| **No rate limiting**          | 🟠 Medium | Login endpoint and all sync endpoints have no rate limiting. Brute-force / DoS is trivial.                                                                                       |
| **No server permission model**| 🟠 Medium | Any authenticated user can access all protected endpoints; no role checks server-side.                                                                                           |
| **Cross-account injection**   | 🟠 Medium | Sync handlers validate the JWT but don't verify the body `employeeId` matches the JWT subject.                                                                                   |
| **Mock data dominance**       | 🟠 Medium | ~80% of web pages use mock data (~40 of ~50; employees list, departments, logs, Employee Journey + Device Specs are real-API), giving a false sense of completeness.                                                                          |
| **Default passwords**         | 🟠 Medium | `AlphaAI@2024!` is the compiled-in default. Easy to forget to change.                                                                                                          |
| **No offline/retry strategy** | 🟢 Low    | Client retries sync every cycle but has no exponential backoff or dedup.                                                                                                         |
| **No data-pruning job**       | 🟢 Low    | Server's only background job (`staleness_sweep.go`) deactivates stale catalog links but never deletes old app_sessions/app_items data.                                            |
| ~~Dead shell-command code~~ (resolved) | — | All shell-command and legacy browser-context DTOs/models deleted 2026-08-05 (client + server). |
| **Horizontal scaling**        | 🟢 Low    | Server uses Redis for employee secrets (short TTL), so scaling is straightforward — but DB queries have no query analysis.                                                      |

---

## 8. How to Run Locally

### Prerequisites

- Go 1.22+ (tested 1.25), PostgreSQL 16+, Redis 7+
- Node.js 20+, npm
- .NET 10 SDK (for client development with `dotnet run`)
- Docker (optional, for PostgreSQL/Redis)

### 1. Start PostgreSQL and Redis

```bash
# Using Docker (recommended)
docker run -d --name pg -e POSTGRES_USER=alpha_ai -e POSTGRES_PASSWORD=yourpassword -e POSTGRES_DB=alpha_ai_tracker -p 5432:5432 postgres:16
docker run -d --name redis -p 6379:6379 redis:7
```

### 2. Server

```bash
cd server
cp .env.example .env
# Edit .env — set DB_PASSWORD and JWT_SECRET
make setup
make run
# Server starts on http://localhost:8080
```

### 3. Web Dashboard

```bash
cd web
npm install
npm run dev
# Dev server on http://localhost:3000
```

If server runs on a different host, set `NEXT_PUBLIC_API_URL` in `web/.env`.

### 4. Desktop Client

```bash
cd client
# Ensure .env has ALPHA_SERVER_URL=http://localhost:8080
dotnet run
```

### Notes

- No `docker-compose.yml` exists — you must start PostgreSQL and Redis manually.
- The client requires a running server with at least one employee created via the web admin.
- Login to the web dashboard with the default credentials from `.env` (admin@alphai.com / AlphaAI@2024!).
