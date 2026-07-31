# Client Architecture — Alpha AI Tracker Desktop App

> **Last audited:** 2026-07-31 (cgroup-based session dedup + atomic cascade-close)  
> **Changelog:** 
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
> - 2026-07-28: **Added `BrowserExtensionService`** — Browser detection + two-strategy extension install (--load-extension → profile injection). Async-safe. Extension detection via process monitoring.
> - 2026-07-28: **Added `url`/`domain` columns to `app_items`** schema + server DTOs.
> - 2026-07-28: **Fixed `InstallNativeHostManuallyAsync`** — computes extension ID into `allowed_origins`.
> - 2026-07-28: **Fixed extension active detection** — replaced socket-based `fuser` with process-based `pgrep native-host.py + pgrep chrome`.
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
> - 2026-07-27: **Added `BuildToolProcesses` set** (`make`, `go`, `npm`, `npx`, `cargo`, `pip`, `tsc`, etc.) — build tools are auto-registered as `installed_packages` (category=`tool`) and tracked without a window title.
> - 2026-07-27: **Fixed process filter** — known-app processes (`appId != null`) and build tools are now tracked even without a window title. Previously, Wayland-native apps (VSCode, Chrome, Nautilus) were silently dropped because they don't appear in X11 window list.
> - 2026-07-27: **Broadened `AutoDetectInstalledApp`** — now accepts `/home/*` and `/media/*` paths as valid install locations (covers project-local compiled binaries like `./bin/alpha-ai-server`).
> - 2026-07-27: **Fixed file manager path resolution** — `ParseFileManagerContext` now resolves folder display names to absolute paths by searching `~/`, `~/Documents`, `~/Desktop`, `/media/<user>/`, etc.
> - 2026-07-27: **Fixed `SessionHierarchyResolver`** — `ResolveParent` now walks through build tools and runtime packages as intermediate PPID steps; `ShouldLinkTo` now accepts build tools as children of IDEs and terminals.
> **Service completion (honest):** ~87%

---

## 1. Responsibility & Scope

**Owns:**
- Collecting process activity data (window titles, CPU%, memory) from employee machines
- Reading shell/terminal command histories (bash, zsh, fish, PowerShell, cmd)
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

---

## 2. Tech Stack Detail

| Component | Technology | Version |
|---|---|---|
| **Language** | C# (.NET) | net10.0 |
| **UI Framework** | Avalonia | 12.1.0 |
| **Desktop** | Avalonia.Desktop | 12.1.0 |
| **Theme** | Avalonia.Themes.Fluent | 12.1.0 |
| **MVVM Toolkit** | CommunityToolkit.Mvvm | 8.4.2 |
| **SQLite Driver** | Microsoft.Data.Sqlite | 10.0.10 |
| **SQLite Bundle** | SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 |
| **DI/Hosting** | Microsoft.Extensions.Hosting | 10.0.10 |
| **HTTP Client** | System.Net.Http (built-in) | — |
| **D-Bus** | Tmds.DBus.Protocol | 0.94.2 |

### Notable Omissions

- **No ORM** — raw SQL via `SqliteCommand` and parameterized queries
- **No test framework** — no xUnit, NUnit, or any test project
- **No logging framework beyond ILogger** — console-only in dev
- **No auto-updater library** — no Squirrel, Velopack, or similar

---

## 3. Project Structure

```
client/
├── Program.cs                          # Entry point. DI setup, CLI modes (--encrypt-config, --background), mutex
├── App.axaml / App.axaml.cs            # Avalonia app lifecycle, tray icon, window close interception (hide vs shutdown)
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
│   │   ├── ILogStore.cs                # 16 methods: store, retrieve unsent, mark sent, cleanup, employee info, shell commands
│   │   ├── IInstalledAppDetector.cs    # GUI/desktop app detection + permission status
│   │   ├── IPackageDetector.cs         # CLI tool/runtime/library detection from package managers
│   │   └── IShellCommandCollector.cs   # Shell command collection + accessibility status
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
│   │   ├── AppSession.cs               # App session + BrowserContext + FileExplorerContext + UrlRecord + UrlVisit + 9 journey fields
│   │   ├── DeviceHardwareInfo.cs       # Phase 1: DeviceHardwareInfo + InstalledApplication + InstalledPackage + NetworkInfo + SessionEvent
│   │   ├── EmployeeInfo.cs             # Employee login info (persisted in SQLite)
│   │   ├── SessionInfo.cs              # Static session ID (generated once per app launch)
│   │   └── ShellCommand.cs             # Shell command model (15 fields)
│   ├── EncryptedConfigService.cs       # AES-256-GCM encryption with transport key + machine-derived key
│   ├── CgroupResolver.cs               # Linux /proc/<pid>/cgroup → systemd app-*.scope (session dedup key; null elsewhere)
│   ├── SessionLabelResolver.cs         # Session context_label: VS Code workspace folder / Chrome --profile-directory
│   ├── InstalledAppDetector.cs         # Cross-platform installed app detection (desktop files, registry, .app bundles) — GUI only
│   ├── PackageDetector.cs              # Cross-platform installed package detection (npm, pip, apt, brew, choco, winget, scoop, etc.)
│   ├── ProcessFilter.cs                # Filters kernel/system processes from collection
│   ├── ParentProcessResolver.cs        # Resolves window titles from parent processes (e.g., terminal → shell)
│   ├── AppProcessClassifier.cs         # Browser/file-manager/IDE/shell/runtime classification + item_type
│   ├── ActivityContextParser.cs        # URL + file path extraction from window titles
│   └── SessionHierarchyResolver.cs     # PID-tree parent linking (node→terminal→IDE)
│
├── Platform/
│   ├── Windows/
│   │   ├── ProcessCollector.cs          # User32.dll-based: EnumWindows, GetForegroundWindow, CPU via TotalProcessorTime
│   │   └── ShellCommandCollector.cs     # PowerShell PSReadLine history + cmd.exe Command History XML
│   ├── MacOS/
│   │   ├── ProcessCollector.cs          # osascript-based foreground window detection. ⚠️ No CPU measurement, no all-window-enum
│   │   └── ShellCommandCollector.cs     # bash/zsh/fish history files
│   └── Linux/
│       ├── ProcessCollector.cs          # Multi-strategy: xprop, xdotool, gdbus (atspi/portal/shell), Python AT-SPI script
│       └── ShellCommandCollector.cs     # bash/zsh/fish history files + /proc/*/cmdline for running shells
│
├── Services/
│   ├── LogCollectorService.cs           # BackgroundService: collect → filter → store app_sessions → sync → cleanup cycle (30s loop)
│   ├── NativeMessageService.cs          # BackgroundService: Unix socket listener for browser navigation events (Native Messaging bridge)
│   ├── BrowserExtensionService.cs       # Browser detection + extension install (two-strategy: --load-extension / profile injection)
│   ├── BackgroundGuardService.cs        # Watchdog: re-installs auto-start/systemd if removed (60s check)
│   ├── AutoStartService.cs              # Platform-specific auto-start: Run key, .desktop, launchd plist
│   ├── DesktopEventService.cs           # BackgroundService: orchestrator — starts watchers, wires EventCoordinator → JourneyEngine
│   └── Watchers/
│       ├── ATSPIEventWatcher.cs         # Linux AT-SPI via Tmds.DBus.Protocol — focus/window events + /proc/cwd for file manager paths
│       ├── FileSystemEventWatcher.cs    # FileSystemWatcher on 7 user directories — create/delete/rename/modify enrichment
│       └── RecentFilesWatcher.cs        # XBEL monitor at ~/.local/share/recently-used.xbel — recent-file-opens evidence
│
├── Storage/
│   ├── DatabaseSchema.cs                # Raw SQL for all table creation (9 new tables + shell_commands + employee_info + app_status + permission_status)
│   └── SqliteLogStore.cs                # ILogStore implementation using Microsoft.Data.Sqlite (45+ methods)
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
    └── build-dmg.sh                     # macOS .dmg builder
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
│  ActivityLog, EmployeeInfo, ShellCommand, SessionInfo     │
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
|---|---|---|
| **Singleton** | `AppConfig`, `ILogStore`, `HttpClient`, `IInstalledAppDetector`, `IActivityCollector`, `AutoStartService`, `LogCollectorService`, `EventCoordinator`, `JourneyEngine`, `ATSPIEventWatcher`, `FileSystemEventWatcher`, `RecentFilesWatcher` |
| **Transient** | `MainViewModel` |
| **Hosted** | `BackgroundGuardService`, `LogCollectorService`, `NativeMessageService`, `DesktopEventService` |

ViewModels are resolved from DI when the window is created (`App.axaml.cs` uses `ServiceProvider.GetRequiredService<MainViewModel>()`).

---

## 5. Local Data Model (SQLite)

### Schema (defined in `DatabaseSchema.cs`)

**`activity_logs` table**
```sql
CREATE TABLE IF NOT EXISTS activity_logs (
    id              TEXT PRIMARY KEY,
    machine_id      TEXT NOT NULL,
    timestamp       TEXT NOT NULL,        -- ISO 8601 string
    process_name    TEXT NOT NULL,
    window_title    TEXT,
    process_id      INTEGER NOT NULL,
    cpu_percent     REAL DEFAULT 0,
    memory_bytes    INTEGER DEFAULT 0,
    is_foreground   INTEGER DEFAULT 0,
    user_name       TEXT,
    platform        TEXT NOT NULL,
    session_id      TEXT,
    employee_id     TEXT,
    employee_name   TEXT,
    synced_at       TEXT,                  -- NULL = unsent, set on successful sync
    created_at      TEXT DEFAULT (datetime('now'))
);
CREATE INDEX idx_logs_unsent ON activity_logs(synced_at, timestamp);
CREATE INDEX idx_logs_timestamp ON activity_logs(timestamp DESC);
CREATE INDEX idx_logs_machine ON activity_logs(machine_id, timestamp DESC);
```

**`shell_commands` table**
```sql
CREATE TABLE IF NOT EXISTS shell_commands (
    id              TEXT PRIMARY KEY,
    machine_id      TEXT NOT NULL,
    timestamp       TEXT NOT NULL,
    shell_name      TEXT NOT NULL,         -- "bash", "zsh", "powershell", etc.
    shell_pid       TEXT,
    command         TEXT NOT NULL,
    working_directory TEXT,
    exit_code       TEXT,
    user_name       TEXT,
    platform        TEXT NOT NULL,
    session_id      TEXT,
    employee_id     TEXT,
    employee_name   TEXT,
    synced_at       TEXT,
    created_at      TEXT DEFAULT (datetime('now'))
);
```

**`installed_applications` table**
```sql
CREATE TABLE IF NOT EXISTS installed_applications (
    id               TEXT PRIMARY KEY,
    app_name         TEXT NOT NULL UNIQUE,
    binary_name      TEXT NOT NULL DEFAULT '',      -- executable name e.g. "code" → maps "code" to "Visual Studio Code"
    app_version      TEXT NOT NULL DEFAULT '',
    publisher        TEXT NOT NULL DEFAULT '',
    install_path     TEXT NOT NULL DEFAULT '',
    install_date     TEXT,
    uninstall_string TEXT NOT NULL DEFAULT '',
    change_type      TEXT NOT NULL DEFAULT 'seen',
    detected_at      TEXT NOT NULL,
    is_synced        INTEGER NOT NULL DEFAULT 0,
    synced_at        TEXT,
    created_at       TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
);
CREATE INDEX IF NOT EXISTS idx_installed_apps_binary ON installed_applications(binary_name);
```

**`installed_packages` table**
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
```

**`app_sessions` table**
```sql
CREATE TABLE IF NOT EXISTS app_sessions (
    id                  TEXT PRIMARY KEY,
    process_name        TEXT NOT NULL,
    app_display_name    TEXT NOT NULL DEFAULT '',      -- resolved from installed_applications.app_name (not window title)
    started_at          TEXT NOT NULL,
    ended_at            TEXT,
    machine_id          TEXT NOT NULL DEFAULT '',
    employee_id         TEXT,
    employee_name       TEXT,
    session_id          TEXT NOT NULL DEFAULT '',
    platform            TEXT NOT NULL DEFAULT '',
    installed_app_id    TEXT REFERENCES installed_applications(id),    -- FK to GUI app
    installed_package_id TEXT REFERENCES installed_packages(id),       -- FK to CLI package
    is_synced           INTEGER NOT NULL DEFAULT 0,
    synced_at           TEXT,
    created_at          TEXT DEFAULT (strftime('%Y-%m-%dT%H:%M:%S.000Z', 'now'))
);
-- Added via MigrateSql ALTERs (idempotent):
--   process_id, parent_process_id,
--   grouped_by   ('cgroup' | 'pid' | NULL),   -- how the session's identity was grouped
--   cgroup_scope (raw systemd app-*.scope string),  -- needed for boot hydration
--   context_label (VS Code workspace folder / Chrome profile)
```

**`employee_info` table**
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
    logged_in_at    TEXT DEFAULT (datetime('now'))
);
```

**`app_status` table** — key-value store for flags (login state, permission statuses)

**`permission_status` table** — records permission check results per session

### Migration Strategy

**None exist.** The schema is created fresh via `DatabaseSchema.CreateTableSql` on every app start (`IF NOT EXISTS`). There is no version tracking or migration system. Schema changes require a wipe or manual migration.

---

## 6. Data Collection Flow

### Collection Trigger

`LogCollectorService` is a `BackgroundService` (runs in the background). It:

1. Waits in an idle loop (checking every 5s) until `_trackingEnabled` is set to `true` by `MainViewModel.StartTracking()`.
2. Once tracking is active, collects on a configurable interval (default: 30 seconds, `ALPHA_COLLECT_INTERVAL_SEC`).

### Collection Cycle (every 30s)

```
┌─────────────────┐     ┌──────────────────────┐     ┌────────────────┐
│ IActivityCollect │────>│ Filter to INSTALLED   │────>│ Store in SQLite │
│ .CollectAsync()  │     │ apps with window      │     │ via ILogStore   │
│                  │     │ titles only            │     │ .StoreAsync()   │
└─────────────────┘     └──────────────────────┘     └────────────────┘
         │                                                      │
         │ Every 30s                                           │
         ▼                                                      │
┌─────────────────┐                                            │
│ IShellCommand   │─────────────────────────────────────────────┘
│ .CollectNew()   │────> StoreShellCommandsAsync()
└─────────────────┘
```

### Filtering

- **ProcessFilter.cs** — filters out kernel/system processes
- **LogCollectorService** — resolves each process against SQLite `installed_applications` (via `binary_name`) and `installed_packages` (via `package_name`):
- **Known GUI apps** (found in `installed_applications` via binary_name match): tracked with full context — both with and without window titles (Wayland-native apps like VSCode don't appear in X11 client list)
- **Unknown processes detected as GUI**: scanned for .desktop files (Linux), .app bundles (macOS), or Start Menu/Program Files entries (Windows) via `IsGuiApplication()`; if GUI, auto-registered into `installed_applications` and tracked
- **CLI-only tools, shells, build tools, runtimes, and daemons**: **SKIPPED entirely** — no `installed_packages` entry created, no `app_session` created, no `app_items` created
- **Unresolvable processes**: silently skipped
- `AppDisplayName` is set from the installed app's `app_name` (e.g., `"Visual Studio Code"`), not from the OS process name (`"code"`) or window title
- `InstalledAppId` / `InstalledPackageId` FK columns link each session to its app/package record
- **Wayland note**: Linux window title enumeration uses X11 `xprop _NET_CLIENT_LIST`. On Wayland, only XWayland windows appear (not native Wayland windows). Foreground detection via AT-SPI/gdbus still works for the active window.

### Batching / Offline Behavior

- Unsent logs are stored in SQLite with `synced_at = NULL`
- Every 10 collection cycles (~5 min), `SyncUnsentLogs()` reads up to 100 unsent logs and sends them to the server
- On success, marks them as synced
- On failure (server unreachable, auth error), does NOT retry immediately — waits for the next sync cycle
- **No exponential backoff** — retries every ~5 min regardless of failure count
- **No deduplication** — if sync succeeds but client doesn't get the 200 OK (network timeout), logs could be sent twice. Server uses `ON CONFLICT (id, employee_id) DO NOTHING` to deduplicate.

### Cleanup

- Every 120 cycles (~60 min): deletes synced logs older than 24h, unsynced logs older than 30 days

---

## 7. Sync/Transport to Server

### Protocol

- **REST over HTTPS** (or HTTP in dev)
- **JSON** payloads
- **No retry header, no idempotency key**

### Auth

- Employee authenticates via `POST /api/v1/auth/employee-login` with `{employeeId, secretKey}`
- Server returns `{employee, token}` where `token` is an **encrypted JWT**
- Token stored in local SQLite (`employee_info.token`)
- All subsequent sync calls include `token` in the request body

### Sync Endpoints Called by Client

| Endpoint | Method | Payload | Status |
|---|---|---|---|
| `/api/v1/activity-logs/sync` | POST | `{employeeId, token, logs: [...]}` | ✅ Exists |
| `/api/v1/shell-commands/sync` | POST | `{employeeId, token, commands: [...]}` | ❌ **Does not exist on server** |
| `/api/v1/device-hardware/sync` | POST | `{employeeId, token, entries: [...]}` | ✅ Exists |
| `/api/v1/installed-apps/sync` | POST | `{employeeId, token, entries: [...]}` | ✅ Exists |
| `/api/v1/installed-packages/sync` | POST | `{employeeId, token, entries: [...]}` | ✅ Exists (new) |
| `/api/v1/network-info/sync` | POST | `{employeeId, token, entries: [...]}` | ✅ Exists |
| `/api/v1/session-events/sync` | POST | `{employeeId, token, entries: [...]}` | ✅ Exists |
| `/api/v1/app-sessions/sync` | POST | `{employeeId, token, entries: [...]}` including `installedAppId` / `installedPackageId` | ✅ Exists |
| `/api/v1/app-items/sync` | POST | `{employeeId, token, entries: [...]}` including `parentItemId` | ✅ Exists |
| `/api/v1/auth/employee-disconnect` | POST | `{employeeId, token}` | ✅ Exists |

### Sync Frequency

| Data Type | Frequency | Batch Size |
|---|---|---|
| Activity logs | Every ~5 min (10 cycles) | Max 100 logs |
| Shell commands | Every ~5 min (10 cycles) | Max 100 commands |
| Permission status | Every ~5 min (10 cycles) | All checks |
| Installed packages | Every ~30 min (60 cycles) | Max 500 packages |
| Cleanup (delete old synced) | Every ~60 min (120 cycles) | All matching |

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
- Default GitHub repo in config: `clickmaster4285/Alpha-AI-Tracker` (overridable via `NEXT_PUBLIC_GITHUB_REPO`)

---

## 9. Production-Readiness Gaps

| Gap | Severity | Details |
|---|---|---|
| **No crash handling** | 🔴 High | Unhandled exceptions crash the app. No global exception handler, no crash dump, no telemetry. `AppDomain.CurrentDomain.UnhandledException` is not subscribed. |
| **No auto-update** | 🔴 High | Employees must manually download and reinstall new versions. No update notification, no silent update. |
| **No tamper resistance** | 🟠 Medium | SQLite database is unencrypted (encryption code is commented out). Config encryption can be bypassed with debugger. Process collection can be stopped by killing the process (though auto-start watchdog re-launches). |
| **No logging beyond console** | 🟠 Medium | Uses `ILogger` but no file sink, no remote logging. Debug builds log to `Trace`. Silent in release. |
| **macOS CPU = 0%** | 🟠 Medium | macOS `ProcessCollector` does not measure CPU. All macOS CPU values are 0. |
| **macOS limited window capture** | 🟢 Low | Only captures foreground window via `osascript`. No EnumWindows equivalent. Background window titles are never captured on macOS. |
| **No storage quota** | 🟢 Low | SQLite grows unbounded until the 30-day cleanup cycle. Could fill disk on a heavily used machine. |
| **No retry backoff** | 🟢 Low | Retries sync every ~5 min regardless of failure count. No exponential backoff. |
| **Single employee per device** | 🟢 Low | Only one employee can be logged in at a time. Logging in as a different employee wipes the previous session. |

---

## 10. Immediate Next Steps

1. **Add tests** — start with unit tests for `SqliteLogStore`, `ProcessFilter`, and `PackageDetector`
2. **Add crash reporting** — subscribe to `AppDomain.CurrentDomain.UnhandledException` and log to a file
3. **Enable SQLite encryption** — uncomment the sqlcipher path in `SqliteLogStore` constructor and switch provider
4. **Fix macOS CPU measurement** — sample `TotalProcessorTime` like Windows/Linux
5. **Fix macOS window title capture** — only captures foreground window currently
6. **Add file logging** — configure `ILogger` with a rolling file sink for production diagnostics
7. **Add offline retry with backoff** — exponential backoff on sync failures to reduce server load
8. **Consider auto-update** — integrate Velopack or Squirrel.Windows for silent updates
9. ~~**Add process ancestry tracking** — persist parent PID chain from `ParentProcessResolver` into `AppItem`~~ ✅ DONE
10. **Browser extension for full URL capture** — window-title parsing is best-effort; MV3 extension + native messaging for production-grade URLs. Currently only page title and a heuristic `title:X` identifier are stored, not actual URLs.
12. ~~**AT-SPI for Wayland window enumeration** — currently only the foreground window is captured via AT-SPI. All-windows enumeration via AT-SPI Registry would fix background Chrome tabs, Nautilus paths, etc.~~ ✅ DONE (File Explorer journey tracking: 3 watchers + EventCoordinator + JourneyEngine)
13. ~~**File manager via `xdg-open` hook or inotify** — watch `~/.local/share/recently-used.xbel` for recently opened files/folders as a supplement to window title parsing.~~ ✅ DONE (RecentFilesWatcher + FileSystemEventWatcher + ATSPIEventWatcher)
