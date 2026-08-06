# Client Architecture — Alpha AI Tracker Desktop App

> **Last audited:** 2026-08-06 (Option B + profile-History URL fallback)  
> **Changelog:** 
> - 2026-08-06: **Hybrid URL fallback — browser profile History reader (no restart, all browsers).**
>   Chrome 136+ on Linux never exposes the omnibox URL via AT-SPI unless launched with
>   `--force-renderer-accessibility` (restart = rejected by the product). NEW
>   `Core/BrowserAccessibility/BrowserHistoryReader.cs` reads the browser's own profile database —
>   Chromium-family `History` (Chrome/Edge/Brave/Opera/Vivaldi) and Firefox `places.sqlite` — while
>   the browser RUNS: DB + `-wal`/`-shm`/`-journal` sidecars are copied to a temp dir and opened
>   read-only (a torn snapshot is swallowed and retried next poll), change signatures throttle
>   re-reads, and resolution is STRICTLY title-match (no newest-visit fallback → no cross-window
>   misattribution). Generic `~/.config`/`~/.mozilla` scans (plus Windows/macOS roots) discover
>   brand-new browser profiles (install→use→uninstall-in-5-min journeys captured while alive).
>   Wired into `AccessibilityBrowserTracker.EnrichUrlsFromHistoryAsync` (fills empty URLs before tab
>   logic; URL-less title changes don't rotate tabs until history catches up); URLs tagged
>   `metadata_json.source="history"` vs `"a11y"` via new `AccessibilitySnapshot.UrlSource`; shared
>   `StripBrowserSuffix` helper added. Config: `ALPHA_BROWSER_HISTORY_ENABLED=true` (default),
>   `ALPHA_BROWSER_HISTORY_POLL_SECONDS=10`; `.env.example` + `--print-config` updated. Verified live
>   on Linux: running Chrome 151, snap Firefox (AppArmor blocks D-Bus, not file reads), a fresh
>   brand-new profile, and an end-to-end tracker run (title + full URL + domain + `source=history`
>   in `app_items`).
> - 2026-08-05: **Option B — accessibility-tree browser journey (debugger pipeline removed).**
>   Chrome 136+ cannot be debugged on its real profile (verified: same-path `--user-data-dir` and
>   `RemoteDebuggingAllowed` policy both fail) and extensions were ruled out, so the entire
>   debugger pipeline (`Core/Browser/*` — 16 files: adapters, CDP/RDP sessions, runtime
>   manager/store, journey engine, watchdog) was DELETED and replaced with a new
>   `Core/BrowserAccessibility/` namespace that reads the OS accessibility tree — the same tree
>   screen readers use — to capture the REAL browser journey on every platform and every Chrome
>   version, with no debugger, no extension, and no browser-catalog dependency (an installed→
>   used→uninstalled-in-5-min browser is captured). Linux: python3+AT-SPI probe (empirically
>   validated on Chrome 151 — the omnibox ENTRY node "Address and search bar" exposes the exact
>   URL/query). Windows: UIA via `Interop.UIAutomationClient` (netstandard2.0, API verified by
>   reflection). macOS: osascript/System Events (best-effort; Accessibility grant required).
>   `AccessibilityBrowserTracker` polls every `ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS` (3),
>   writes one `app_sessions` per window + `browser_tab`/`browser_navigation` `app_items`,
>   idle-closes after `ALPHA_BROWSER_JOURNEY_IDLE_MINUTES` (15), closes vanished windows after 3
>   polls, closes gracefully on shutdown, watches Downloads for `browser_download` items, and
>   gates incognito URLs behind `ALPHA_BROWSER_CAPTURE_INCOGNITO=false`. Dead code removed:
>   shell-command collectors (interface + 3 impls + model), `publish/install-extensions.sh`,
>   and the `extensions/` bundling step. Also fixed the `SqliteLogStore` fuzzy-match AND/OR
>   precedence bug (junk `chrome` row beat `Google Chrome`; now prefers `is_browser` rows).
>   Env knobs are now a11y-only (installer parity: `.env` + `.env.example` updated).
> - 2026-08-03: **Single-instance + tray UX fix; Installer-Parity rule** — a second launch now restores the running window (`SingleInstance.cs` named-event signaling), the tray menu gains **Quit**, and on tray-less desktops (no StatusNotifierWatcher — `Services/TrayAvailability.cs` D-Bus check) closing the window exits instead of stranding an invisible process. Added the mandatory **Build-Parity Rule** (§8): every feature/modification must also be wired into the installer packaging and verified from an installed build — `dotnet run` is not a valid release test.
> - 2026-08-01: **Docs audit** — removed stale `activity_logs` / `shell_commands` schema + sync references (both gone from the product), corrected sync endpoint table (7 endpoints, batch 500), documented the `MigrateSql` migration strategy, added `storage_devices`/`permission_status`/`app_items` tables, noted `IShellCommandCollector`/`ShellCommand` as dead code, added `FileLoggerProvider` (dotnetrunlog.txt), added `install-extensions.sh`, and cleaned up the completion % to ~85%.
> - 2026-07-31: **Cross-service payload sync + local catalog dedup** — installed-apps mapper now sends `binaryName`/`isBrowser`/`desktopId`/`categories`; app-sessions sends `groupedBy`/`cgroupScope`/`contextLabel`; app-items sends `processId` + all 9 journey fields. `installed_applications` conflict-update resets `is_synced = 0` so re-detected apps re-sync (drives server `last_seen_at` freshness). `installed_packages` switches to `ON CONFLICT(package_name, source_manager)` with a dedup block (window fn keeps newest per fingerprint) + `CREATE UNIQUE INDEX IF NOT EXISTS idx_installed_packages_fingerprint` in `MigrateSql` — fixes the 6,530-row duplicate package bloat caused by `ON CONFLICT(id)` never conflicting.
> - 2026-07-31: **cgroup-based session dedup (multi-process GUI apps → one session)** — `CgroupResolver.cs` (new) reads `/proc/<pid>/cgroup` and extracts the systemd transient scope (`app-gnome-code-*.scope`); every subprocess of one logical window shares it, so `BuildSessionKey` became scope-aware: `scope|{scope}|{installedAppId}|{machine}|{session}` for scoped processes, unchanged PID key as fallback. Scope resolved once per process and threaded through the `resolvedLogs` tuple — all 6 `BuildSessionKey` call sites consistent. Boot hydration recomputes scope live (`OpenSessionRecord.InstalledAppId` added to both open-session queries). `SessionLabelResolver.cs` (new) labels sessions (VS Code workspace folder via argv + PPID fallback, Chrome `--profile-directory`) into `context_label`. New `grouped_by`/`cgroup_scope`/`context_label` columns on `app_sessions` via `MigrateSql` ALTERs (client-only; server sync later). `gnome-control-center-search-provider` → `NonAppProcesses`.
> - 2026-07-31: **Atomic cascade-close** — new composite `CloseSessionsAndAppItemsAsync()` acquires the connection gate once, closes sessions + their open `app_items` in ONE transaction. Wired into all 4 close paths (main loop — which was missing the item-close entirely —, crash recovery, garbage cleanup, non-GUI cleanup). Fixes 11 orphaned open items on closed sessions. Per-tab closes in `NativeMessageService`/`JourneyEngine` still use `CloseAppItemsBySessionIdsAsync`.
> - 2026-07-31: **Terminal classification + PPID walk logging** — `TerminalEmulators` expanded (`gnome-terminal-server`, `foot`); `SessionHierarchyResolver` takes optional `ILogger` and logs `ResolveParent` walks at Debug.
> - 2026-07-30: **Software classification pipeline** — Added `SoftwareCategoryResolver.cs` (metadata-driven category resolution: `.desktop Categories` → Browser/IDE/FileManager/Application, macOS bundle ID fallback), `SoftwareClassifier.cs` (joint dedup pipeline: GUI apps win over matching package entries), `SoftwareIdentityResolver.cs` (SHA-256 stable identity for cross-source dedup across InstalledAppDetector vs PackageDetector). Refactored `AppProcessClassifier.cs`: renamed `FileManagerProcesses`/`IdeProcesses` → `FileManagerFallbacks`/`IdeFallbacks`, added `ResolveCategory()` and new `ResolveRootItemType()` overload with `categories`/`desktopId` params. Upgraded `InstalledAppDetector.cs`: Linux `.desktop` scanning now follows `$XDG_DATA_DIRS` (covers snap `/var/lib/snapd/desktop/applications/` and flatpak exports), macOS `IsMacOSBrowserApp()` → `InspectMacOSBundle()` returning `CFBundleIdentifier` + browser flag, added `ExtractPlistString()` helper, browser detection via `Categories=WebBrowser` (Linux) / `URLAssociations` http/https (Windows) / `CFBundleURLSchemes` http+https (macOS). Added server migration 012 (`desktop_id`, `categories`, `is_browser` columns).
> - 2026-07-30: **File logger** — Added `FileLoggerProvider.cs` for `dotnetrunlog.txt` output, registered in `Program.cs`.
> - 2026-07-30: **GUI-apps-only tracking gate** — New rule: only processes resolving to `installed_applications` or detected as GUI apps (has .desktop file / .app bundle / Start Menu) are tracked. Removed shell-always-tracked, build-tool auto-registration, runtime auto-registration, package fallback tracking from `ResolveAppInfoInner`. Added `IsGuiApplication()` to `IInstalledAppDetector`/`InstalledAppDetector` with `CheckGuiPath()`. Simplified main loop filter, removed `_knownPackageNames`, `CloseStalePackageSessionsAsync`. Renamed `AutoDetectInstalledApp` → `AutoDetectInstalledGuiApp`.
> - 2026-07-30: **Fixed NativeMessageService app_display_name GUID bug** — `_browserAppCache` now stores `(id, displayName)` tuple instead of raw GUID. `ResolveBrowserAppIdAsync` → `ResolveBrowserAppAsync` returning both ID and name. Caller uses display name for `AppDisplayName`.
> - 2026-07-30: **Cross-platform headless subprocess filter** — Added `GetProcessCommandLine()` (PowerShell on Windows, `ps -o command=` on macOS). Centralized `ChromiumSubprocessFlags` + `IsHeadlessSubprocess()` in `AppProcessClassifier`. Linux `IsChromeSubprocess()` → `ReadProcessCmdline()`. Startup cleanup closes old `--type=` rows.
> - 2026-07-30: **Dynamic browser suffix stripping** — Removed 12-entry hardcoded `BrowserSuffixes` array from `ActivityContextParser`. Strips suffix dynamically from `installed_applications.app_name`. No generic regex fallback.
> - 2026-07-30: **SemaphoreSlim concurrency gate** — `SemaphoreSlim(1,1)` guarding all `SqliteLogStore` public methods. Private ungated helpers avoid reentrancy deadlock. `PRAGMA busy_timeout = 5000;`. `GatedTransaction` wrapper + gate-leak fix.
> - 2026-07-30: **FileSystemEventWatcher exclusion list** — Excluded Waydroid, Flatpak, Snap, cache, trash, containers, Steam dirs. Removed `UserProfile` from `WatchDirectories`. Per-process resilience via inner try/catch.
> - 2026-07-29: **Fixed GNOME daemon contamination via Xwayland empty `binary_name`** — Xwayland `.desktop` file has no `Exec=` line, so `InstalledAppDetector` stored it with `binary_name=""`. The fuzzy-match SQL (`$name LIKE '%' || binary_name || '%'`) became `$name LIKE '%%'` — matching **every** process. Fixed by: (1) `AND binary_name != ''` in fuzzy SQL; (2) `NonAppProcesses` expanded with 16 GNOME daemons + added `NonAppProcessPrefixes` array (`gvfsd-`, `gsd-`, `goa-`, `evolution-`, `ibus-`, `at-spi2-`, `gnome-shell-`, `tracker-`, `gdm`, `mutter-`); (3) `KernelNamePrefixes` in `ProcessFilter.cs` for first-stage filter; (4) `NoDisplay=true` + `Type!=Application` gate in `AddAppFromDesktopFile`. DB cleaned: orphaned sessions closed, Xwayland entry patched with `binary_name='Xwayland'`.
> - 2026-07-29: **Added File Explorer journey tracking** — Full event-driven desktop event bus for file manager operations (Nautilus, Dolphin, Thunar, Nemo, etc.). Three watchers: `ATSPIEventWatcher` (Tmds.DBus.Protocol → AT-SPI focus/window events + `/proc/cwd`), `FileSystemEventWatcher` (FileSystemWatcher on 7 user directories), `RecentFilesWatcher` (XBEL monitor at `~/.local/share/recently-used.xbel`). `EventCoordinator` deduplicates (3s), correlates (500ms), normalizes raw→business events. `JourneyEngine` resolves `AppSession`, creates `AppItem` rows with 9 journey fields (`object_type`, `action`, `journey_id`, `sequence`, `previous_path`, `current_path`, `window_id`, `tab_id`, `metadata_json`). `IObservableEventSource` interface for all watchers. Coexistence: `item_type` preserved; browser pipeline untouched. NuGet: `Tmds.DBus.Protocol` v0.94.2.
> - 2026-07-28: **Added browser extension journey tracking** — Chrome MV3 extension + NativeMessageService + native-host.py pipeline captures real-time browser navigation (URLs, tabs, titles). Stored as `browser_tab`/`browser_navigation` in `app_items` with `url`/`domain` fields.
> - 2026-07-28: **Added `NativeMessageService`** — BackgroundService listening on Unix socket for browser events. `_tabSessionCache` maps browser:tabId→AppSession. Handles tab create/update/activate/close events.
> - 2026-07-28: **Added `BrowserExtensionService`** — Browser detection + two-strategy extension install (--load-extension → profile injection). Async-safe. Extension detection via NativeMessageService socket-level heartbeat.
> - 2026-07-28: **Added `url`/`domain` columns to `app_items`** schema + server DTOs.
> - 2026-07-28: **Fixed `InstallNativeHostManuallyAsync`** — computes extension ID into `allowed_origins`.
> - 2026-07-28: **Fixed extension active detection** — replaced socket-based `fuser` with process-based `pgrep native-host.py + pgrep chrome`, then replaced again with socket-level heartbeat for precision.
> - 2026-07-28: **Removed `--enable-automation`** from Chrome launch to silence GCM noise.
> - 2026-07-28: **Added crash-safe session ended_at tracking** — heartbeat persisted every cycle (`last_heartbeat_at` in `app_status`), `ReconcileStaleSessionsOnBootAsync()` called on startup detects stale heartbeats and closes orphaned sessions with the last heartbeat time as approximate crash time. Includes cross-platform `GetSystemUptime()` for diagnostic logging. Handles poweroff, process crash, and fast restart.
> - 2026-07-27: Added `ActivityContextParser`, `AppProcessClassifier`, `SessionHierarchyResolver` for browser URL / file path / process-tree hierarchy.
> - 2026-07-27: Sessions keyed by PID; `process_id` persisted on `app_sessions` and `app_items`.
> - 2026-07-27: Added `binary_name` column to `installed_applications` for process→display-name resolution.
> - 2026-07-27: Added `installed_app_id` / `installed_package_id` FK columns to `app_sessions`.
> - 2026-07-27: Replaced in-memory `IsInstalledApp()` filter with SQLite-backed `ResolveAppInfo()` + auto-detect.
> - 2026-07-27: Fixed Linux ProcessCollector `resolvedTitle ??= name` bug (was giving every process a fake title, bypassing window-title filters).
> - 2026-07-27: Added process-tree-based parent-child tracking for terminal shells inside IDE/terminal-emulator sessions.
> - 2026-07-27: Added `waydroid` / `gnome-software` to `NonAppProcesses` blocklist.
> - 2026-07-27: **Added `BuildToolProcesses` set** (`make`, `go`, `npm`, `npx`, `cargo`, `pip`, `tsc`, etc.) — build tools were auto-registered as `installed_packages` (category=`tool`) and tracked without a window title. *(Reversed 2026-07-30 by the GUI-apps-only gate — build tools are now skipped entirely.)*
> - 2026-07-27: **Fixed process filter** — known-app processes (`appId != null`) and build tools are now tracked even without a window title. Previously, Wayland-native apps (VSCode, Chrome, Nautilus) were silently dropped because they don't appear in X11 window list.
> - 2026-07-27: **Broadened `AutoDetectInstalledApp`** — now accepts `/home/*` and `/media/*` paths as valid install locations (covers project-local compiled binaries like `./bin/alpha-ai-server`).
> - 2026-07-27: **Fixed file manager path resolution** — `ParseFileManagerContext` now resolves folder display names to absolute paths by searching `~/`, `~/Documents`, `~/Desktop`, `/media/<user>/`, etc.
> - 2026-07-27: **Fixed `SessionHierarchyResolver`** — `ResolveParent` now walks through build tools and runtime packages as intermediate PPID steps; `ShouldLinkTo` now accepts build tools as children of IDEs and terminals.
> **Service completion (honest):** ~85%

---

## 1. Responsibility & Scope

**Owns:**
- Collecting process activity data (window titles, CPU%, memory) from employee machines
- Identifying installed (non-kernel, non-system) applications via platform heuristics
- Storing collected data locally in SQLite before syncing
- Syncing data to the central server via REST APIs
- Managing employee login/logout lifecycle (JWT-based)
- Ensuring the app stays running (auto-start + background guard)
- Providing a UI for login flow and permission setup

**Does NOT own:**
- Any business logic about what constitutes "productive" vs "unproductive" activity
- User management, department hierarchy, or admin functionality
- Data persistence beyond local buffering (server is source of truth)
- Any web-facing display or analytics
- Shell/terminal command history collection (**removed** — no longer collected or synced)

---

## 2. Tech Stack Detail

| Component | Technology | Version |
|---|---|---|
| **Language** | C# (.NET) | net10.0 |
| **UI Framework** | Avalonia | 12.1.0 |
| **Desktop** | Avalonia.Desktop | 12.1.0 |
| **Theme** | Avalonia.Themes.Fluent | 12.1.0 |
| **Fonts** | Avalonia.Fonts.Inter | 12.1.0 |
| **MVVM Toolkit** | CommunityToolkit.Mvvm | 8.4.2 |
| **SQLite Driver** | Microsoft.Data.Sqlite | 10.0.10 |
| **SQLite Bundle** | SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 |
| **DI/Hosting** | Microsoft.Extensions.Hosting | 10.0.10 |
| **HTTP Client** | System.Net.Http (built-in) | — |
| **D-Bus** | Tmds.DBus.Protocol | 0.94.2 |
| **UIA (Windows reader)** | Interop.UIAutomationClient | 10.19041.0 |
| **Diagnostics** | AvaloniaUI.DiagnosticsSupport | 2.2.3 (Debug only) |

### Notable Omissions

- **No ORM** — raw SQL via `SqliteCommand` and parameterized queries
- **No test framework** — no xUnit, NUnit, or any test project
- **No structured logging framework beyond ILogger** — console (dev) + `FileLoggerProvider` → `dotnetrunlog.txt`
- **No auto-updater library** — no Squirrel, Velopack, or similar

---

## 3. Project Structure

```
client/
├── Program.cs                          # Entry point. DI setup, CLI modes (--encrypt-config, --background), mutex
├── App.axaml / App.axaml.cs            # Avalonia app lifecycle, tray icon, window close interception (hide vs shutdown)
├── SingleInstance.cs                   # Named-event signaling — a 2nd launch restores the running instance's window
├── app.manifest                        # Windows compatibility manifest
├── ViewLocator.cs                      # ViewModel → View resolution via reflection
├── client.csproj                       # Project file with NuGet references
├── appsettings.json                    # Logging config (not heavily used)
├── .env.example                        # Template for environment config
│
├── Configuration/
│   ├── AppConfig.cs                    # Reads env vars (ALPHA_CLIENT_ID, ALPHA_SERVER_URL, ALPHA_DB_PATH, etc.)
│   └── EnvLoader.cs                    # Multi-source config loading: config.enc (encrypted) → .env (plaintext dev fallback)
│
├── Core/
│   ├── Abstractions/
│   │   ├── IActivityCollector.cs       # Single method: CollectAsync → ActivityLog[]
│   │   ├── ILogStore.cs                # 40+ methods: store/retrieve-unsent/mark-sent for 8 tables + lookup + journey + close paths
│   │   ├── IInstalledAppDetector.cs    # GUI/desktop app detection + permission status
│   │   └── IPackageDetector.cs         # CLI tool/runtime/library detection from package managers
│   ├── BrowserAccessibility/
│   │   ├── IAccessibilityBrowserReader.cs   # Browser-window snapshot contract (platform readers)
│   │   ├── AccessibilitySnapshot.cs         # window key, pid, process, title, normalized URL, UrlSource, incognito flag
│   │   ├── LinuxAtSpiBrowserReader.cs       # python3 + AT-SPI D-Bus probe (embedded script) — validated on Chrome 151
│   │   ├── WindowsUiaBrowserReader.cs       # UIA via Interop.UIAutomationClient (address bar ValuePattern)
│   │   ├── MacOsAccessibilityBrowserReader.cs # osascript/System Events (Accessibility grant required)
│   │   ├── AccessibilityBrowserReaderFactory.cs # platform picker
│   │   ├── BrowserHistoryReader.cs          # NEW: reads browser profile History/places.sqlite (safe copy+WAL) while running — title-match URL fallback, brand-new-profile discovery
│   │   ├── AccessibilityBrowserTracker.cs   # BackgroundService: poll → history enrichment → sessions/items → idle-close → downloads
│   │   └── BrowserAccessibilityHelpers.cs   # URL normalization, domain extraction, StripBrowserSuffix, incognito hints
│   ├── DesktopEventBus/
│   │   ├── IObservableEventSource.cs   # Common interface for all watchers (atspi, filesystem, recentfiles)
│   │   ├── RawDesktopEvent.cs          # Raw OS-level event from watchers
│   │   ├── DesktopEvent.cs             # Normalized business-level event (after coordinator processing)
│   │   ├── DesktopEventValidator.cs    # Static validation: file manager detection, path validity, process filtering
│   │   ├── EventCoordinator.cs         # Subscribes watchers → dedup (3s) + correlate (500ms) + normalize raw→business events
│   │   ├── JourneyEngine.cs            # Receives DesktopEvent → resolve AppSession → create AppItem rows with journey fields
│   │   └── JourneyRecord.cs            # In-memory journey state (per window/tab session)
│   ├── Models/
│   │   ├── ActivityLog.cs              # Intermediate collection DTO (used by IActivityCollector, not persisted)
│   │   ├── AppSession.cs               # AppSession + AppItem (self-referencing, url/domain + 9 journey fields)
│   │   ├── DeviceHardwareInfo.cs       # DeviceHardwareInfo, InstalledApplication, InstalledPackage, NetworkInfo, SessionEvent, StorageDevice
│   │   ├── EmployeeInfo.cs             # Employee login info (persisted in SQLite)
│   │   └── SessionInfo.cs              # Static session ID (generated once per app launch)
│   ├── CollectionExtensions.cs         # Small LINQ-style helpers used across collection code
│   ├── EncryptedConfigService.cs       # AES-256-GCM encryption with transport key + machine-derived key
│   ├── CgroupResolver.cs               # Linux /proc/<pid>/cgroup → systemd app-*.scope (session dedup key; null elsewhere)
│   ├── SessionLabelResolver.cs         # Session context_label: VS Code workspace folder / Chrome --profile-directory
│   ├── InstalledAppDetector.cs         # Cross-platform installed app detection (desktop files, registry, .app bundles) — GUI only
│   ├── PackageDetector.cs              # Cross-platform installed package detection (npm, pip, apt, brew, choco, winget, scoop, etc.)
│   ├── SoftwareCategoryResolver.cs     # Metadata-driven category resolution (.desktop Categories / macOS bundle ID)
│   ├── SoftwareClassifier.cs           # Joint dedup pipeline — GUI apps win over matching package entries
│   ├── SoftwareIdentityResolver.cs     # SHA-256 stable identity for cross-source dedup
│   ├── ProcessFilter.cs                # Filters kernel/system processes from collection
│   ├── ParentProcessResolver.cs        # Resolves window titles from parent processes (e.g., terminal → shell)
│   ├── AppProcessClassifier.cs         # Browser/file-manager/IDE/shell/runtime classification + item_type + headless-subprocess filter
│   ├── ActivityContextParser.cs        # URL + file path extraction from window titles
│   └── SessionHierarchyResolver.cs     # PID-tree parent linking (node→terminal→IDE)
│
├── Platform/
│   ├── Windows/
│   │   └── ProcessCollector.cs          # User32.dll-based: EnumWindows, GetForegroundWindow, CPU via TotalProcessorTime
│   ├── MacOS/
│   │   └── ProcessCollector.cs          # osascript-based foreground window detection. ⚠️ No CPU measurement, no all-window-enum
│   └── Linux/
│       └── ProcessCollector.cs          # Multi-strategy: xprop, xdotool, gdbus (atspi/portal/shell), Python AT-SPI script
│
├── Services/
│   ├── LogCollectorService.cs           # BackgroundService: collect → resolve → store app_sessions → sync → heartbeat cycle (30s loop)
│   ├── BackgroundGuardService.cs        # Watchdog: re-installs auto-start/systemd if removed (60s check)
│   ├── AutoStartService.cs              # Platform-specific auto-start: Run key, .desktop, launchd plist
│   ├── DesktopEventService.cs           # BackgroundService: orchestrator — starts watchers, wires EventCoordinator → JourneyEngine
│   ├── FileLoggerProvider.cs            # ILogger provider writing to dotnetrunlog.txt
│   ├── TrayAvailability.cs              # D-Bus StatusNotifierWatcher detection — does this desktop show a tray icon?
│   └── Watchers/
│       ├── ATSPIEventWatcher.cs         # Linux AT-SPI via Tmds.DBus.Protocol — focus/window events + /proc/cwd for file manager paths
│       ├── FileSystemEventWatcher.cs    # FileSystemWatcher on 7 user directories — create/delete/rename/modify enrichment
│       └── RecentFilesWatcher.cs        # XBEL monitor at ~/.local/share/recently-used.xbel — recent-file-opens evidence
│
├── Storage/
│   ├── DatabaseSchema.cs                # Raw SQL: CREATE TABLE for 11 tables + MigrateSql idempotent ALTERs + insert/mark-sent statements
│   └── SqliteLogStore.cs                # ILogStore implementation using Microsoft.Data.Sqlite (50+ methods, SemaphoreSlim gate)
│
├── ViewModels/
│   ├── ViewModelBase.cs                 # Base class (extends ObservableObject from CommunityToolkit.Mvvm)
│   └── MainViewModel.cs                 # Login state, employee info, permission steps (4-step wizard), commands
│
├── Views/
│   ├── MainWindow.axaml                  # XAML layout: login form → permission wizard → employee profile
│   └── MainWindow.axaml.cs              # Code-behind (minimal — just InitializeComponent)
│
├── Converters/
│   ├── BoolInvertConverter.cs           # !bool for visibility bindings
│   ├── StringNotEmptyConverter.cs       # string → bool (show when not empty)
│   └── LoadingToTextConverter.cs        # bool → "Authenticating..." or "Login"
│
├── Styles/
│   └── AppTheme.xaml                    # Dark theme color definitions, brushes, radii, fonts, button styles
│
└── publish/
    ├── build-installer.sh               # Cross-platform installer builder
    ├── encrypt-config.sh                # Config encryption script
    ├── installer-windows.iss            # Inno Setup script (auto-kills running instances)
    ├── release.sh                       # Release workflow script
    ├── build-deb.sh                     # Linux .deb builder (prerm kills running instances)
    ├── build-dmg.sh                     # macOS .dmg builder
    └── {linux,macos,windows}/           # Pre-published platform builds (client + deps)
```

---

## 4. MVVM Layering

### Views → ViewModels → Models → Services

```
┌─────────────────────────────────────────────────────────┐
│  Views (XAML)                                            │
│  MainWindow.axaml — Login, Permission Steps, Profile     │
│  Binds to MainViewModel via {Binding ...}                │
├─────────────────────────────────────────────────────────┤
│  ViewModels                                              │
│  MainViewModel (CommunityToolkit.Mvvm)                    │
│  - [ObservableProperty] for all bindable state            │
│  - [RelayCommand] for Login/Logout/GrantPermission       │
│  - Injected: AppConfig, ILogStore, HttpClient, etc.      │
├─────────────────────────────────────────────────────────┤
│  Models (plain data objects)                              │
│  ActivityLog, AppSession, AppItem, DeviceHardwareInfo,    │
│  InstalledApplication, InstalledPackage, NetworkInfo,     │
│  SessionEvent, StorageDevice, EmployeeInfo, SessionInfo   │
├─────────────────────────────────────────────────────────┤
│  Services / Infrastructure                                │
│  LogCollectorService (BackgroundService)                  │
│  BackgroundGuardService (BackgroundService)               │
│  AutoStartService (singleton)                             │
│  SqliteLogStore (ILogStore implementation)                │
│  Platform collectors (IActivityCollector impls)           │
└─────────────────────────────────────────────────────────┘
```

### DI Container

Microsoft.Extensions.Hosting (`Host.CreateApplicationBuilder`). All services registered in `Program.cs`:

| Lifetime | Services |
|---|---|
| **Singleton** | `AppConfig`, `ILogStore`, `HttpClient`, `IInstalledAppDetector`, `IPackageDetector`, `IActivityCollector`, `AutoStartService`, `LogCollectorService`, `IAccessibilityBrowserReader` (platform reader), `EventCoordinator`, `JourneyEngine`, `ATSPIEventWatcher`, `FileSystemEventWatcher`, `RecentFilesWatcher` |
| **Transient** | `MainViewModel` |
| **Hosted** | `BackgroundGuardService`, `LogCollectorService`, `AccessibilityBrowserTracker`, `DesktopEventService` |

ViewModels are resolved from DI when the window is created (`App.axaml.cs` uses `ServiceProvider.GetRequiredService<MainViewModel>()`).

---

## 5. Local Data Model (SQLite)

### Schema (defined in `DatabaseSchema.CreateTableSql`)

**11 tables:** `device_hardware_info`, `storage_devices`, `installed_applications`, `installed_packages`, `network_info`, `session_events`, `app_sessions`, `app_items`, `app_status`, `permission_status`, `employee_info`.

**`device_hardware_info`** — Hardware snapshot
```sql
CREATE TABLE IF NOT EXISTS device_hardware_info (
    id               TEXT PRIMARY KEY,
    mac_address      TEXT NOT NULL DEFAULT '',
    hostname         TEXT NOT NULL DEFAULT '',
    os_name          TEXT NOT NULL DEFAULT '',
    os_version       TEXT NOT NULL DEFAULT '',
    cpu_model        TEXT NOT NULL DEFAULT '',
    cpu_cores        INTEGER NOT NULL DEFAULT 0,
    ram_total_mb     INTEGER NOT NULL DEFAULT 0,
    gpu_model        TEXT NOT NULL DEFAULT '',
    gpu_vram_mb      INTEGER NOT NULL DEFAULT 0,
    collected_at     TEXT NOT NULL,
    is_synced        INTEGER NOT NULL DEFAULT 0,
    synced_at        TEXT,
    created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
);
```

**`storage_devices`** — Relational child of `device_hardware_info` (replaces the old JSON blob)
```sql
CREATE TABLE IF NOT EXISTS storage_devices (
    id                  TEXT PRIMARY KEY,
    device_hardware_id  TEXT NOT NULL REFERENCES device_hardware_info(id),
    device_type         TEXT NOT NULL DEFAULT '',
    model               TEXT NOT NULL DEFAULT '',
    capacity_mb         INTEGER NOT NULL DEFAULT 0,
    is_synced           INTEGER NOT NULL DEFAULT 0,
    synced_at           TEXT,
    created_at          TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
);
```

**`installed_applications`** — GUI apps only
```sql
CREATE TABLE IF NOT EXISTS installed_applications (
    id               TEXT PRIMARY KEY,
    app_name         TEXT NOT NULL UNIQUE,
    binary_name      TEXT NOT NULL DEFAULT '',
    app_version      TEXT NOT NULL DEFAULT '',
    publisher        TEXT NOT NULL DEFAULT '',
    install_path     TEXT NOT NULL DEFAULT '',
    install_date     TEXT,
    uninstall_string TEXT NOT NULL DEFAULT '',
    change_type      TEXT NOT NULL DEFAULT 'seen',
    is_browser       INTEGER NOT NULL DEFAULT 0,
    desktop_id       TEXT NOT NULL DEFAULT '',
    categories       TEXT NOT NULL DEFAULT '',
    detected_at      TEXT NOT NULL,
    is_synced        INTEGER NOT NULL DEFAULT 0,
    synced_at        TEXT,
    created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
);
-- Upsert: ON CONFLICT(app_name) DO UPDATE ... is_synced = 0 (re-sync on re-detect)
```

**`installed_packages`** — CLI tools/runtimes/libraries
```sql
CREATE TABLE IF NOT EXISTS installed_packages (
    id               TEXT PRIMARY KEY,
    package_name     TEXT NOT NULL,
    version          TEXT NOT NULL DEFAULT '',
    category         TEXT NOT NULL DEFAULT 'tool',
    source_manager   TEXT NOT NULL DEFAULT '',
    install_path     TEXT NOT NULL DEFAULT '',
    publisher        TEXT NOT NULL DEFAULT '',
    description      TEXT NOT NULL DEFAULT '',
    detected_at      TEXT NOT NULL,
    is_synced        INTEGER NOT NULL DEFAULT 0,
    synced_at        TEXT,
    created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
);
-- Upsert: ON CONFLICT(package_name, source_manager) DO UPDATE ... is_synced = 0
-- Unique index created in MigrateSql: idx_installed_packages_fingerprint
```

**`network_info`**
```sql
CREATE TABLE IF NOT EXISTS network_info (
    id                   TEXT PRIMARY KEY,
    public_ip            TEXT NOT NULL DEFAULT '',
    private_ip           TEXT NOT NULL DEFAULT '',
    network_interface_name TEXT NOT NULL DEFAULT '',
    collected_at         TEXT NOT NULL,
    is_synced            INTEGER NOT NULL DEFAULT 0,
    synced_at            TEXT,
    created_at           TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
);
```

**`session_events`** — login/logout/lock/unlock
```sql
CREATE TABLE IF NOT EXISTS session_events (
    id               TEXT PRIMARY KEY,
    event_type       TEXT NOT NULL,
    os_username      TEXT NOT NULL DEFAULT '',
    event_at         TEXT NOT NULL,
    is_synced        INTEGER NOT NULL DEFAULT 0,
    synced_at        TEXT,
    created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
);
```

**`app_sessions`**
```sql
CREATE TABLE IF NOT EXISTS app_sessions (
    id                  TEXT PRIMARY KEY,
    process_name        TEXT NOT NULL,
    app_display_name    TEXT NOT NULL DEFAULT '',      -- resolved from installed_applications.app_name
    started_at          TEXT NOT NULL,
    ended_at            TEXT,
    machine_id          TEXT NOT NULL DEFAULT '',
    employee_id         TEXT,
    employee_name       TEXT,
    session_id          TEXT NOT NULL DEFAULT '',
    platform            TEXT NOT NULL DEFAULT '',
    installed_app_id    TEXT REFERENCES installed_applications(id),
    installed_package_id TEXT REFERENCES installed_packages(id),
    process_id          INTEGER,
    parent_process_id   INTEGER,
    is_synced           INTEGER NOT NULL DEFAULT 0,
    synced_at           TEXT,
    created_at          TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
);
-- Added via MigrateSql ALTERs (idempotent):
--   grouped_by   ('cgroup' | 'pid' | NULL)   -- how the session's identity was grouped
--   cgroup_scope (raw systemd app-*.scope string)  -- needed for boot hydration
--   context_label (VS Code workspace folder / Chrome profile)
```

**`app_items`** — Generic self-referencing child of `app_sessions` (replaces browser_contexts/file_explorer_contexts/urls/url_visits)
```sql
CREATE TABLE IF NOT EXISTS app_items (
    id                TEXT PRIMARY KEY,
    app_session_id    TEXT NOT NULL REFERENCES app_sessions(id),
    parent_item_id    TEXT REFERENCES app_items(id),
    item_type         TEXT NOT NULL DEFAULT '',     -- 'tab', 'browser_tab', 'browser_navigation', 'terminal', 'folder', 'file'
    title             TEXT NOT NULL DEFAULT '',
    identifier        TEXT NOT NULL DEFAULT '',
    url               TEXT NOT NULL DEFAULT '',
    domain            TEXT NOT NULL DEFAULT '',
    opened_at         TEXT NOT NULL,
    closed_at         TEXT,
    process_id        INTEGER,
    is_synced         INTEGER NOT NULL DEFAULT 0,
    synced_at         TEXT,
    created_at        TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now')),
    object_type       TEXT NOT NULL DEFAULT '',     -- journey fields (9)
    action            TEXT NOT NULL DEFAULT '',
    journey_id        TEXT NOT NULL DEFAULT '',
    sequence          INTEGER NOT NULL DEFAULT 0,
    previous_path     TEXT NOT NULL DEFAULT '',
    current_path      TEXT NOT NULL DEFAULT '',
    window_id         INTEGER,
    tab_id            INTEGER,
    metadata_json     TEXT NOT NULL DEFAULT '{}'
);
```

**`app_status`** — key-value store for flags (login state, permission statuses, `last_heartbeat_at`)

**`permission_status`** — records permission check results per session (pruned older than 24h on write)

**`employee_info`**
```sql
CREATE TABLE IF NOT EXISTS employee_info (
    id              TEXT PRIMARY KEY,
    employee_id     TEXT NOT NULL,
    name            TEXT NOT NULL,
    email           TEXT NOT NULL,
    role            TEXT NOT NULL,
    department      TEXT NOT NULL,
    shift           TEXT,
    avatar          TEXT,
    avatar_color    TEXT,
    token           TEXT,                  -- JWT token for API calls
    logged_in_at    TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
);
```

### Migration Strategy

**`DatabaseSchema.MigrateSql` + `SqliteLogStore.InitializeAsync`.** The base schema is created via `CreateTableSql` (`IF NOT EXISTS`) on every start. `MigrateSql` then runs a batch of **idempotent `ALTER TABLE ... ADD COLUMN`** statements (all the `process_id`, journey, `grouped_by`/`cgroup_scope`/`context_label`, `url`/`domain`, identity columns added after the base schema shipped) plus the package dedup block:
- Deletes duplicate `installed_packages` rows keeping the newest per `(package_name, source_manager)` using a window function
- `CREATE UNIQUE INDEX IF NOT EXISTS idx_installed_packages_fingerprint ON installed_packages(package_name, source_manager)`

There is no numbered version table; the ALTERs are re-run idempotently each boot.

---

## 6. Data Collection Flow

### Collection Trigger

`LogCollectorService` is a `BackgroundService` (runs in the background). It:

1. Waits in an idle loop (checking every 5s) until `_trackingEnabled` is set to `true` by `MainViewModel.StartTracking()`.
2. Once tracking is active, collects on a configurable interval (default: 30 seconds, `ALPHA_COLLECT_INTERVAL_SEC`).

On startup it also runs, in order:
- `ReconcileStaleSessionsOnBootAsync` — closes sessions whose heartbeat is stale (crash recovery)
- `CleanupGarbageSessionRowsAsync` — closes old `--type=` Chromium subprocess rows
- `CleanupNonGuiAppEntriesAsync` — removes non-GUI `installed_applications` entries (sh, snap) auto-registered before the GUI gate
- Immediate hardware + network collection

### Browser Journey (Option B — accessibility tree)

`AccessibilityBrowserTracker` is an independent `BackgroundService` that does NOT wait for login and does NOT depend on the installed-apps catalog:

1. Every `ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS` (default 3) it calls `IAccessibilityBrowserReader.ReadAsync()` — a short platform probe that returns the currently-visible browser windows (`AccessibilitySnapshot`: window key, pid, process, title, normalized address-bar URL, incognito flag).
2. **Linux** — embedded python3 + AT-SPI D-Bus script: FRAME/WINDOW nodes → the `ENTRY` node named `"Address and search bar"` (the omnibox) whose `Text` is the exact URL/query (scheme-less — the reader prepends `https://`); page title = FRAME `Name`. Validated live on Chrome 151.
3. **Windows** — UIA via `Interop.UIAutomationClient`: top-level Window elements → Edit elements; the `"Address and search bar"` edit's `ValuePattern.CurrentValue` is the URL.
4. **macOS** — osascript/System Events: window titles + best-effort address-bar value (requires the Accessibility permission granted at install).
5. **URL fallback — `BrowserHistoryReader`** (new, `ALPHA_BROWSER_HISTORY_ENABLED`, default on): after `ReadAsync`, `EnrichUrlsFromHistoryAsync` fills any snapshot whose a11y URL is empty by matching the window title against the browser's own profile history database — Chromium `History` (Chrome/Edge/Brave/Opera/Vivaldi) and Firefox `places.sqlite`. Read while the browser runs via temp copy of DB + `-wal`/`-shm`/`-journal` sidecars (read-only; torn snapshots retried next poll), throttled by change signatures (`ALPHA_BROWSER_HISTORY_POLL_SECONDS`, default 10). Resolution is STRICTLY title-match — no unconditional newest-visit fallback, so multiple open windows never receive a misattributed URL. Generic scans of `~/.config`/`~/.mozilla` (plus Windows/macOS profile roots) discover brand-new browsers, so install→use→uninstall-in-5-min journeys are captured while the browser is alive. URLs recovered this way are tagged `metadata_json.source="history"` (vs `"a11y"`).
6. For each window: first sight → new `app_sessions` row + `browser_tab` root `app_items` row (`url`/`domain`/`journey_id`); URL change → `browser_navigation` child item (`previous_path` → `current_path`); title-only change → root item refresh (URL-less titles don't rotate until history catches up).
7. A window absent for 3 consecutive polls is closed; a window with no URL/title change for `ALPHA_BROWSER_JOURNEY_IDLE_MINUTES` (15) is idle-closed; shutdown closes everything gracefully via `CloseSessionsAndAppItemsAsync` (no orphans).
8. Downloads in `~/Downloads` are appended to the most recent browser session as `browser_download` items.
9. Incognito windows are detected (title/cmdline) and flagged in metadata; their URLs are stored only when `ALPHA_BROWSER_CAPTURE_INCOGNITO=true` (default off — legal-safe; private browsing is not silently recorded; private visits are also absent from the profile History DB).

This replaces the debugger pipeline entirely — it works on every Chrome version (incl. 136+), every browser (a11y URL where exposed, profile History fallback everywhere else — Chrome 136+ on Linux, snap Firefox), and even a browser installed → used → uninstalled within minutes (nothing to detect; it reads the screen + the profile diary).

### Collection Cycle (every 30s)

```
┌─────────────────┐     ┌──────────────────────────────┐     ┌────────────────┐
│ IActivityCollect │────>│ ResolveAppInfo() per process │────>│ Store in SQLite │
│ .CollectAsync()  │     │  - GUI gate (IsGuiApplication)│     │ via ILogStore   │
│                  │     │  - binary_name fuzzy match    │     │ .StoreAppSessionsAsync() │
└─────────────────┘     │  - display name + FKs          │     └────────────────┘
         │              └──────────────────────────────┘             │
         │ Every 30s                                                 │
         ▼                                                           │
┌─────────────────┐                                                  │
│ CgroupResolver  │──▶ BuildSessionKey (scope-aware) ────────────────┤
└─────────────────┘   close stale sessions (CloseSessionsAndAppItemsAsync)
```

### Filtering

- **ProcessFilter.cs** — filters out kernel/system processes (first stage)
- **`NonAppProcesses` / `NonAppProcessPrefixes`** (LogCollectorService) — exact + prefix blocklist (GNOME daemons, gvfsd-*, gsd-*, etc.)
- **`ResolveAppInfo()`** resolves each process against SQLite `installed_applications` (via `binary_name` fuzzy match) and `installed_packages` (via `package_name`):
  - **Known GUI apps** (found in `installed_applications`): tracked with full context — both with and without window titles (Wayland-native apps like VSCode don't appear in X11 client list)
  - **Unknown processes detected as GUI**: scanned for .desktop files (Linux), .app bundles (macOS), or Start Menu/Program Files entries (Windows) via `IsGuiApplication()`; if GUI, auto-registered into `installed_applications` and tracked
  - **CLI-only tools, shells, build tools, runtimes, and daemons**: **SKIPPED entirely** — no `installed_packages` entry created, no `app_session` created, no `app_items` created
  - **Headless Chromium/Electron subprocesses**: filtered via `--type=` cmdline detection (`ChromiumSubprocessFlags`)
  - **Unresolvable processes**: silently skipped
- `AppDisplayName` is set from the installed app's `app_name` (e.g., `"Visual Studio Code"`), not from the OS process name (`"code"`) or window title
- `InstalledAppId` / `InstalledPackageId` FK columns link each session to its app/package record
- **Wayland note**: Linux window title enumeration uses X11 `xprop _NET_CLIENT_LIST`. On Wayland, only XWayland windows appear (not native Wayland windows). Foreground detection via AT-SPI/gdbus still works for the active window.

### Session Identity & Hierarchy

- Sessions are keyed by **PID + cgroup scope** (`BuildSessionKey`). Multi-process GUI apps (VS Code, Chrome) collapse to ONE `app_sessions` row per logical window via systemd `app-*.scope`; `grouped_by`/`cgroup_scope`/`context_label` record how and what.
- `SessionHierarchyResolver` walks the OS PPID tree (through runtimes/build tools) to link child sessions under their parent via `parent_item_id`.
- On session close, `CloseSessionsAndAppItemsAsync()` closes the session AND its open `app_items` in ONE transaction (no orphans).

### Batching / Offline Behavior

- Unsent rows are stored in SQLite with `is_synced = 0` / `synced_at = NULL`
- Every 10 collection cycles (~5 min), `SyncUnsentData()` sends unsent rows per table in batches of up to **500**
- Order matters: sessions sync before items (parents before children)
- On success, marks them as synced
- On failure (server unreachable, auth error), does NOT retry immediately — waits for the next sync cycle
- **No exponential backoff** — retries every ~5 min regardless of failure count
- **No deduplication on the client** — server uses `ON CONFLICT` (fingerprint for catalogs, `ON CONFLICT(id)` for sessions/items) to deduplicate

### Periodic Collection (30s cycle)

| Data | Frequency | Method |
|---|---|---|
| Device hardware | Every 30 cycles (~15 min) | `CollectDeviceHardwareAsync` |
| Network info | Every 10 cycles (~5 min) | `CollectNetworkInfoAsync` (dedup by IP change) |
| Installed apps scan | Every 30 cycles (~15 min) | `CollectInstalledApplicationsAsync` |
| Installed packages scan | Every 60 cycles (~30 min) | `CollectInstalledPackagesAsync` |
| Sync unsent + permission status | Every 10 cycles (~5 min) | `SyncUnsentData` + `StorePermissionStatus` |
| Heartbeat (`last_heartbeat_at`) | Every cycle | `SetStatusAsync` |

---

## 7. Sync/Transport to Server

### Protocol

- **REST over HTTPS** (or HTTP in dev)
- **JSON** payloads: `{employeeId, token, entries: [...]}`
- **No retry header, no idempotency key**

### Auth

- Employee authenticates via `POST /api/v1/auth/employee-login` with `{employeeId, secretKey}`
- Server returns `{employee, token}` where `token` is an **encrypted JWT**
- Token stored in local SQLite (`employee_info.token`)
- All subsequent sync calls include `token` in the request body

### Sync Endpoints Called by Client

| Endpoint | Method | Payload | Status |
|---|---|---|---|
| `/api/v1/device-hardware/sync` | POST | `{employeeId, token, entries: [...]}` | ✅ Exists |
| `/api/v1/installed-apps/sync` | POST | `{employeeId, token, entries: [...]}` (`binaryName`, `isBrowser`, `desktopId`, `categories`) | ✅ Exists |
| `/api/v1/installed-packages/sync` | POST | `{employeeId, token, entries: [...]}` | ✅ Exists |
| `/api/v1/network-info/sync` | POST | `{employeeId, token, entries: [...]}` | ✅ Exists |
| `/api/v1/session-events/sync` | POST | `{employeeId, token, entries: [...]}` | ✅ Exists |
| `/api/v1/app-sessions/sync` | POST | `{employeeId, token, entries: [...]}` (`installedAppId`, `installedPackageId`, `processId`, `parentProcessId`, `groupedBy`, `cgroupScope`, `contextLabel`) | ✅ Exists |
| `/api/v1/app-items/sync` | POST | `{employeeId, token, entries: [...]}` (`parentItemId`, `processId`, 9 journey fields, `url`/`domain`) | ✅ Exists |
| `/api/v1/auth/employee-login` | POST | `{employeeId, secretKey}` | ✅ Exists |
| `/api/v1/auth/employee-disconnect` | POST | `{employeeId, token}` | ✅ Exists |

> **Removed:** `activity-logs/sync` and `shell-commands/sync` are gone. No `activity_logs` table, no `shell_commands` table, no sync call.

### Sync Frequency

| Data Type | Frequency | Batch Size |
|---|---|---|
| All sync tables | Every ~5 min (10 cycles) | Max 500 rows per table |
| Installed packages scan | Every ~30 min (60 cycles) | Max 500 rows |
| Heartbeat | Every cycle (30s) | — |

---

## 8. Installer / Deployment

### Build Scripts (`client/publish/`)

| Script | Purpose |
|---|---|
| `build-installer.sh` | Cross-platform: detects OS and calls platform-specific builder |
| `build-deb.sh` | Linux: creates `.deb` package with prerm script to kill running instances |
| `build-dmg.sh` | macOS: creates `.dmg` disk image |
| `installer-windows.iss` | Windows: Inno Setup script for `.exe` installer (auto-kills running processes) |
| `release.sh` | GitHub release workflow script |
| `encrypt-config.sh` | Encrypts `.env` → `config.enc` for distribution |

### Config Encryption Flow

1. **Build time**: `./encrypt-config.sh` encrypts `.env` → `config.enc` using a hardcoded transport key (SHA256 of `"AlphaAITracker:TransportKey:v1"`)
2. **Install time**: `config.enc` is bundled with the installer
3. **First launch**: `EnvLoader` decrypts using transport key, then immediately re-encrypts with a **machine-derived key** (SHA256 of stable machine ID: `/etc/machine-id`, Windows `MachineGuid`, macOS `IOPlatformUUID`)
4. **Subsequent launches**: decrypts using machine key. If machine key fails (e.g., after OS reinstall), falls back to transport key.

### Distribution

- No auto-update mechanism exists
- Releases are manually built and uploaded to GitHub
- The web dashboard has a "Download App" dialog that fetches the latest release from GitHub API, filtering assets by platform pattern (`.exe`, `.deb`, `.dmg`)
- Default GitHub repo in web config: `clickmaster4285/Alpha-AI-Tracker` (overridable via `NEXT_PUBLIC_GITHUB_REPO`). Note: `client/.env.example` still carries a stale `REPO=AlphaDev-7/Alpha-AI-Tracker` value that is **not read by the client** — the web dashboard is the source of the download link.

### ⚠️ Build-Parity Rule (mandatory) — `dotnet run` ≠ installed build

`dotnet run` compiles from the source tree, so it always runs the newest code. Installed builds (`.deb` / `.exe` / `.dmg` via `build-installer.sh` + the platform builders) ship the **publish output** plus only what the scripts explicitly bundle, from a root-owned install dir. **A change is NOT done until it works from an installed build.**

Checklist:

1. **Always ship-test** — build the installer, install the artifact, run the new functionality there. `dotnet run` success is not sufficient.
2. **New runtime assets** (icons, JSON, images) — must be copied by `bundle_into_publish()` in `build-installer.sh` or by `build-deb.sh` / `build-dmg.sh` / `installer-windows.iss`.
3. **New files in `extensions/`** — bundled automatically; keep extension / native-host files there.
4. **New scripts in `publish/`** — copied into every publish output automatically.
5. **New env vars** — add to `.env` BEFORE `encrypt-config.sh`; installers ship `config.enc` baked at build time (dev reads `.env` directly).
6. **No writes to the exe dir** — installed app runs from root-owned `/usr/share/alpha-ai-tracker/` (Linux) / Program Files (Windows). Write only to `~/.config/alpha-ai-tracker/` (logs, machine-id) and `~/.local/share/alpha-ai-tracker/` (DB, sockets).
7. **Packaging edits apply to all platforms** — update `build-installer.sh`, `build-deb.sh`, `build-dmg.sh`, `installer-windows.iss` together.
8. **Stale-binary guard** — `build-installer.sh` aborts if source is newer than the published `client.dll`; run `dotnet clean && bash publish/build-installer.sh`. This guard covers compiled code only — assets are your responsibility (items 2–6).

---

## 9. Production-Readiness Gaps

| Gap | Severity | Details |
|---|---|---|
| **No crash handling** | 🔴 High | Unhandled exceptions crash the app. No global exception handler, no crash dump, no telemetry. `AppDomain.CurrentDomain.UnhandledException` is not subscribed. |
| **No auto-update** | 🔴 High | Employees must manually download and reinstall new versions. No update notification, no silent update. |
| **No tamper resistance** | 🟠 Medium | SQLite database is unencrypted (encryption code is commented out). Config encryption can be bypassed with debugger. Process collection can be stopped by killing the process (though auto-start watchdog re-launches). |
| **macOS CPU = 0%** | 🟠 Medium | macOS `ProcessCollector` does not measure CPU. All macOS CPU values are 0. |
| **macOS limited window capture** | 🟢 Low | Only captures foreground window via `osascript`. No EnumWindows equivalent. Background window titles are never captured on macOS. |
| **No storage quota** | 🟢 Low | SQLite grows unbounded (no periodic deletion of old synced data — only the `permission_status` 24h prune and boot-time cleanup passes exist). Could fill disk on a heavily used machine. |
| **No retry backoff** | 🟢 Low | Retries sync every ~5 min regardless of failure count. No exponential backoff. |
| **Single employee per device** | 🟢 Low | Only one employee can be logged in at a time. Logging in as a different employee wipes the previous session. |
| **Browser journey = active tab only** | 🟢 Low | The accessibility tree + profile-History fallback cover the ACTIVE tab's URL. Background tabs are not captured (Chrome does not expose them to AT-SPI, and history title-match is window-title based). Window-level journey is exact; per-tab parallelism is not. |

---

## 10. Immediate Next Steps

1. **Add tests** — start with unit tests for `SqliteLogStore`, `ProcessFilter`, and `PackageDetector`
2. **Add crash reporting** — subscribe to `AppDomain.CurrentDomain.UnhandledException` and log to a file
3. **Enable SQLite encryption** — uncomment the sqlcipher path in `SqliteLogStore` constructor and switch provider
4. **Fix macOS CPU measurement** — sample `TotalProcessorTime` like Windows/Linux
5. **Fix macOS window title capture** — only captures foreground window currently
6. **Add periodic data cleanup** — delete old synced rows (only `permission_status` is pruned today)
7. **Add offline retry with backoff** — exponential backoff on sync failures to reduce server load
8. **Consider auto-update** — integrate Velopack or Squirrel.Windows for silent updates
9. ~~**Remove dead shell-command code** — `IShellCommandCollector`, 3 impls, `ShellCommand` model deleted (2026-08-05)~~ ✅ DONE
10. ~~**Add process ancestry tracking** — persist parent PID chain from `ParentProcessResolver` into `AppItem`~~ ✅ DONE
11. ~~**Browser journey via debugger/extension** — REPLACED (2026-08-05) by Option B: accessibility-tree capture (`Core/BrowserAccessibility`), validated on Chrome 151 — works on Chrome 136+, no extension, no debugger~~ ✅ DONE
12. ~~**AT-SPI for Wayland window enumeration** — File Explorer journey tracking (3 watchers + EventCoordinator + JourneyEngine)~~ ✅ DONE
13. ~~**File manager via `xdg-open` hook or inotify** — RecentFilesWatcher + FileSystemEventWatcher + ATSPIEventWatcher~~ ✅ DONE
14. **Installed-build acceptance** — rebuild the installer (installer-parity rule) and verify the a11y journey from the installed build
