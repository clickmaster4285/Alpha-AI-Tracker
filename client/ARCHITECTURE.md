# Client Architecture — Alpha AI Tracker Desktop App

> **Last audited:** 2026-07-25  
> **Service completion (honest):** ~35%

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
│   │   ├── IInstalledAppDetector.cs    # Installed app detection + permission status
│   │   └── IShellCommandCollector.cs   # Shell command collection + accessibility status
│   ├── Models/
│   │   ├── ActivityLog.cs              # Core data model (14 fields)
│   │   ├── EmployeeInfo.cs             # Employee login info (persisted in SQLite)
│   │   ├── SessionInfo.cs              # Static session ID (generated once per app launch)
│   │   └── ShellCommand.cs             # Shell command model (15 fields)
│   ├── EncryptedConfigService.cs       # AES-256-GCM encryption with transport key + machine-derived key
│   ├── InstalledAppDetector.cs         # Cross-platform installed app detection (desktop files, dpkg, brew, registry)
│   ├── ProcessFilter.cs                # Filters kernel/system processes from collection
│   └── ParentProcessResolver.cs        # Resolves window titles from parent processes (e.g., terminal → shell)
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
│   ├── LogCollectorService.cs           # BackgroundService: collect → filter → store → sync → cleanup cycle (30s loop)
│   ├── BackgroundGuardService.cs        # Watchdog: re-installs auto-start/systemd if removed (60s check)
│   └── AutoStartService.cs              # Platform-specific auto-start: Run key, .desktop, launchd plist
│
├── Storage/
│   ├── DatabaseSchema.cs                # Raw SQL for all table creation (activity_logs, shell_commands, employee_info, etc.)
│   └── SqliteLogStore.cs                # ILogStore implementation using Microsoft.Data.Sqlite
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
|---|---|
| **Singleton** | `AppConfig`, `ILogStore`, `HttpClient`, `IInstalledAppDetector`, `IActivityCollector`, `IShellCommandCollector`, `AutoStartService`, `LogCollectorService` |
| **Transient** | `MainViewModel` |
| **Hosted** | `BackgroundGuardService`, `LogCollectorService` |

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

- **ProcessFilter.cs** — filters out kernel processes (`kthreadd`, `systemd-*`, etc.) and processes from different sessions
- **LogCollectorService** — further filters to only installed applications (via `IInstalledAppDetector`) AND only entries with non-empty window titles
- Shell processes (`bash`, `zsh`, `cmd`, etc.) are always tracked regardless of installed app status

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
| `/api/v1/auth/employee-disconnect` | POST | `{employeeId, token}` | ✅ Exists |

### Sync Frequency

| Data Type | Frequency | Batch Size |
|---|---|---|
| Activity logs | Every ~5 min (10 cycles) | Max 100 logs |
| Shell commands | Every ~5 min (10 cycles) | Max 100 commands |
| Permission status | Every ~5 min (10 cycles) | All checks |
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

1. **Create the server-side shell commands table and sync endpoint** — currently client sends data that's silently lost
2. **Add tests** — start with unit tests for `SqliteLogStore` and `ProcessFilter`, then integration tests for sync flow
3. **Add crash reporting** — subscribe to `AppDomain.CurrentDomain.UnhandledException` and log to a file
4. **Enable SQLite encryption** — uncomment the sqlcipher path in `SqliteLogStore` constructor and switch provider
5. **Fix macOS CPU measurement** — sample `TotalProcessorTime` like Windows/Linux
6. **Add file logging** — configure `ILogger` with a rolling file sink for production diagnostics
7. **Add offline retry with backoff** — exponential backoff on sync failures to reduce server load
8. **Consider auto-update** — integrate Velopack or Squirrel.Windows for silent updates
