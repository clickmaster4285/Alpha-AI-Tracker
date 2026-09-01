# Client Architecture — Alpha AI Tracker Desktop App

> **Last audited:** 2026-08-22
> **Changelog:**
> - 2026-08-22: **Flatpak `bwrap` PPID chain walk in embedded Python probe.**
>   `resolve_app_name(pid)` in `LinuxAtSpiBrowserReader.cs` now detects when the AT-SPI PID belongs to Flatpak's `bwrap` or `xdg-dbus-proxy` and walks up the PPID chain (via `/proc/<pid>/stat`) until it reaches the real app process. The FLATPAK_ID / snap / comm resolution then runs against the real PID, so Floorp/LibreWolf/Waterfox Flatpak installs resolve to their short app ID (`floorp`, `librewolf`, `waterfox`) instead of the proxy name. Verified: `dotnet build` 0/0, 0 warnings.
> - 2026-08-21: **All hardcoded browser names removed — dynamic `IBrowserRegistry` replaces `BrowserProcessHints` / `IsBrowserProcess` / `BROWSER_HINTS` / `ResolveFamily`.**
>   New `IBrowserRegistry` interface + `BrowserRegistry` implementation source browser classification from `installed_applications.is_browser` (set via `.desktop` `Categories=WebBrowser`, Windows registry `URLAssociations`, macOS `CFBundleURLSchemes`) and cache it for 5 minutes. Every reader, tracker, history fallback, session label resolver, and the main collector loop now resolves browsers through the registry instead of hardcoded name lists. The Linux embedded Python probe's `BROWSER_HINTS` tuple is deleted; the probe uses `_browser_exes` (already built from `.desktop` files) for both AT-SPI browser detection and Firefox sessionstore discovery. `StripBrowserSuffix` is now dynamic — it strips ` - {appDisplayName}` using the registry's display name instead of a 10-entry hardcoded suffix list. `BrowserHistoryReader.ResolveFamily()` is removed; URL resolution searches all visits without a family filter (exact-title match is the primary signal). `SessionLabelResolver` accepts the registry as a parameter and runs profile extraction (`--profile-directory`, `-P`, `--profile=`) for ANY browser the registry knows, not just a hardcoded Chromium-family switch. `InstalledAppDetector.ScanStartMenuShortcuts` no longer pre-classifies shortcuts as browsers — the subsequent registry scan sets `IsBrowser` from URL associations. `IBrowserRegistry` is registered unconditionally in DI (outside the `BrowserTrackingEnabled` guard) because `LogCollectorService` and `SessionLabelResolver` consume it regardless of the tracking master switch. Verified: `dotnet build` 0/0, 0 warnings; zero remaining references to `BrowserProcessHints`, `IsBrowserProcess`, `BROWSER_HINTS`, `ResolveFamily`, or `ResolveChromeProfile` in the client.
> - 2026-08-18: **Embedded-webview journeys (VS Code Simple Browser, Electron apps) — structural detection, no hardcoded names.**
>   Readers no longer gate on a browser-process list alone: any window whose a11y tree exposes a
>   DOCUMENT_WEB node (AT-SPI role 95) with an **http(s) DocURL** is tracked (UIA on Windows: a
>   descendant Document/Edit whose Value/Name is an http URL). App chrome excludes itself by URL scheme
>   (`vscode-webview://`, `file://`, `about:`). Metadata `source="webview"` + the host process name let
>   the dashboard show the source app. Linux scans non-browser apps every 5th poll (400-node budget) and
>   caches/re-emits webview windows each poll so sessions never falsely close; `--type=` child processes
>   are skipped structurally. The tracker hydrates browser_tab-rooted sessions, and the main loop skips
>   them too (webview session + host-app session coexist; no duplicate-close).
> - 2026-08-18: **Structural process name resolution — Flatpak/snap browsers get correct names + structural browser detection.**
>   The Python probe's `resolve_app_name(pid)` now walks up the `bwrap`/`xdg-dbus-proxy` PPID chain (via `/proc/<pid>/stat`) to reach the real app process before checking FLATPAK_ID in `/proc/<pid>/environ` (extracts short name: `org.mozilla.Floorp` -> `floorp`) and snap path in `/proc/<pid>/exe` (`/snap/firefox/...` -> `firefox`), falling back to `/proc/comm` for native apps. No name lists: Flatpak/snap are the only sandboxing systems that inject proxy PIDs (e.g. `xdg-dbus-proxy`). Structural browser detection: `.desktop` files with `Categories=WebBrowser` are scanned (cached 5 min) and the resolved process name is matched against browser exe names from the desktop files + Flatpak app IDs. C# `ReadComm()` checks a `_pidNameCache` (populated from probe results each poll) before `/proc/comm`, so WM-only Flatpak/snap windows also get the correct name. `StripBrowserSuffix` gains Floorp/LibreWolf/Waterfox.
> - 2026-08-18: **Focus totals frozen (~0s on the web) fixed — flush is now ADDITIVE + every-minute push.**
>   Root cause: `UpdateAppSessionFocusSql` OVERWROTE the row with the in-memory delta and the counter was
>   then cleared — each flush wrote only the last ~10-cycle window (300s main / 30s browser), never the
>   session total (live evidence: Chrome frozen at exactly 30.0, others at exactly 300.0, identical across
>   65s). Fix: `fg = COALESCE(fg,0) + $fg` — the in-memory dict stays a per-flush DELTA (cleared after
>   every write, both loops), so SQLite holds the true cumulative total, restart-safe, re-sent verbatim
>   (server upsert overwrites). Close paths flush first then close with NULL (`COALESCE($fg, fg)`) — no
>   double-counting. Push cadence: main loop flushes focus every 2 cycles (~60s at the default 30s
>   interval) and `SyncService` failure backoff is capped at 60s, so every `is_synced=0` row reaches the
>   server within a minute even during repeated failures.
> - 2026-08-18: **Foreground/background focus times fixed — Linux/Wayland detection + browser sessions.**
>   (1) `ProcessCollector` Linux AT-SPI foreground detection rewritten: at-spi2-core ≥ 2.50 returns
>   `GetState` as a **packed 64-bit bitmask** (bit N = state N) not a list of ids, and the old check
>   looked for state 8 (ENABLED, present on every window) — the focused window is the only one with
>   bit 1 (STATE_ACTIVE)/bit 12 (STATE_FOCUSED). New `IsAtSpiActiveState` (gdbus path) and
>   `is_active_window` (python path) decode both formats. This also makes the browser reader's
>   per-window `active` flag work (the focused browser window is now identifiable on Wayland).
>   (2) `AccessibilitySnapshot.IsActive` added and set per platform (Linux AT-SPI ACTIVE/FOCUSED,
>   Windows `GetForegroundWindow` HWND, macOS frontmost process); `AccessibilityBrowserTracker` now
>   accumulates the poll interval per open window every poll (active → `foreground_seconds`, rest →
>   `background_seconds`) and flushes every 10 polls + on close via `UpdateAppSessionFocusAsync`
>   (re-queues `is_synced=0` so SyncService re-sends) — browser journeys finally earn focus time.
> - 2026-08-12: **Self-update from GitHub Releases** — new `Services/AppUpdateService.cs`
>   (singleton + hosted, ObservableObject): checks the GitHub latest-release API, picks the platform
>   installer asset (`.deb` by arch / `.exe` / `.dmg`), compares against `AppInfo.Version`, and on a
>   newer version either auto-installs (`ALPHA_UPDATE_AUTO_INSTALL=true` default — Linux
>   `pkexec dpkg -i`, Windows silent Inno which force-closes the app then a detached `.cmd` relaunches
>   it, macOS `open` dmg) or surfaces a GUI banner + top-bar **Check updates** / **Update to vX.Y.Z** /
>   **Restart to apply** buttons. Downloads stream to `~/.local/share/alpha-ai-tracker/updates`
>   (never the install dir). Background loop checks every 30 min, gated by persisted
>   `update_last_check_at` ≥ `ALPHA_UPDATE_AUTO_CHECK_HOURS` (24h). New   `--restart` CLI arg: the
>   single-instance mutex is retried for 8s instead of signal-and-exit (post-update relaunch). Full
>   detail in §18.
> - 2026-08-10: **Employee disconnect removed.** The nav-rail **Disconnect** button, `LogoutCommand`/
>   `LogoutAsync` and `LogCollectorService.StopTracking()` are deleted; the client no longer calls
>   `POST /api/v1/auth/employee-disconnect`. Once `IsProfile` is reached the shell has no in-app
>   logout — tracking runs until the process stops. No `logout` session event is emitted anymore
>   (only `login`, from `StartTracking`), and the Windows anti-sleep state now persists for the whole
>   process lifetime (the OS clears it at process exit — no leak). `ClearEmployeeInfoAsync` remains
>   on `ILogStore` (store primitive).
> - 2026-08-10: **Six-page GUI rewrite + runtime branding pipeline.** `MainWindow.axaml` is now a
>   ~230-line **router**; every screen lives in `Views/Pages/` as its own `UserControl` (Splash, Login,
>   PermissionSetup, Dashboard, SystemSpecs, InstalledApps). Three new page ViewModels
>   (`DashboardViewModel`, `SystemSpecsViewModel`, `InstalledAppsViewModel`) joined `MainViewModel` in DI
>   as Transient. `Styles/AppTheme.xaml` flipped from a dark palette to a **light design-token
>   dictionary** (colors, radii, shadows, vector icon geometries); `App.axaml` carries ~30 style classes
>   and 5 animations. New: `Core/AppInfo.cs` reads `client/APP_IDENTIFIERS` (embedded resource) +
>   `client/VERSION` (informational version) so **every visible brand string and version is derived at
>   runtime** — no product name is baked into XAML or C#. Full UI reference: **[UI_ARCHITECTURE.md](UI_ARCHITECTURE.md)**.
> - 2026-08-07: **Docs re-audit & rewrite.** Re-verified every claim against source. Corrected stale
>   references: `TrayAvailability.cs` and `SingleInstance.cs` do not exist (`SingleInstanceService.cs`
>   does); the tray menu has **Show / Hide only (no Quit item)**; `extensions/`, `native-host.py`,
>   `NativeMessageService`, `BrowserExtensionService` and `install-extensions.sh` are gone from the
>   product; the permission wizard is now **4 steps on Linux / 3 elsewhere** (Auto-Start → Background
>   Guard → [Linux-only] Dependencies → Other Permissions) and always re-validates the real condition.
>   Documented the current 11-table schema, the joint software classifier, the sync ordering
>   (app-sessions BEFORE app-items), the headless `--background` service mode, and the exact env keys.
> - 2026-08-07: **Private/incognito window URLs captured.** (1) `AccessibilityBrowserTracker` rotates on
>   title change after a bounded ~25s `historyLag` timeout even with no URL (was: held forever waiting
>   for history — private windows froze rotation). (2) `LogCollectorService` no longer closes browser
>   sessions owned by the accessibility tracker (browser-owned sessions excluded from the close phase —
>   they were being silently closed 4–40s after opening). (3) Linux URL sources: **Firefox** builds its
>   full AT-SPI tree on demand, so `LinuxAtSpiBrowserReader` reads the `DOCUMENT_WEB` (role 95) node's
>   `DocURL` attribute — the EXACT private-window URL; **Chrome 136+** only builds the tree when launched
>   with `--force-renderer-accessibility` (the tracker ships a user-level `google-chrome.desktop` that
>   carries the flag on every Exec line). Verified end-to-end in the INSTALLED build.
> - 2026-08-07: **Killed the `dpkg-query: no packages found matching ${Version}` startup noise** — the
>   `-f` format string is now double-quoted so `\t`/`\n` escapes reach dpkg-query as ONE argument, and
>   stderr is drained.
> - 2026-08-07: **Quiet terminal** — per-event browser-journey logs demoted to Debug (visible with
>   `ALPHA_LOG_LEVEL=debug`); startup banner stays at Information.
> - 2026-08-07: **Headless `--background` service mode** — `Program.cs` no longer initializes the
>   Avalonia/X11 UI at boot. Manual launches signal that process to create the UI lazily. Linux user
>   units inherit the graphical environment from the systemd user manager instead of hardcoding
>   `DISPLAY` / `XAUTHORITY`; `BackgroundGuardService` removes legacy `~/.Xauthority` overrides and
>   performs one systemd-only restart so GNOME Wayland's rotating Mutter cookie takes effect.
> - 2026-08-06: **Hybrid URL fallback — browser profile History reader** (`BrowserHistoryReader.cs`).
>   Reads Chromium `History` / Firefox `places.sqlite` while the browser runs (safe copy + `-wal`/`-shm`/
>   `-journal` sidecars, read-only, signature-throttled); resolution is STRICTLY title-match; generic
>   profile discovery catches brand-new browsers. Tags `metadata_json.source="history"` vs `"a11y"`.
> - 2026-08-05: **Option B — accessibility-tree browser journey** (the debugger pipeline `Core/Browser/*`
>   — 16 files — was DELETED). New `Core/BrowserAccessibility/` reads the OS accessibility tree (AT-SPI
>   / UIA / AX) — no debugger, no extension, no browser-catalog dependency. `AccessibilityBrowserTracker`
>   polls every `ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS` (3), one `app_sessions` per window +
>   `browser_tab`/`browser_navigation`/`browser_download` `app_items`, idle-close, graceful shutdown,
>   incognito gated by `ALPHA_BROWSER_CAPTURE_INCOGNITO` (default off). Dead code removed: shell-command
>   collectors, `publish/install-extensions.sh`, the `extensions/` bundling step.
> - 2026-08-03: **Single-instance + tray UX; Installer-Parity rule** — second launch restores the running
>   window via `SingleInstanceService` named-pipe signalling; `--background` runs headless for systemd.
>   Added the mandatory **Build-Parity Rule** (§15): `dotnet run` is not a valid release test.
> - 2026-08-01 … 2026-07-27: See prior entries below (kept for history — all still accurate).
>   Notable: GUI-apps-only tracking gate (07-30), cgroup-based session dedup (07-31), software
>   classification pipeline (07-30), file-explorer journey (07-29), atomic cascade-close (07-31),
>   crash-safe session ended_at tracking (07-28), SQLite concurrency gate (07-30), GNOME-daemon
>   contamination fix (07-29).
> **Service completion (honest):** ~85%

---

## 1. Responsibility & Scope

**Owns:**
- Collecting process activity (window titles, CPU%, memory) from employee machines via per-platform collectors
- Capturing the **browser journey** from the OS accessibility tree (exact active-tab URL + title) + profile-history fallback
- Capturing the **file-explorer journey** via an event-driven desktop event bus (AT-SPI + FileSystemWatcher + XBEL)
- Building a **software inventory** (GUI apps in `installed_applications`, CLI tools/runtimes/libraries in `installed_packages`)
- Collecting **system info** (device hardware incl. storage/GPU, network info, session events, permission status)
- Storing everything locally in SQLite before batched sync to the central server
- Managing the employee login lifecycle (JWT in request body) and the post-login **permission wizard**
- Keeping the app alive (auto-start + background-guard watchdog), single-instance signalling, tray, headless systemd mode

**Does NOT own:**
- Any business logic about "productive" vs "unproductive" activity
- User/department/admin management (server + web)
- Data persistence beyond local buffering (server is the source of truth)
- Any web-facing display or analytics
- Shell/terminal **command history** collection — removed; shells themselves are only tracked as *children* of terminals/IDEs

---

## 2. Tech Stack Detail

| Component | Technology | Version |
|---|---|---|
| **Language / TFM** | C# (.NET) | net10.0 |
| **UI Framework** | Avalonia | 12.1.0 |
| **Desktop / Theme / Fonts** | Avalonia.Desktop / Avalonia.Themes.Fluent / Avalonia.Fonts.Inter | 12.1.0 |
| **MVVM Toolkit** | CommunityToolkit.Mvvm | 8.4.2 |
| **SQLite Driver** | Microsoft.Data.Sqlite | 10.0.10 |
| **SQLite Bundle** | SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 |
| **DI/Hosting** | Microsoft.Extensions.Hosting | 10.0.10 |
| **Registry (Windows)** | Microsoft.Win32.Registry | 5.0.0 (windows only) |
| **D-Bus** | Tmds.DBus.Protocol | 0.94.2 |
| **UIA (Windows reader)** | Interop.UIAutomationClient | 10.19041.0 |
| **Diagnostics** | AvaloniaUI.DiagnosticsSupport | 2.2.3 (Debug only) |
| **HTTP Client** | System.Net.Http (built-in) | — |

### Notable Omissions

- **No ORM** — raw parameterized SQL via `SqliteCommand`
- **No test framework** — no test project at all
- **No structured logging beyond ILogger** — console (dev) + `FileLoggerProvider` → `dotnetrunlog.txt`
- **No third-party auto-updater library** — the built-in `AppUpdateService` (2026-08-12, §16) self-updates from GitHub Releases; installers are still published via `release.sh`

---

## 3. Project Structure (verified against source)

```
client/
├── Program.cs                      # Entry point. CLI modes (--encrypt-config, --print-config,
│                                   #   --background, --minimized), mutex + single-instance pipe,
│                                   #   DI container, log/db path resolution
├── App.axaml / App.axaml.cs        # Avalonia app, tray icon (Show/Hide), window-close hides to tray,
│                                   #   single-instance SHOW handling, DI-resolved MainViewModel
├── SingleInstanceService.cs        # (in Core/) named-pipe signalling ("alpha-ai-tracker-activation")
├── app.manifest                    # Windows compatibility manifest
├── ViewLocator.cs                  # ViewModel → View resolution (reflection, simple replacement)
├── client.csproj                   # net10.0, Avalonia 12.1.0, DefaultServerUrl AssemblyMetadata,
│                                   #   VERSION → Version/InformationalVersion (read at evaluation time),
│                                   #   APP_IDENTIFIERS → EmbeddedResource "client.APP_IDENTIFIERS"
├── APP_IDENTIFIERS                 # ⭐ Single source of truth for branding (13 KEY="value" pairs).
│                                   #   Consumed at BUILD time by publish/* and at RUNTIME by Core/AppInfo.cs
├── VERSION                         # ⭐ Single source of truth for the version string (currently 0.2.0)
├── APP_IDENTIFIERS_README.md       # How to re-brand; lists every consumer
├── VERSION_README.md               # How to bump the version
├── appsettings.json                # Logging config (largely unused — level comes from ALPHA_LOG_LEVEL)
├── .env / .env.example             # Dev plaintext config template (REPO key is NOT read by client)
│
├── Configuration/
│   ├── AppConfig.cs                # Reads all ALPHA_* env vars into typed config (see §14)
│   └── EnvLoader.cs                # Multi-source config loading: dev .env vs installed config.enc,
│                                   #   shipped-config propagation, machine-key migration, secure wipe
│
├── Core/
│   ├── Abstractions/
│   │   ├── IActivityCollector.cs   # CollectAsync() → ActivityLog[]
│   │   ├── ILogStore.cs            # ~40 methods: store/unsent/mark-sent × 8 tables + lookups + journey + close paths
│   │   ├── IInstalledAppDetector.cs# GUI app detection + MissingPermissions / grant instructions
│   │   └── IPackageDetector.cs     # CLI tool/runtime/library detection from package managers
│   ├── BrowserAccessibility/       # ⭐ Browser journey (Option B)
│   │   ├── IBrowserRegistry.cs         # interface: IsBrowser / GetDisplayName / GetAllBrowserProcessNames / GetAllBrowserDisplayNames
│   │   ├── BrowserRegistry.cs          # implementation: caches IsBrowser apps from IInstalledAppDetector, refreshes every 5 min
│   │   ├── IAccessibilityBrowserReader.cs   # snapshot contract (Platform, IsAvailable, ReadAsync)
│   │   ├── AccessibilitySnapshot.cs         # windowKey, pid, process, title, Url, UrlSource, incognito, IsActive, IsWebview
│   │   ├── LinuxAtSpiBrowserReader.cs       # ONE embedded python3 probe: AT-SPI + Firefox sessionstore (mozLz4) + WM window list
│   │   ├── WindowsUiaBrowserReader.cs       # UIA via Interop.UIAutomationClient
│   │   ├── MacOsAccessibilityBrowserReader.cs # osascript/System Events (Accessibility grant required)
│   │   ├── AccessibilityBrowserReaderFactory.cs # platform picker
│   │   ├── BrowserHistoryReader.cs          # profile History/places.sqlite URL fallback (copy+WAL, title-match)
│   │   ├── AccessibilityBrowserTracker.cs   # BackgroundService: poll → enrich → sessions/items → idle/vanished close → downloads
│   │   └── BrowserAccessibilityHelpers.cs   # NormalizeUrl, ExtractDomain, StripBrowserSuffix(title, appDisplayName), TitleSuggestsIncognito, StableInt32
│   ├── DesktopEventBus/            # ⭐ File-explorer journey
│   │   ├── IObservableEventSource.cs   # common watcher contract (SourceName, IsActive, EventRaised, Start/Stop)
│   │   ├── RawDesktopEvent.cs          # raw OS-level event from a watcher
│   │   ├── DesktopEvent.cs             # normalized business event (after coordinator)
│   │   ├── DesktopEventValidator.cs    # file-manager detection, ignored-process prefixes, path validity
│   │   ├── EventCoordinator.cs         # dedup (3s) + correlate (500ms) + journey records + normalize
│   │   ├── JourneyEngine.cs            # resolve/create AppSession → file_manager_tab root + fm_* items (9 journey fields)
│   │   └── JourneyRecord.cs            # in-memory journey state
│   ├── Models/                     # ActivityLog, AppSession/AppItem (journey fields), DeviceHardwareInfo,
│   │                               #   InstalledApplication, InstalledPackage, NetworkInfo, StorageDevice,
│   │                               #   HardwareDevice, SessionEvent, EmployeeInfo, SessionInfo (per-launch GUID)
│   ├── AppInfo.cs                  # ⭐ Runtime branding accessor — parses the embedded APP_IDENTIFIERS
│   │                               #   (strict regex, never executed) + reads VERSION from the assembly's
│   │                               #   InformationalVersion. Every brand string in the UI comes from here.
│   ├── SingleInstanceService.cs    # Named-pipe signalling ("alpha-ai-tracker-activation")
│   ├── EncryptedConfigService.cs   # AES-256-GCM: transport key → machine-derived key; fallback .machine-id
│   │                               #   ⚠️ TransportKeySeed / MachineKeyPrefix are KDF seeds — never templatize
│   ├── CgroupResolver.cs           # /proc/<pid>/cgroup → systemd app-*.scope (session dedup key)
│   ├── SessionLabelResolver.cs     # context_label: VS Code workspace folder / Chrome --profile-directory
│   ├── InstalledAppDetector.cs     # GUI apps: .desktop (XDG), registry/Start Menu (Win), .app bundles (mac)
│   ├── PackageDetector.cs          # npm, pip, dpkg/apt, snap, flatpak, brew, macports, choco, winget, scoop
│   ├── SoftwareCategoryResolver.cs # canonical categories from .desktop Categories / bundle id
│   ├── SoftwareClassifier.cs       # joint dedup pipeline — GUI apps win over matching package entries
│   ├── SoftwareIdentityResolver.cs # SHA-256 stable identity for cross-source dedup
│   ├── ExecutableMetadata.cs       # publisher/product strings read off the binary itself (no name lists)
│   ├── CollectionExtensions.cs     # small LINQ-ish helpers shared by the resolvers
│   ├── ProcessFilter.cs            # kernel/system process filtering (names, prefixes, session, window, age)
│   ├── ParentProcessResolver.cs    # PPID tree, window-title resolution, cmdline, browser profile extraction
│   ├── AppProcessClassifier.cs     # category + root item_type resolution, headless --type= filter
│   ├── ActivityContextParser.cs    # URL/path/title parsing, dynamic browser-suffix stripping
│   └── SessionHierarchyResolver.cs # PID-tree parent linking (node → terminal → IDE)
│
├── Platform/
│   ├── Windows/ProcessCollector.cs # User32 EnumWindows (all titles), GetForegroundWindow, CPU 2-sample, PS cmdline
│   ├── MacOS/ProcessCollector.cs   # osascript foreground only. ⚠️ No CPU measurement
│   └── Linux/ProcessCollector.cs   # multi-strategy foreground + GNOME Shell Introspect / xprop window lists
│
├── Services/
│   ├── LogCollectorService.cs      # ⭐ main BackgroundService: collect → resolve → sessions/items → heartbeat (no network I/O since 2026-08-11)
│   ├── SyncService.cs              # ⭐ dedicated sync engine: drains unsent rows in byte-bounded chunks (gzip, polite pauses, exponential backoff)
│   ├── AppUpdateService.cs         # ⭐ self-updater (2026-08-12): GitHub latest-release check → platform asset → download to user data dir → pkexec dpkg / silent Inno / dmg; ObservableObject state bound by the GUI; 24h auto-check loop
│   ├── DesktopEventService.cs      # ⭐ orchestrates file-explorer watchers → coordinator → JourneyEngine
│   ├── BackgroundGuardService.cs   # watchdog: re-installs auto-start/systemd unit if removed (60s)
│   ├── AutoStartService.cs         # Run key / ~/.config/autostart .desktop / launchd plist
│   ├── FileLoggerProvider.cs       # dotnetrunlog.txt (defensive path fallback, null sink last)
│   └── Watchers/
│       ├── ATSPIEventWatcher.cs    # Tmds.DBus.Protocol → AT-SPI focus:/window: events + /proc/cwd path
│       ├── FileSystemEventWatcher.cs # FileSystemWatcher on 6 user dirs (exclusion list, debounce)
│       └── RecentFilesWatcher.cs   # XBEL monitor (~/.local/share/recently-used.xbel)
│
├── Storage/
│   ├── DatabaseSchema.cs           # CreateTableSql (11 tables) + MigrateSql (idempotent ALTERs + dedup) + insert SQL
│   └── SqliteLogStore.cs           # ILogStore impl: SemaphoreSlim(1,1) gate, PRAGMA busy_timeout, atomic cascade-close
│
├── ViewModels/                     # ⭐ One VM per concern — see UI_ARCHITECTURE.md
│   ├── ViewModelBase.cs            # ObservableObject base
│   ├── MainViewModel.cs            # Shell VM (~1150 lines): splash sequence, login, permission
│   │                               #   wizard, AppPage router state, branding passthrough to AppInfo
│   ├── DashboardViewModel.cs       # Page 4 — identity + status tiles, pipeline health, attached devices
│   ├── SystemSpecsViewModel.cs     # Page 5 — machine/compute/network, storage + peripherals
│   └── InstalledAppsViewModel.cs   # Page 6 — apps + packages inventory, search, InventoryRow projection
│
├── Views/
│   ├── MainWindow.axaml(.cs)       # ⭐ ROUTER ONLY — 4 top-level states + nav rail; splash on Opened,
│   │                               #   90%-of-working-area sizing, close-hides-to-tray
│   └── Pages/                      # One UserControl per screen (.axaml + .axaml.cs each)
│       ├── SplashPage              # 1 — boot checklist + determinate progress
│       ├── LoginPage               # 2 — artwork panel + credential card
│       ├── PermissionSetupPage     # 3 — stepper (4 steps Linux / 3 elsewhere)
│       ├── DashboardPage           # 4 — operational overview
│       ├── SystemSpecsPage         # 5 — system specifications
│       └── InstalledAppsPage       # 6 — installed applications & packages
│
├── Converters/                     # BoolInvert, StringNotEmpty, LoadingToText, PercentToGridLength
├── Styles/AppTheme.xaml            # ⭐ Light design-token dictionary: palette + rail tokens + badge
│                                   #   surfaces + 4 shadows + 5 radii + 9 StreamGeometry icons + Inter fonts.
│                                   #   No product name here — branding is resolved at runtime.
├── App.axaml                       # ~30 style classes (card/nav/badge/table/h1/label/ghost…) + 5 animations
├── Assets/
│   ├── avalonia-logo.ico, icon.png
│   └── backgrounds/                # dashboard-hero.png, login-hero.png, specs-hero.png, apps-hero.png
│                                   #   (covered by the existing <AvaloniaResource Include="Assets\**" />
│                                   #    glob → compiled into client.dll, so installer parity is automatic)
├── installers/                     # Built artifacts, version-stamped from VERSION
└── publish/                        # build-installer.sh, encrypt-config.sh, firefox-a11y-apparmor.sh,
                                    #   release.sh, build-deb.sh, build-dmg.sh, generate-windows-vars.sh,
                                    #   installer-windows.iss, linux/, macos/, windows/
```

> **Adding a screen, a style class, or a design token?** Read
> **[UI_ARCHITECTURE.md](UI_ARCHITECTURE.md)** — it documents the token set, every style class, the
> router contract, the binding patterns, and the branding pipeline.

---

## 4. MVVM Layering & DI

### Layers

```
Views (XAML)          ──►  ViewModels                ──►  Models (plain DTOs)      ──►  Services / Infrastructure
  MainWindow (router)        MainViewModel                  ActivityLog, AppSession,      LogCollectorService
  Pages/SplashPage           ├─ splash / login / wizard     AppItem, DeviceHardwareInfo,  DesktopEventService
  Pages/LoginPage            ├─ AppPage router state        InstalledApplication,         AccessibilityBrowserTracker
  Pages/PermissionSetupPage  └─ branding → Core/AppInfo     InstalledPackage, NetworkInfo, SqliteLogStore
  Pages/DashboardPage        DashboardViewModel             SessionEvent, StorageDevice,  platform collectors
  Pages/SystemSpecsPage      SystemSpecsViewModel           HardwareDevice                HardwareDeviceWatcherService
  Pages/InstalledAppsPage    InstalledAppsViewModel                                       SqliteLogStore (+ Rescan → LogCollectorService)
```

`MainViewModel` owns the three page VMs as properties (`Dashboard`, `SystemSpecs`, `InstalledApps`) and
re-points each hosted page's `DataContext` to its own VM. Because of that re-pointing, a page that needs
shell state binds through the window:
`{Binding $parent[Window].((vm:MainViewModel)DataContext).IsDashboardPage}`.

### DI Container (`Program.cs`, `Host.CreateApplicationBuilder`)

| Lifetime | Services |
|---|---|
| **Singleton** | `AppConfig`, `ILogStore` (SqliteLogStore), `HttpClient` (30s timeout), `IInstalledAppDetector`, `IPackageDetector`, `IActivityCollector` (per-platform), `AutoStartService`, `LogCollectorService`, `EventCoordinator`, `JourneyEngine`, `ATSPIEventWatcher`, `WindowsExplorerWatcher`, `IExplorerWindowProvider`, `FileSystemEventWatcher`, `RecentFilesWatcher`, `AppUpdateService`, `IBrowserRegistry` (unconditional — consumed by `LogCollectorService` + `SessionLabelResolver` regardless of `ALPHA_BROWSER_TRACKING_ENABLED`) |
| **Singleton (conditional)** | `IAccessibilityBrowserReader` (platform reader) + `BrowserHistoryReader` — only when `ALPHA_BROWSER_TRACKING_ENABLED` |
| **Hosted** | `BackgroundGuardService`, `LogCollectorService`, `DesktopEventService`, `AccessibilityBrowserTracker` (conditional), `HardwareDeviceWatcherService`, `AppUpdateService` |
| **Transient** | `DashboardViewModel`, `SystemSpecsViewModel`, `InstalledAppsViewModel`, `MainViewModel` |

`App.ServiceProvider = host.Services` is set after `StartAsync`; `App.axaml.cs` resolves `MainViewModel`
from DI (which pulls the three page VMs in through its constructor).

`Core/AppInfo` is deliberately **static, not injected** — branding is process-wide immutable data read
from an embedded resource, and it is needed from places with no container (XAML resources, tray setup,
log banners).

### CLI modes & process model

- `--encrypt-config [input] [output]` — build-time `.env` → `config.enc` (transport key)
- `--print-config` — prints the config an INSTALLED build would resolve (diagnostic)
- `--background` — headless: hosts the services with **no Avalonia/X11 UI** (systemd unit). Skips GUI init so a stale `XAUTHORITY` can never crash startup.
- `--minimized` — start hidden to tray (auto-start launches).
- **Single instance:** global mutex `AlphaAITracker`. A second launch that cannot acquire the mutex sends `SHOW` over the named pipe (`alpha-ai-tracker-activation`) so the running instance raises its window — but only for *user* launches (not `--background`/`--minimized`).
- Windows: `SetThreadExecutionState(ES_SYSTEM_REQUIRED)` while tracking prevents sleep.

---

## 5. Local Data Model (SQLite)

**11 tables** (defined in `DatabaseSchema.CreateTableSql`, `IF NOT EXISTS`):

`device_hardware_info` · `storage_devices` (relational child of hardware) · `installed_applications` (GUI apps) · `installed_packages` (CLI tools/runtimes/libraries) · `network_info` · `session_events` · `app_sessions` · `app_items` (generic self-referencing child) · `app_status` (key-value, e.g. `last_heartbeat_at`, `perm_*`) · `permission_status` · `employee_info` (login + JWT token)

### Key columns

**`app_sessions`** — process_name, app_display_name (real app name), started_at/ended_at, machine_id, employee_id/name, session_id (per-launch GUID), platform, `installed_app_id`/`installed_package_id` FKs, `process_id`, `parent_process_id`, and (added via MigrateSql ALTERs) `grouped_by` ('cgroup'|'pid'), `cgroup_scope` (raw systemd scope, needed for boot hydration), `context_label` (VS Code workspace / Chrome profile).

**`app_items`** — self-referencing via `parent_item_id` (session → tab → navigation / terminal → child). Core columns: `item_type` ('tab','browser_tab','browser_navigation','browser_download','file_manager_tab','fm_*','terminal','folder','file','process','window'), `title`, `identifier`, `url`, `domain`, `opened_at`/`closed_at`, `process_id`. Journey fields (coexist with item_type): `object_type`, `action`, `journey_id`, `sequence`, `previous_path`, `current_path`, `window_id`, `tab_id`, `metadata_json`.

**Upserts / dedup:**

| Table | Conflict key | Behavior |
|---|---|---|
| `installed_applications` | `ON CONFLICT(app_name)` | Updates metadata; **resets `is_synced = 0`** so re-detection re-syncs (drives server `last_seen_at`) |
| `installed_packages` | `ON CONFLICT(package_name, source_manager)` | Update version/category; dedup window-fn + `CREATE UNIQUE INDEX idx_installed_packages_fingerprint` in MigrateSql (kills duplicate rows) |
| `app_sessions` | `ON CONFLICT(id)` | Merge `ended_at` / `parent_process_id` |
| `app_items` | `ON CONFLICT(id)` | Merge title/identifier/url/domain/parent/closed_at/journey fields |

### Migration strategy

`DatabaseSchema.MigrateSql` + `SqliteLogStore.InitializeAsync`: base schema via `IF NOT EXISTS` on every start, then a batch of **idempotent `ALTER TABLE ... ADD COLUMN`** statements (caught via "duplicate column" SqliteException), the installed_packages dedup block, and a few `CREATE INDEX IF NOT EXISTS`. There is no numbered version table.

### Concurrency

`SqliteLogStore` guards the single shared connection with `SemaphoreSlim(1,1)`; `PRAGMA busy_timeout = 5000`; `GatedTransaction` releases the gate on dispose; private ungated helpers (`SetStatusCoreAsync`, `GetEmployeeInfoCoreAsync`) avoid re-entrancy deadlocks. `CloseSessionsAndAppItemsAsync()` closes sessions + their open items in ONE transaction (crash-safe cascade — no orphaned open items).

---

## 6. Lifecycle & Startup Sequence

1. `Program.cs` handles CLI modes, acquires the mutex, starts the single-instance pipe server, builds DI, `host.StartAsync`.
2. **`LogCollectorService`** (hosted):
   - `InitializeAsync` (schema), `RefreshEmployeeInfo` (restores login from SQLite),
   - **Headless session restore (2026-08-18):** if persisted employee credentials exist, `StartTracking()` runs here at boot — so `--background` mode (no GUI) tracks app sessions from power-on, not just browser journeys. Previously this restore happened ONLY in the Avalonia GUI (`MainViewModel.InitializeAsync`), so a headless boot spun on "waiting for login" forever while the browser tracker (which restores the login itself) kept working — the dashboard showed browser activity but zero app sessions.
   - **`ReconcileStaleSessionsOnBootAsync`** — stale `last_heartbeat_at` (>60s) → close orphaned sessions + items with the heartbeat time as approximate crash time (handles poweroff/crash/fast-restart).
   - **`CleanupGarbageSessionRowsAsync`** — closes old `--type=` / long process-name rows.
   - **`CleanupNonGuiAppEntriesAsync`** — removes pre-GUI-gate non-GUI entries (`sh`, `snap`) from `installed_applications` and closes their sessions.
   - Immediate hardware + network collection, then the main loop.
3. **`DesktopEventService`** starts the file-explorer watchers after DB init.
4. **`AccessibilityBrowserTracker`** starts polling browser windows (independent of login, no catalog dependency).
5. **`BackgroundGuardService`** watches auto-start / systemd unit (60s loop) and re-installs if removed — it never *creates* them on its own.
6. **Login (from UI, first-time identity only)** → `MainViewModel.LoginAsync` → `_logCollector.SetEmployeeInfo(...)` + `StartTracking()` (idempotent — already running on a restored session) → permission wizard. After the first login the GUI is **login-only**: opening or closing it never starts or stops the tracking services (the background process owns tracking; window-close hides to tray; only an explicit quit exits the process).

---

## 7. Login & Permission Wizard

### Login

- **Login:** `POST {serverUrl}/api/v1/auth/employee-login` with `{employeeId, secretKey}` → `{employee, token}`. Persists to `employee_info` (SQLite) and feeds `LogCollectorService.SetEmployeeInfo(employeeId, name, token)` + `StartTracking()` (which also records a `login` session event and arms Windows anti-sleep). **Instant sync on login (2026-08-11):** `LoginAsync` then calls `SyncService.RequestImmediateSync()` — the dedicated sync engine's inter-pass wait is a `SemaphoreSlim` that the request releases, so a full drain pass starts right away instead of waiting out the idle interval. The employee record itself is already server-side (it IS the login response); the instant pass pushes everything else — device_hardware_info, installed apps/packages, network, storage_devices, hardware_devices, session_events, permission_status, app_status, sessions/items.
- **Session restore:** at boot, `LogCollectorService` reads `employee_info` and **starts tracking headlessly** (2026-08-18) — no GUI required. When the GUI opens, `MainViewModel.InitializeAsync` re-authenticates the collector (idempotent), **fires `RequestImmediateSync()`** (so rows buffered since the last run land immediately), resets stored `perm_*` statuses, and re-evaluates the permission steps from scratch.
- **No logout:** the employee-disconnect flow (button, `LogoutCommand`, `StopTracking()`, `POST /api/v1/auth/employee-disconnect`) was **removed** 2026-08-10. Once logged in the client tracks until the process stops.

### Permission wizard (`GetNextPermissionStep`)

Always **re-validates the actual condition** — it never trusts stored statuses. Returns the first incomplete step:

| # | Step | Linux check | Grant action |
|---|---|---|---|
| 1 | **Auto-Start** | `~/.config/autostart/alpha-ai-tracker.desktop` exists | `AutoStartService.EnableAutoStartForced()` (+ systemd unit enable) |
| 2 | **Background Guard** | `~/.config/systemd/user/alpha-ai-tracker.service` exists | `EnableAutoStartForced()` (installs the unit) |
| 3 | **Dependencies** (Linux only) | `pkexec`, `loginctl`, `gsettings`, `setcap` present | `pkexec apt-get install -y policykit-1 systemd libglib2.0-bin libcap2-bin` (or manual sudo command) |
| 4 | **Other Permissions** | Linux: Wayland `toolkit-accessibility` via gsettings; Windows: process enumeration; macOS: osascript Accessibility grant | Linux: `pkexec` bash script (chmod +r shell-history files; enable toolkit accessibility as `$USER`; `setcap CAP_DAC_READ_SEARCH+ep` on the installed binary — skipped for `dotnet run`); Windows: UAC `runas`; macOS: instructions (Accessibility / Full Disk Access / Screen Recording) |

`permission_status` results (`perm_auto_start`, `perm_background`, `perm_other`) are stored in `app_status` for reference. Linux dependency installs also surface a manual `sudo apt-get install -y …` command via `ShowManualInstall`.

### UI

`MainWindow.axaml` is a **router**, not a screen. It picks exactly one of four mutually exclusive
top-level states from `MainViewModel`, in this order:

| Guard | Shows |
|---|---|
| `IsSplashVisible` | **Page 1** `SplashPage` — boot checklist + determinate progress, played once on window open |
| `!IsLoggedIn` | **Page 2** `LoginPage` — Employee ID + Secret Key (secret masked; it is a long-lived credential) |
| `RequiresPermissionAction` | **Page 3** `PermissionSetupPage` — the wizard above, one card per step |
| `IsProfile` | **Pages 4–6** behind the nav rail |

`IsProfile => IsLoggedIn && CurrentPermissionStep == PermissionStep.None`, so the shell only appears
once the wizard reports nothing left to grant.

The shell is a 246px nav rail (brand mark, three nav buttons, signed-in employee, version) plus a
content column with a top bar (`ActivePageTitle` / `ActivePageSubtitle`, environment badge, refresh)
hosting pages 4–6. Navigation is `NavigateCommand` with a string parameter (`dashboard` / `specs` /
`apps`) which sets `ActivePage` and awaits `RefreshActivePageAsync()`.

Theming is the **light** token dictionary in `Styles/AppTheme.xaml` plus the style classes in
`App.axaml`. Window close hides to tray (`ShutdownMode.OnExplicitShutdown`). Full detail:
**[UI_ARCHITECTURE.md](UI_ARCHITECTURE.md)**.

---

## 8. Process Collection Pipeline

### The 30-second cycle (`LogCollectorService.ExecuteAsync`)

```
┌─────────────────┐   ┌────────────────────────────────────┐   ┌─────────────────────────┐
│ IActivityCollect │──▶│ Per-process resolution             │──▶│ SQLite                  │
│ .CollectAsync()  │   │  ProcessFilter → headless filter   │   │  StoreAppSessionsAsync  │
│  (platform)      │   │  NonAppProcesses/prefixes          │   │  StoreAppItemsAsync     │
└─────────────────┘   │  ResolveAppInfo (known/fuzzy/auto)  │   │  CloseSessionsAnd…      │
        │             │  CgroupResolver scope               │   └─────────────────────────┘
        │ every 30s   │  BuildSessionKey (scope-aware)      │
        ▼             │  SessionHierarchyResolver parent    │
   process snapshot   └────────────────────────────────────┘
```

- Waits (5s idle poll) until `StartTracking()` after login.
- **Every cycle:** collect → build process tree → hydrate open sessions from DB (**excluding browser-owned sessions** — those belong to the accessibility tracker and must not be closed or duplicated) → resolve each process → sort by priority (IDE → browser → file manager → terminal → shell) → compute `currentKeys` → close vanished sessions atomically → store new sessions + root items + context updates → heartbeat.
- **Context updates** (`UpdateActivityContextAsync`): refreshes root title/identifier and appends `browser_navigation`/`folder`/`file` children with a 30s per-(session,type,identifier) cooldown.

### Platform collectors

| Platform | Window titles | Foreground | CPU | Cmdline |
|---|---|---|---|---|
| **Linux** | GNOME Shell `Introspect.GetWindows` (Wayland, ALL windows) + `xprop _NET_CLIENT_LIST` (X11/XWayland) | xprop → AT-SPI (python3 / gdbus, **decodes the packed 64-bit state bitmask, ACTIVE/FOCUSED bits**) → xdg portal → GNOME Shell → xdotool → heuristic | `TotalProcessorTime` two-sample 100ms gap | `/proc/<pid>/cmdline` |
| **Windows** | `EnumWindows` (all visible windows → titles) | `GetForegroundWindow` | same | PowerShell `Get-CimInstance Win32_Process` |
| **macOS** | **foreground only** via osascript | osascript/System Events | **0 (not measured)** | `ps -o command=` |

All platforms filter **headless Chromium/Electron subprocesses** (`--type=renderer|gpu-process|utility|zygote|broker|crashpad-handler`, Firefox `--contentproc`) via cmdline.

### Filtering & resolution (`ResolveAppInfo`)

1. Headless subprocesses without a window title → skipped.
2. `NonAppProcesses` (exact: GNOME daemons, gvfsd-*, gsd-*, shells `sh/bash/zsh/…`, `waydroid`, `snapd`, …) + `NonAppProcessPrefixes` → skipped.
3. **Known binary names** cache (refreshed ≤1 min from `installed_applications`): exact match, then re-verify GUI (non-browser rows need non-empty Categories).
4. **Fuzzy match** (`GetInstalledAppByBinaryNameFuzzyAsync`, process name >3 chars): `binary_name LIKE` with corrected AND/OR precedence; prefers `is_browser` rows + shortest binary name. Also rejects non-GUI rows.
5. **Auto-detect GUI app** (`IsGuiApplication` → `AutoDetectInstalledGuiApp`): executable in standard paths (Linux `/usr/bin`, `/opt`, `/snap/bin`, flatpak, `/home/*`, `/media/*`; Windows Program Files/WindowsApps; macOS `.app`/`/Applications`) or a matching `.desktop` `Exec=` → registers into `installed_applications`.
6. **Anything else is skipped** — CLI tools, shells, build tools, runtimes, and daemons never become sessions/items.
7. **Browsers are skipped when `ALPHA_BROWSER_TRACKING_ENABLED`** (owned by the accessibility tracker; `IBrowserRegistry.IsBrowser()` covers the post-DB-wipe window before the catalog scan marks `is_browser=1`).

### Session identity & hierarchy

- **`BuildSessionKey`**: scoped processes → `scope|{scope}|{installedAppId}|{machine}|{session}` (collapses all subprocesses of one logical window); unscoped → `{pid}|{machine}|{session}`.
- **`CgroupResolver`** reads `/proc/<pid>/cgroup` for `app-*.scope` (systemd transient scope per `.desktop` launch) — VS Code's ~11 PIDs become ONE session; two windows get different scopes.
- **`SessionHierarchyResolver`** walks the PPID chain (through shells, terminals, build tools, runtimes, IDE subprocesses) and links sessions under their parent via `parent_item_id`. Duplicate-PID open sessions (browser tracker + main loop overlap) are deduped keeping the earliest per PID.
- **`SessionLabelResolver`** derives `context_label` (VS Code workspace folder from argv, browser `--profile-directory`/`-P`/`--profile=` for any browser the `IBrowserRegistry` knows).
- **Close:** `CloseSessionsAndAppItemsAsync` closes sessions + their open items in ONE transaction; crash recovery reuses it at boot.

### Root item type (`AppProcessClassifier.ResolveRootItemType`)

`isBrowser → browser_tab` · `.desktop Categories`/desktopId → `folder` (file manager) / `tab` (IDE/app) / `process` (runtime) · fallbacks: file manager → `folder`, shell → `terminal`, appId → `tab`, pkgId/runtime/build-tool → `process`, window title → `tab`, else `process`.

---

## 9. System Info Collection

### Device hardware — every 30 cycles (~15 min)

Fingerprint-deduped (`mac|hostname|os|cpu|ram|gpu`): hostname, `RuntimeInformation.OSDescription`, CPU model (`/proc/cpuinfo` / `PROCESSOR_IDENTIFIER`), cores, total RAM (`/proc/meminfo` / Windows fallback constant), **MAC** (first up non-loopback interface), **storage devices** (Linux `lsblk -J` with SSD/HDD via `ROTA`; Windows `DriveInfo`), **GPU** (`/proc/driver/nvidia/version`, else `lspci`). Stored as `device_hardware_info` + relational `storage_devices` rows (backfilled if the hardware fingerprint is unchanged but storage is empty).

### Hardware device hotplug tracking — real-time (Linux only)

`HardwareDeviceWatcherService` tracks physical device plug/unplug events in real-time:
- **USB devices** via `udevadm monitor` (subsystem=usb) + periodic `/sys/bus/usb/devices/` enumeration
- **Analog audio jacks** via `amixer` polling for headphone/headset jack state changes (ALSA)
- Stores events in `hardware_devices` table with `device_class` (`usb`/`audio`/`headphone`/`storage`/`input`/`display`/`other`)
- Plug events create open rows; unplug events set `unplugged_at`
- USB storage devices detected via `/sys/class/block` entries with `/usb` in their bus path
- Audio jack detection monitors `Headphone` and `Headset Mic` mixer controls for `[on]`/`[off]` state changes
- Deduped by `bus_path` to avoid duplicate rows for the same physical slot

### Network info — every 10 cycles (~5 min)

Public IP from `api.ipify.org` → `icanhazip.com` → `checkip.amazonaws.com` (first success), private IPs from `Dns.GetHostEntry` + per up-interface unicast addresses. Deduped by IP change (only new-IP rows are stored).

### Session events

`login` rows with OS username + timestamp.

### Permission status — every 10 cycles

Per-platform `GetPermissionStatus()` (Linux: xprop/xdotool/gdbus/python3/portal/introspect/atspi/X11 availability; Windows: user32 probes; macOS: osascript + Accessibility grant) → `permission_status` table.

### Scan cadence (30s cycle counters)

| Data | Frequency |
|---|---|
| Device hardware | Every 30 cycles (~15 min) |
| Installed apps | Every 30 cycles (~15 min) |
| Installed packages | Every 60 cycles (~30 min, delegates to joint scan) |
| Network info | Every 10 cycles (~5 min) |
| Permission status | Every 10 cycles (~5 min) |
| Heartbeat `last_heartbeat_at` | Every cycle |
| **Focus flush** (open-session `foreground/background_seconds`, additive) | **Every 2 cycles (~1 min)** — was every 10 (~5 min); re-queues `is_synced=0` so the server learns growing totals within a minute (user rule 2026-08-18) |

> **Sync moved out of this loop (2026-08-11)** — the dedicated `SyncService` background loop (§13) drains unsent rows on its own schedule; the collection loop never performs network I/O.

---

## 10. Installed Apps & Packages Inventory

**Two detectors + one classifier:**

1. **`InstalledAppDetector`** (GUI apps → `installed_applications`):
   - **Linux:** `.desktop` files from `$XDG_DATA_HOME/applications`, `~/.local/share/applications`, every `$XDG_DATA_DIRS` entry + `/applications`, plus baseline `/usr/share` & `/usr/local/share` (covers **snap** `/var/lib/snapd/desktop/applications` and **flatpak** exports). `NoDisplay=true` / non-`Type=Application` skipped. Browser detection via `Categories=WebBrowser` or `MimeType x-scheme-handler/http(s)`. `desktop_id` = `.desktop` filename, `categories` stored raw.
   - **Windows:** Start Menu `.lnk`s, Program Files (x86) dirs, registry `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` (DisplayName, version, publisher, path, uninstall string); browser via `URLAssociations` http/https.
   - **macOS:** `/Applications` + `~/Applications` `.app` bundles; `Info.plist` → `CFBundleIdentifier` (identity) + `CFBundleURLSchemes` http+https (browser).
2. **`PackageDetector`** (CLI tools/runtimes/libraries → `installed_packages`): npm global (`npm list -g --json`), pip (`pip list --format=json`), dpkg/apt (`dpkg-query -W -f='…'` — quoted format fix), snap (skips snaps with desktop entries), flatpak (skips apps with desktop entries), brew, macports, choco, winget, scoop, plus a built-in `CliKnownPackages` list. Categories: `runtime`/`tool`/`library`/`system`.
3. **`SoftwareClassifier.Classify(rawApps, rawPackages)`** — the joint dedup: generates a **SHA-256 identity** per entry (`SoftwareIdentityResolver`: Linux desktop_id, macOS bundle id, Windows uninstall-key, + normalized install path), **GUI apps win** (a matching package entry is dropped — fixes Firefox-snap appearing as a package), assigns canonical categories (`SoftwareCategoryResolver`: WebBrowser/FileManager/IDE/Development/TerminalEmulator/…), and routes to exactly one table.

Both detectors are forced-rechecked every scan (`ForceRecheck`) and upsert into SQLite (re-detect resets `is_synced=0`).

---

## 11. Browser Journey (Option B — accessibility tree + history fallback)

`AccessibilityBrowserTracker` (BackgroundService, gated on `ALPHA_BROWSER_TRACKING_ENABLED`) captures the REAL browser journey — **no debugger, no extension, no catalog dependency** (works for install→use→uninstall-in-5-min browsers). Browser detection is dynamic via `IBrowserRegistry` (sourced from `installed_applications.is_browser`, refreshed every 5 min) — any browser with a `.desktop` `Categories=WebBrowser` / Windows `URLAssociations` http+https / macOS `CFBundleURLSchemes` http+https is automatically detected without hardcoded name lists.

### Poll loop (every `ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS`, default 3s)

1. `IAccessibilityBrowserReader.ReadAsync()` → `AccessibilitySnapshot[]` (windowKey, pid, process, title, URL, `UrlSource`, incognito, **`IsActive`** — AT-SPI ACTIVE/FOCUSED state on Linux, `GetForegroundWindow` HWND match on Windows, frontmost process on macOS).
2. `EnrichUrlsFromHistoryAsync` fills empty URLs from the **profile-history fallback** (preserves `IsActive`).
3. **Focus accounting** — each poll the ACTIVE window's session earns the poll interval as `foreground_seconds` and every other open window earns `background_seconds`; totals flush every 10 polls + on close via `UpdateAppSessionFocusAsync` (`is_synced=0` re-syncs). The flush is **additive** (delta in memory, cleared after each write — the SQLite row is the true cumulative total), so long-running browser sessions show growing foreground/background on the dashboard, not just the last flush window. Browser journeys finally earn focus time (they are owned here, not by the main loop's focus accounting).
4. Per window: first sight → **new `app_sessions`** + `browser_tab` root item (URL, domain, journeyId, `metadata_json`); changes → rotate/record; vanished (5-miss grace) → close; idle (`ALPHA_BROWSER_JOURNEY_IDLE_MINUTES`, 15) → close; shutdown → close all.

### Reading the URL — three merged sources (Linux embedded python3 probe)

- **A) AT-SPI tree** — for browsers that expose it: the omnibox `ENTRY`/`EDITBAR` node **"Address and search bar"** gives the exact URL/query; **Firefox (incl. private windows)** additionally exposes the exact page URL via the `DOCUMENT_WEB` (role 95) node's **`DocURL`** attribute (Firefox builds its tree on demand). Chrome 136+ only builds the tree with `--force-renderer-accessibility` (the installed user-level `google-chrome.desktop` carries the flag).
- **B) Firefox sessionstore** (`recovery.jsonlz4` / `sessionstore.jsonlz4`, decompressed by an embedded pure-python **LZ4 block decoder**) — survives snap Firefox's AppArmor D-Bus block; exact per-tab URL + active-tab index.
- **C) WM window list** (GNOME Shell `Introspect.GetWindows`, else `xprop _NET_CLIENT_LIST`) — stable per-window ids (`wm:<id>`) and X11-only browsers AT-SPI cannot see.

**Windows** = UIA (`Interop.UIAutomationClient`) address-bar `ValuePattern`. **macOS** = osascript/System Events (best-effort, Accessibility grant required).

### Hybrid URL fallback (`BrowserHistoryReader`)

When the a11y tree cannot expose the omnibox (Linux Chrome 136+ without the flag, snap Firefox), reads the browser's **own profile database while it runs**: Chromium-family `History` (visit_time = microseconds since 1601) and Firefox `places.sqlite` (visit_date = µs since 1970). DB + `-wal`/`-shm`/`-journal` sidecars are copied to a temp dir and opened read-only; change signatures throttle re-reads (`ALPHA_BROWSER_HISTORY_POLL_SECONDS`, 10s); resolution is **strictly title-match** (no newest-visit fallback → no cross-window misattribution); generic scans of `~/.config`, `~/.mozilla`, snap-firefox, Windows `LocalAppData`, macOS `Library/Application Support` discover brand-new profiles. Incognito visits are never written to these DBs, so they stay out automatically.

### Rotation semantics (per-page records)

- Each page visit gets its OWN `browser_tab` root — the previous root is **closed** via `CloseAppItemAsync` (resets `is_synced=0` so the server learns the close) and a fresh root is opened.
- A real URL change rotates immediately; a title-only change rotates only after ~10s stability (`MinTabRotationInterval`, filters badge/timer flicker).
- **Bounded history-lag:** if the URL is missing after a title change (private windows / `about:`/`file:` pages never write history), the tab rotates on title after ~25s (`HistoryLagTimeout`) — never frozen.
- URL changes also write a `browser_navigation` child (`previous_path` → `current_path`, `sequence` via `GetNextSequenceAsync`).
- **Downloads** in `~/Downloads` (watched via `FileSystemWatcher`, skipping `.crdownload`/`.part`/`.tmp`) are appended to the most recent browser session as `browser_download` items.
- **Window identity** (`ResolveWindowKey`): re-keys by page-title match (multi-window Chrome/Edge/Firefox share one PID) or a single-window count guard — one window can never steal another's session.
- **Incognito:** windows are detected (title/cmdline) and flagged in `metadata_json` (`"incognito":true`); their URL is stored only when `ALPHA_BROWSER_CAPTURE_INCOGNITO=true` (default off — legal-safe).
- `metadata_json` = `{source: accessibility|history|sessionstore|downloads-watcher, windowKey, incognito, processName, capturedAt}`.

---

## 12. File Explorer Journey (Desktop Event Bus)

An event-driven pipeline (introduced 2026-07-29) for file-manager operations (Nautilus, Dolphin, Thunar, Nemo, Caja, PCManFM, Konqueror, …):

```
Watchers (IObservableEventSource)          EventCoordinator                 JourneyEngine
  ATSPIEventWatcher     ──raw──►  dedup (3s) / correlate (500ms)  ──normalized──►  resolve AppSession
  FileSystemEventWatcher  (RawDesktopEvent)  journey records (15-min        create file_manager_tab root
  RecentFilesWatcher            timeout, max 500)                           + fm_* items (9 journey fields)
```

- **`ATSPIEventWatcher`** (Linux; `Tmds.DBus.Protocol`): registers for AT-SPI `focus:`/`window:` events, detects the foreground file manager via `xdotool getactivewindow getwindowpid` + `/proc/<pid>/cwd` for the current path.
- **`FileSystemEventWatcher`**: `FileSystemWatcher` on 6 user dirs (Desktop, Documents, Music, Pictures, Videos, Downloads) — created/deleted/renamed/changed; 500ms debounce; exclusion prefixes (Waydroid, flatpak, snap, cache, Trash, containers, Steam, node_modules, …).
- **`RecentFilesWatcher`**: watches `~/.local/share/recently-used.xbel` (XDG), parses the top 5 bookmarks, emits `open` events for recent files.
- **`EventCoordinator`**: validates (file manager, not ignored process, valid path), infers `object_type` (Folder/File/Window via `File.GetAttributes` + path heuristics) and `action` (navigate/open/create/delete/rename/modify/close), dedups (3s) + correlates (500ms), assigns `journey_id` + `sequence`, reaps stale journeys (15 min).
- **`JourneyEngine`**: resolves/creates an `AppSession` per journey, ensures a `file_manager_tab` root item, and stores `fm_{object}_{action}` items with the 9 journey fields; a `close` action closes the session + items.
- **`DesktopEventService`** (hosted) wires it all: starts Linux AT-SPI watcher + filesystem + recent-files watchers, subscribes them to the coordinator, forwards normalized events to the engine.

---

## 13. Sync / Transport to Server

- **Protocol:** REST over HTTP(S); JSON `{employeeId, token, entries: [...]}`; token = encrypted JWT from login, sent in the request body (not a header). Idempotent by design — the server upserts by client GUID (`ON CONFLICT (id) …`), so failed chunks are retried safely.
- **Engine (2026-08-11):** dedicated `SyncService` background loop — collection **never blocks on the network**. Unsent rows drain in chunks bounded by **both** row count (`ALPHA_SYNC_MAX_ROWS`, default 1000) **and** serialized payload bytes (`ALPHA_SYNC_MAX_BYTES`, default ~1MB), with a politeness pause between chunks (`ALPHA_SYNC_CHUNK_DELAY_MS`, 150ms), a per-pass time budget (`ALPHA_SYNC_MAX_DURATION_SEC`, 5 min) so a backlog never monopolizes CPU, and **exponential backoff** on failure (5s → 10s → … → `ALPHA_SYNC_BACKOFF_MAX_SEC`, 5 min). **2026-08-18:** the between-pass wait is now **capped at 60s even on failure** — backoff only stretches a single retry gap, never the guaranteed cadence — so every `is_synced=0` row reaches the server within a minute (user rule: force-push every minute). A 50k+ backlog drains in minutes — the old inline sync moved a fixed 500 rows/table per 5-minute cycle while blocking collection (~8h to drain 50k).
- **Compression:** request bodies are **gzip**-encoded when `ALPHA_SYNC_COMPRESSION=true` (server must run `middleware.Decompress()`); oversized slices are auto-split (binary halving) to stay under the byte cap.
- **Order matters:** small/inventory tables first (device-hardware → network-info → session-events → installed-apps → installed-packages), then **app-sessions (parents)**, then **app-items (children)**.
- On success rows are marked `is_synced=1` (batched `UPDATE … WHERE id IN (…)`, 400 ids/statement); on 401/403 the chunk is dropped until re-login; on network failure the chunk is retried next pass with backoff.

| Endpoint | Payload extras |
|---|---|
| `POST /api/v1/device-hardware/sync` | macAddress, storageDevices (JSON), gpuModel/vramMb |
| `POST /api/v1/installed-apps/sync` | binaryName, isBrowser, desktopId, categories |
| `POST /api/v1/installed-packages/sync` | packageName, version, category, sourceManager |
| `POST /api/v1/network-info/sync` | publicIp, privateIp, networkInterfaceName |
| `POST /api/v1/session-events/sync` | eventType, osUsername |
| `POST /api/v1/app-sessions/sync` | installedAppId, installedPackageId, processId, parentProcessId, groupedBy, cgroupScope, contextLabel |
| `POST /api/v1/app-items/sync` | parentItemId, processId, url/domain, 9 journey fields, windowId/tabId, metadataJson |
| `POST /api/v1/app-status/sync` | key, value, updatedAt (upserted by employee+key; re-sent when a value changes) |
| `POST /api/v1/hardware-devices/sync` | deviceClass, vendor, product, serial, busPath, pluggedAt/unpluggedAt |
| `POST /api/v1/permission-status/sync` | checkId, sessionId, sessionType, platform, method, works, details |
| `POST /api/v1/storage-devices/sync` | deviceHardwareId, deviceType, model, capacityMb |
| `POST /api/v1/auth/employee-login` | employeeId + secretKey → {employee, token} |

> **Instant sync on login (2026-08-11):** `MainViewModel` (which injects the singleton
> `SyncService` — registered `AddSingleton` + `AddHostedService(GetRequiredService)` like
> `LogCollectorService`) calls `RequestImmediateSync()` after a successful login and on session
> restore. That releases a `SemaphoreSlim(0,1)` the loop waits on, so a **full drain pass starts
> immediately** — the machine's whole picture (hardware, inventory, network, permissions, sessions,
> items) lands on the server right after login, not on the next 60s idle tick. A request while a
> pass is already running/pending is a single no-op release (one drain covers it — never stacks).

> **Retention (2026-08-11):** after each clean sync pass, `SyncService` deletes rows the server
> already has and that are no longer needed locally — `app_items`/`app_sessions` older than
> `ALPHA_SYNC_RETENTION_HOURS` (24h; **open sessions are never deleted**, sessions are only deleted
> once their items are gone), `installed_applications`/`installed_packages` with `is_installed=0`,
> and superseded `network_info` rows (`is_current=0`). All other tables are retained forever.

> `activity-logs/sync` and `shell-commands/sync` are **removed** — no `activity_logs`/`shell_commands` tables or calls.

---

## 14. Configuration & Encryption

### Env keys (`AppConfig.FromEnv`)

| Key | Default | Meaning |
|---|---|---|
| `ALPHA_CLIENT_ID` | auto (`.machine-id` file next to binary, else GUID) | stable machine identifier |
| `ALPHA_DB_PATH` | `data/alpha_tracker.db` | SQLite path (resolved under `~/.local/share/alpha-ai-tracker` Linux / `%LOCALAPPDATA%\AlphaAITracker` Win) |
| `ALPHA_DB_ENCRYPTION_KEY` | — | SQLite encryption key (not currently wired to sqlcipher) |
| `ALPHA_COLLECT_INTERVAL_SEC` | 30 (min 5) | process-collection interval |
| `ALPHA_LOG_LEVEL` | Info (Verbose/Debug/Info/Warn/Error) | logging level |
| `ALPHA_SERVER_URL` | `DefaultServerUrl` assembly metadata (baked at publish) | server base URL |
| `ALPHA_API_KEY` | — | unused by sync (login token is used) |
| `ALPHA_BROWSER_TRACKING_ENABLED` | true | master switch for the accessibility browser tracker |
| `ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS` | 3 | a11y poll interval |
| `ALPHA_BROWSER_JOURNEY_IDLE_MINUTES` | 15 | idle-close timeout |
| `ALPHA_BROWSER_CAPTURE_INCOGNITO` | false | store incognito URLs (legal review required) |
| `ALPHA_BROWSER_HISTORY_ENABLED` | true | profile-history URL fallback |
| `ALPHA_FILE_JOURNEY_ENABLED` | true | master switch for the Desktop Event Bus (file-manager navigations + file create/rename/delete/recent-file journeys) |
| `ALPHA_SYNC_INTERVAL_SEC` | 60 | min wait between sync drain passes when idle |
| `ALPHA_SYNC_MAX_ROWS` | 1000 | max rows per sync chunk |
| `ALPHA_SYNC_MAX_BYTES` | 1000000 | max serialized payload bytes per chunk (~1MB) |
| `ALPHA_SYNC_CHUNK_DELAY_MS` | 150 | politeness pause between chunks while draining a backlog |
| `ALPHA_SYNC_MAX_DURATION_SEC` | 300 | per-pass time budget — a huge backlog never monopolizes CPU |
| `ALPHA_SYNC_BACKOFF_MAX_SEC` | 300 | exponential-backoff ceiling on sync failure (5 min) |
| `ALPHA_SYNC_COMPRESSION` | true | gzip request bodies (server: `middleware.Decompress()`) |
| `ALPHA_BROWSER_HISTORY_POLL_SECONDS` | 10 | history re-read cadence |
| `ALPHA_UPDATE_REPO` | — (no code default) | GitHub repo the self-updater checks — ALWAYS from `.env`; falls back to `REPO=`; updater disabled when both are empty |
| `ALPHA_UPDATE_ENABLED` | true | master switch for self-update (background checks + auto-install + GUI) |
| `ALPHA_UPDATE_AUTO_CHECK_HOURS` | 24 | min hours between quiet background update checks (persisted `update_last_check_at` in app_status) |
| `ALPHA_UPDATE_AUTO_INSTALL` | true | auto-download+install when a check finds a newer version (Linux still shows the polkit password dialog) |

> `client/.env.example` carries a `REPO=` key; the client self-updater reads it as a fallback when
> `ALPHA_UPDATE_REPO` is unset (the web dashboard separately owns its GitHub download link via `NEXT_PUBLIC_GITHUB_REPO`).

### Loading order (`EnvLoader.Load`)

1. **Explicit path** (`--config-enc`).
2. **Dev (`dotnet run`):** plaintext `.env` in the source tree (walked up to 6 dirs) is authoritative — a stale user `config.enc` can never shadow fresh edits.
3. **Installed:** user-config `config.enc` (`~/.config/alpha-ai-tracker`, `%APPDATA%\AlphaAITracker`, `~/Library/Application Support/AlphaAITracker`) — but if the freshly shipped copy next to the binary decrypts to **different values** (`ShippedConfigDiffers` on decrypted content), it **replaces** the stale user copy (fixes "rebuilt installer still uses old server URL").
4. Corrupt user copies self-heal from the shipped copy; then app-dir `config.enc`; then plaintext `.env` as last resort.

### Encryption

`EncryptedConfigService` — **AES-256-GCM**, format `[nonce 12][tag 16][ciphertext]`. Build time encrypts with the fixed **transport key** (SHA256 of `AlphaAITracker:TransportKey:v1`); first launch re-encrypts with a **machine-derived key** (SHA256 of `AlphaAITracker:MachineKey:v1:` + OS machine id — `/etc/machine-id`, Windows `MachineGuid`, macOS `IOPlatformUUID` — falling back to a persisted `.machine-id` in the user config dir). Leftover plaintext `.env` files are securely wiped (3-pass random overwrite). `--print-config` prints the resolved values of an installed build.

---

## 15. Installer / Deployment

### Build scripts (`client/publish/`)

| Script | Purpose |
|---|---|
| `build-installer.sh` | Cross-platform: builds Release, publishes per-RID (win-x64/linux-x64/osx-x64), **stale-binary guard** (aborts if any source file is newer than the published `client.dll`), bundles `publish/*.sh` + `*.iss`, encrypts config, calls platform builders |
| `build-deb.sh` | Linux `.deb` (prerm kills running instances) |
| `build-dmg.sh` | macOS `.dmg` |
| `installer-windows.iss` | Inno Setup (auto-kills running processes) |
| `generate-windows-vars.sh` | Reads `APP_IDENTIFIERS` + `VERSION` → `windows_vars.iss`, so the Inno script carries no literal product name |
| `release.sh` | GitHub release workflow |
| `encrypt-config.sh` | `.env` → `config.enc` (transport key) |
| `firefox-a11y-apparmor.sh` | **snap Firefox fix**: loads a surgical copy of the snap AppArmor profile adding ONE `dbus (receive)` rule so the AT-SPI bridge can read snap-Firefox windows (sandbox stays enforcing); installs `alpha-ai-firefox-a11y.service` to re-apply at boot / after `snap refresh`; `--undo` supported; requires Firefox restart |

> **No `extensions/`** — browser tracking is accessibility-based (embedded in the binary); `install-extensions.sh` was deleted 2026-08-05.

### ⚠️ Build-Parity Rule (mandatory) — `dotnet run` ≠ installed build

Installed builds ship the **publish output** plus only what the scripts bundle, from a root-owned install dir. **A change is NOT done until it works from an installed build.**

1. **Always ship-test** — build the installer, install it, run the feature there.
2. **New runtime assets** (icons, JSON, images) — must be added to `bundle_into_publish()` / `build-deb.sh` / `build-dmg.sh` / `installer-windows.iss`.
3. **New scripts in `publish/`** — copied into every publish output automatically.
4. **New env vars** — add to `.env` BEFORE `encrypt-config.sh`; installers ship `config.enc` baked at build time (auto-propagation now replaces stale user copies on next launch).
5. **Path assumptions** — never write relative to cwd/exe dir (root-owned). Use `~/.config/alpha-ai-tracker/` (logs, machine-id) and `~/.local/share/alpha-ai-tracker/` (DB).
6. **Packaging edits apply to ALL platforms** — update all four builder scripts together.
7. **Stale-binary guard** — `build-installer.sh` aborts if source is newer than published `client.dll`; fix with `dotnet clean && bash publish/build-installer.sh`. Covers compiled code only — items 2–6 are the developer's responsibility.

### Branding & version — one file each, two consumers each

`client/APP_IDENTIFIERS` and `client/VERSION` are the only places a product name or version number is
written. Both feed **two** paths from that single edit:

| File | Build-time consumer | Runtime consumer |
|---|---|---|
| `APP_IDENTIFIERS` | `build-deb.sh`, `build-dmg.sh`, `generate-windows-vars.sh` → `installer-windows.iss`, `release.sh` — package names, bundle id, desktop entry, publisher | `client.csproj` embeds it as `client.APP_IDENTIFIERS`; `Core/AppInfo` parses it and the UI binds to `AppDisplayName`, `AppTagline`, `AppInitials`, `AppCopyright` |
| `VERSION` | installer filenames and package metadata | `client.csproj` sets `Version` / `InformationalVersion`; `AppInfo.Version` reads it back (stripping any `+sha`), surfaced as `AppVersionDisplay` and the window title |

Two consequences worth knowing before you touch either file:

- **The embedded resource lives inside `client.dll`,** so branding needs no `bundle_into_publish()` entry
  — installer parity for item 2 above is automatic. Same for `Assets/backgrounds/*.png`, which the
  existing `<AvaloniaResource Include="Assets\**" />` glob already compiles in.
- **`client.csproj` reads `VERSION` at project-evaluation time,** not in a `BeforeBuild` target. That is
  deliberate: a `BeforeBuild` read would leave IDE design-time builds silently on `1.0.0`.
- ⚠️ **`Core/EncryptedConfigService.cs` `TransportKeySeed` / `MachineKeyPrefix` are NOT branding.** They
  are key-derivation seeds. Re-branding must leave them byte-for-byte alone — changing them makes every
  `config.enc` already deployed in the field undecryptable.

---

## 16. Production-Readiness Gaps

| Gap | Severity | Details |
|---|---|---|
| **No crash handling** | 🔴 High | No `AppDomain.UnhandledException` subscription, no crash dumps, no telemetry. |
| ~~**No auto-update**~~ (resolved 2026-08-12) | — | `AppUpdateService` checks GitHub Releases on a 24h cadence + on demand (GUI **Check updates**), auto-installs (Linux polkit / Windows silent Inno / macOS dmg) and relaunches itself. |
| **SQLite unencrypted** | 🟠 Medium | `ALPHA_DB_ENCRYPTION_KEY` exists but sqlcipher is not wired in. |
| **macOS CPU = 0%** | 🟠 Medium | macOS collector does not measure CPU. |
| **macOS window capture limited** | 🟢 Low | Only foreground window via osascript; no EnumWindows equivalent. |
| **No storage quota** | 🟢 Low | No periodic pruning of old synced rows (only `permission_status` 24h prune + boot cleanup passes). |
| **No retry backoff** | 🟢 Low | Sync retries every ~5 min regardless of failure count. |
| **Single employee per device** | 🟢 Low | One login at a time; logging in as another employee replaces it. |
| **Browser journey = active tab only** | 🟢 Low | Background tabs are not captured (a11y exposes the active tab; history fallback is title-match). Per-window journey is exact; per-tab parallelism is not. |
| **Tray has no Quit** | 🟢 Low | Tray menu = Show / Hide only; window close hides to tray. Full exit via process stop (systemd); there is no in-app disconnect/logout. |

---

## 17. Immediate Next Steps

1. **Add tests** — start with `SqliteLogStore`, `ProcessFilter`, `PackageDetector`, `AccessibilityBrowserTracker` rotation logic.
2. **Add crash reporting** — subscribe to `AppDomain.CurrentDomain.UnhandledException`, write to the log file.
3. **Enable SQLite encryption** — wire `ALPHA_DB_ENCRYPTION_KEY` into the provider (sqlcipher).
4. **Fix macOS CPU + window capture** — sample `TotalProcessorTime`; enumerate windows beyond the foreground.
5. **Add periodic data cleanup** — prune old synced `app_sessions`/`app_items` locally.
6. **Offline retry with backoff** — exponential backoff on sync failures.
7. ~~**Consider auto-update**~~ (resolved 2026-08-12 — built-in `AppUpdateService`, §18).
8. ~~Remove dead shell-command code~~ ✅ (deleted 2026-08-05)
9. ~~Debugger/extension browser pipeline~~ ✅ (replaced 2026-08-05 by Option B accessibility)
10. ~~File-explorer journey watchers~~ ✅ (2026-07-29)
11. **Installed-build acceptance** — rebuild the installer and re-verify the a11y journey + private-window URLs from the installed build (the 2026-08-07 verification passed; re-run after the next code change).
12. **Ship-test the six-page GUI** — the hero images and `APP_IDENTIFIERS` both ride inside `client.dll`, so no packaging change was needed, but that has not yet been confirmed from an installed build. Per the Build-Parity Rule this is the remaining acceptance step for the 2026-08-10 redesign.
13. **Re-brand smoke test** — change `DISPLAY_NAME` in `APP_IDENTIFIERS`, bump `VERSION`, rebuild, and confirm the rail, window title, splash, footer, tray tooltip and installer filenames all follow with no other edit.

---

## 18. Self-Update from GitHub Releases (`AppUpdateService`)

> Added 2026-08-12. **Installers are published to GitHub by `release.sh`** (tag `v<version>` with the
> `.deb` / `.exe` / `.dmg` attached); this service is the client half of that loop.

### Flow

1. `GET https://api.github.com/repos/{ALPHA_UPDATE_REPO}/releases/latest` (User-Agent set; GitHub requires it).
2. Normalize `tag_name` (`v1.1.0` → `1.1.0`; numeric three-part compare, `-beta`/`+sha` ignored).
3. Pick the installer asset for this platform: Linux → `_<arch>.deb` (arch from `RuntimeInformation`,
   fallback any `.deb`); Windows → `.exe`; macOS → `.dmg`. No asset → "No release found".
4. Compare vs `AppInfo.Version` (the `VERSION` file). Newer → `UpdateAvailable`, else "You're up to date".
5. **Auto-install** (when `ALPHA_UPDATE_AUTO_INSTALL=true`, the default):
   - **Linux:** stream-download to `~/.local/share/alpha-ai-tracker/updates/`, then `pkexec dpkg -i`
     (polkit password dialog — the only human step; the install dir `/usr/share/alpha-ai-tracker` is
     root-owned). dpkg replaces the binary while the process runs; a **Restart to apply** button
     relaunches with `--restart`.
   - **Windows:** download, write a detached `.cmd` that runs `start /wait "" installer.exe
     /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` then relaunches the app with `--restart`. The Inno
     installer (`CloseApplications=force` + `AppMutex` + `taskkill` in `InitializeSetup`) terminates
     the running app itself, so the relaunch must come from outside the process.
   - **macOS:** `open` the dmg (no silent installer for the dmg build) — manual drag to Applications.
6. `--restart` in `Program.cs`: retry the single-instance mutex for up to 8s (`WaitOne`) instead of
   the normal second-launch signal-and-exit, so the freshly-installed binary can take over.

### GUI

- Top bar: **Check updates** ghost button (always visible), **"Update to vX.Y.Z"** primary button when
  available, **Restart to apply** after a Linux dpkg install, and a status line for check feedback.
- Dashboard: an **update banner** card with version, release notes, download progress bar,
  **Download & Install** and **Later** (persists `update_dismissed_version` in app_status so a
  dismissed version is not re-offered by background checks; a manual check still shows it).
- Both surfaces bind the SAME singleton `AppUpdateService` instance (injected into `MainViewModel`
  and `DashboardViewModel`), which is an `ObservableObject` (`IsChecking`, `IsDownloading`,
  `DownloadProgress`, `UpdateAvailable`, `RestartReady`, `StatusText`, …).

### Background loop

`IHostedService.StartAsync` starts a 30-min `PeriodicTimer` loop; each tick calls
`RunAutoCheckIfDueAsync`, which reads `update_last_check_at` (app_status) and only checks when the
`ALPHA_UPDATE_AUTO_CHECK_HOURS` (default 24h) interval has elapsed. `MainViewModel.EnterShellAsync`
also fires the same method on shell entry (startup/login), so a check runs shortly after launch even
if the 30-min tick has not yet elapsed. All failures surface as `StatusText` and are never thrown
into the UI thread.

### Config knobs

| Key | Default | Meaning |
|---|---|---|
| `ALPHA_UPDATE_REPO` | — (no code default) | GitHub repo to check — ALWAYS from `.env`; falls back to `REPO=`; disabled when both empty |
| `ALPHA_UPDATE_ENABLED` | true | master switch (background loop + GUI no-op when false) |
| `ALPHA_UPDATE_AUTO_CHECK_HOURS` | 24 | min hours between background checks |
| `ALPHA_UPDATE_AUTO_INSTALL` | true | auto-download+install without a click |

⚠️ **Installers are not code-signed / checksum-verified** — the download is trusted over GitHub's TLS.
Signing is a future hardening step (the Inno `.iss`, `build-deb.sh` and `build-dmg.sh` have no signing
hooks yet).

## 19. Time & Attendance (Phase 1 — client foundation)

> Added 2026-08-28. Builds the client half of the **Time & Attendance** sidebar module. The web
> dashboard (Phase 2) renders this data via new server endpoints. This section is the client
> foundation only; it is fully transport-independent and ships behind `ALPHA_TA_ENABLED`.
> See the repo-root `finalplan.txt` (§0–§15) for the authoritative build-order + rule audit.

### Vocabulary (single source of truth, `Core/Models/SessionEvent.cs`)

The `SessionEventTypes` static class is the ONE place every session_events `event_type` string
lives on the client (finalplan R5). Go and web mirrors exist; run `test/contract-event-types.sh`
to catch drift.

| Constant | event_type | Emitter |
|---|---|---|
| `PowerOn` | `power_on` | `LogCollectorService.StartTracking` + `SystemEventWatcher` boot |
| `PowerOff` | `power_off` | `ShutdownSentinel` (SIGTERM / Ctrl+C / Avalonia exit / dispose) |
| `Resume` | `resume` | `SystemEventWatcher` (UPower, SystemEvents resume) |
| `OsLogin` | `os_login` | `SystemEventWatcher` (Windows SessionSwitch) |
| `OsLogout` | `os_logout` | `SystemEventWatcher` (Windows SessionSwitch) |
| `ScreenLock` | `screen_lock` | `SystemEventWatcher` (login1 / GNOME ScreenSaver / SessionSwitch) |
| `ScreenUnlock` | `screen_unlock` | `SystemEventWatcher` (login1 / ScreenSaver / SessionSwitch) |
| `TrackerLogin` | `tracker_login` | `LogCollectorService.StartTracking` |
| `UiHidden` | `ui_hidden` | `App.axaml.cs` window.Closing (hide-to-tray) |
| `IdleStart` | `idle_start` | `IdleDetector` (threshold crossing) |
| `IdleEnd` | `idle_end` | `IdleDetector` (threshold crossing) |
| `OldDataDropped` | `old_data_dropped` | `SyncService` S6 row-ceiling rollup |

`Login` is kept as an `[Obsolete]` alias of `TrackerLogin` for back-compat with pre-2026-08-28 rows.

Cross-service mirrors: `server/internal/models/session_event_types.go`, `web/src/lib/eventTypes.ts`.
Contract test: `test/contract-event-types.sh` (T5).

### Services

| Service | Type | Cadence | Purpose |
|---|---|---|---|
| `SessionEventRecorder` | `IEventRecorder` singleton | — | Single funnel for ALL session_events writes; 5 s dedup window (burst collapse) + hard 2 s write timeout |
| `ShutdownSentinel` | hosted (FIRST in DI) | — | Writes `power_off` before the host stops; background mode uses `WaitForShutdownAsync` so SIGTERM reaches hosted-service shutdown, then `ManualResetEventSlim` bounds the final write wait |
| `SystemEventWatcher` | hosted | event-driven | Linux D-Bus (UPower / login1 shutdown+lock / GNOME ScreenSaver), Windows `SystemEvents`, macOS stub; login1 `PrepareForShutdown(true)` persists before Xwayland teardown; touches `ta_last_known_os_event_at` watermark |
| `IdleDetector` | hosted | 30 s poll | OS idle source (Mutter.IdleMonitor / X11 / GetLastInputInfo); emits `idle_start`/`idle_end` crossings |
| `ScheduleCacheService` | hosted | login/resume + every 6 h | Mirrors the Phase 2 `GET /api/v1/schedules/me` response into local tables; login wakes it immediately and resume waits 10 s for network stabilization |
| `LocalTimeSkewService` | hosted | startup/resume + every 15 min | Measures client↔server clock skew from `GET /api/v1/server-time`'s Date header; resume waits 10 s before measuring |
| `AttendanceAggregator` | hosted | login + every 5 min | Rolls up arrival/last-seen, the union of idle+screen-lock time, active time, schedule overlap, holidays, lateness, absence, half-day, and off-shift time; legacy offset timestamps are normalized to UTC at the store boundary |

DI order (finalplan §3): `ShutdownSentinel` is registered FIRST so .NET stops it LAST — guaranteeing
`power_off` is written while the SQLite singleton is still alive.

### SQLite additions (made idempotent in `DatabaseSchema.CreateTableSql`)

- `employee_schedule` (PK employee_id) — mirrored shift (timezone, weekly_pattern JSON, grace_minutes, validity).
- `company_holidays` (PK holiday_date) — mirrored holiday calendar.
- `daily_attendance_cache` (PK employee_id, work_date) — DERIVED, client-owned, NEVER sent to server.
- `local_time_skew` (PK server_url) — per-server clock-skew measurement.
- Indexes: `(event_type, event_at)` on session_events, `(employee_id, work_date)` on
  `daily_attendance_cache` (finalplan S5).

### Concurrency (finalplan R8 / BUG-9)

With 6 hosted services hitting SQLite, the original single-connection `SemaphoreSlim(1,1)` would
serialize everything and risk deadlock. `Program.cs` initializes the store before starting hosted
services (so the first boot event cannot race SQLite); `SqliteLogStore.InitializeAsync` is
serialized/idempotent and:
1. enables `PRAGMA journal_mode=WAL;` + `synchronous=NORMAL` FIRST, then
2. opens a second **ReadOnly** connection (`WithReadConnectionAsync`) for pure readers
   (`AttendanceAggregator`, schedule/holiday/skew reads).

Writers (collector, sync) still use the gated write connection; readers use the read connection, so
collection never blocks aggregation and vice versa.

### Feature flag & config

| Key | Default | Meaning |
|---|---|---|
| `ALPHA_TA_ENABLED` | true | master switch; false parks SystemEventWatcher, IdleDetector, ScheduleCacheService, LocalTimeSkewService, AttendanceAggregator |
| `ALPHA_IDLE_THRESHOLD_SEC` | 120 | seconds of no input before `idle_start` |
| `ALPHA_IDLE_AWAY_THRESHOLD_SEC` | 600 | reserved for A.8 away classification |
| `ALPHA_IDLE_POLL_SEC` | 30 | idle-source poll cadence |
| `ALPHA_TA_LOCK_HYSTERESIS_SEC` | 30 | suppress re-locks within this window |
| `ALPHA_EVENT_AGGREGATION_WINDOW_SEC` | 300 | sync-time bucket size for session_events aggregates (S1) |
| `ALPHA_TA_MAX_LOCAL_ROWS` | 50000 | unsynced row ceiling; excess rolls into `old_data_dropped` (S6) |

### Session-event sync aggregation (A.9 / A.10)

Raw OS events are written one-per-row to SQLite immediately (BUG-13). `SyncService` groups
unsynced rows into rolling buckets per `(event_type, window)` when the bucket window has fully
elapsed, then POSTs `{ count, firstAt, lastAt }` to `/api/v1/session-events/sync`. Closed buckets
only — the current open window stays local until it closes. `SessionEventSyncAggregator` performs
the grouping; `DrainSessionEventsAsync` marks every source row `is_synced` after a successful send.

### Phase 1 → Phase 2 handoff

- **Server (Phase 2, implemented 2026-08-31):** `GET /api/v1/schedules/me` (SVR-1),
  `GET /api/v1/server-time` (SVR-3), `GET /api/v1/attendance/today|range` (SVR-4/5), holiday
  CRUD, and aggregate-compatible `session-events/sync` fields `{count, firstAt, lastAt}` (SVR-2).
- **Client (A.9/A.10, implemented 2026-09-01):** `SessionEventSyncAggregator` + `DrainSessionEventsAsync`
  aggregate unsynced rows at sync time (`ALPHA_EVENT_AGGREGATION_WINDOW_SEC`, default 300 s).
  S6 ceiling via `ALPHA_TA_MAX_LOCAL_ROWS` + `old_data_dropped` sentinel. Mirrors:
  `server/internal/models/session_event_types.go`, `web/src/lib/eventTypes.ts`;
  `test/contract-event-types.sh` (T5).
- **Client follow-up:** backward compatible — servers without aggregate columns still accept
  count=1 rows; Phase 2 server expects `{count, firstAt, lastAt}`.
- **Web (Phase 2, live):** `/attendance`, `/timesheets`, `/holidays` call the attendance/holiday
  APIs with infinite scroll. First/last active times use `record.timezone` from the server (shift
  IANA zone) — operators must set `DEFAULT_SHIFT_TIMEZONE` or per-shift timezone on the server so
  late/present matches wall-clock. `gps-location` UI is gated Coming Soon (`LOCATION_UI_ENABLED`).
