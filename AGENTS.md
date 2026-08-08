# Alpha AI Tracker — Project Map

> **Last audited:** 2026-08-08
> **Changelog:**
>
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
3. **Web Dashboard** (`web/`) — Next.js 15 App Router. Admin-facing UI for viewing employee data, managing departments, generating login secrets, and analytics. Most pages currently render mock localStorage data rather than calling the real API.

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

> All 7 sync endpoints exist on the server. `activity-logs/sync` and `shell-commands/sync` were removed from the product entirely (client + server).

---

## 3. Service Breakdown Table

| Service           | Stack                                                                | Responsibility                       | Entry Point                                | Internal Doc                                      |
| ----------------- | -------------------------------------------------------------------- | ------------------------------------ | ------------------------------------------ | ------------------------------------------------- |
| **client/** | .NET 10, Avalonia 12.1, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite | Employee-side data collection & sync | `Program.cs`, `App.axaml.cs`           | [client/ARCHITECTURE.md](./client/ARCHITECTURE.md) |
| **server/** | Go 1.25, Echo v4.15, pgx v5.10, go-redis v9.21                       | Central API hub, data storage, auth  | `cmd/server/main.go`                     | [server/ARCHITECTURE.md](./server/ARCHITECTURE.md) |
| **web/**    | Next.js 15.3.4, React 18, Redux Toolkit, TanStack Query              | Admin dashboard & analytics          | `next.config.ts`, `src/app/layout.tsx` | [web/ARCHITECTURE.md](./web/ARCHITECTURE.md)       |

---

## 4. Cross-Service Contracts

### Client ↔ Server

| Direction                                  | Protocol                                      | Auth Method                           | Format                                                   |
| ------------------------------------------ | --------------------------------------------- | ------------------------------------- | -------------------------------------------------------- |
| Employee login (client → server)          | REST POST`/api/v1/auth/employee-login`      | emp_id + secret_key (Redis-validated) | JSON`{employeeId, secretKey}` → `{employee, token}` |
| Employee disconnect                        | REST POST`/api/v1/auth/employee-disconnect` | JWT token in body                     | JSON`{employeeId, token}`                              |
| Device hardware sync (client → server)    | REST POST`/api/v1/device-hardware/sync`     | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Installed apps sync (client → server)     | REST POST`/api/v1/installed-apps/sync`      | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Installed packages sync (client → server) | REST POST`/api/v1/installed-packages/sync`  | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Network info sync (client → server)       | REST POST`/api/v1/network-info/sync`        | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| Session events sync (client → server)     | REST POST`/api/v1/session-events/sync`      | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| App sessions sync (client → server)       | REST POST`/api/v1/app-sessions/sync`        | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |
| App items sync (client → server)          | REST POST`/api/v1/app-items/sync`           | JWT token in request body             | JSON`{employeeId, token, entries: [...]}`              |

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

- Migrations 001-016 run on startup (15 files; 010 adds `process_id`/`parent_process_id`, 013 adds session identity, 014 adds journey fields, 015/016 add app/package catalogs + junction tables)
- Full CRUD for users, employees, departments
- Web admin auth (email/password → httpOnly cookie with encrypted JWT)
- Employee auth (Redis one-time secret → JWT token)
- 7 sync endpoints: device_hardware, installed_apps, installed_packages, network_info, session_events, app_sessions, app_items (+ synced_at for all)
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
- Login/logout flow with server
- Batched sync engine (every ~5 min, FK-ordered, 500-row batches, stop-on-failure per table)
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
- **Headless `--background` service mode** — runs the tracking services with no Avalonia/X11 UI (systemd); skips GUI init so the installed service can't crash on Wayland `XAUTHORITY`
- **Single-instance activation** — a second user launch signals the running instance (named pipe `alpha-ai-tracker-activation`) to raise its window; `--background`/`--minimized` relaunches exit quietly

**What's missing:**

- **No tests** (0 test files)
- **No auto-update mechanism**
- **No crash reporting** — unhandled exceptions crash silently
- **No offline queue analysis** — if server is unreachable, logs buffer locally with no back-pressure handling
- **No encryption at rest** — SQLite encryption (sqlcipher) is commented out
- **macOS CPU measurement** — macOS process collector skips CPU measurement (always 0%)
- **macOS window titles** — only captures foreground window

### Web — ~16% complete

**What works:**

- ~45 page routes exist with polished UI
- Login page with animated hero section
- Auth check on mount (Redux + server cookie)
- Users page — real API calls via TanStack Query (CRUD + generate secret)
- Departments page — real API calls (CRUD)
- Logs/Comprehensive page — real API calls (now using new app_sessions API)
- Sidebar with permission-based filtering (client-side only)
- Dashboard shows mock stats and chart

**What's missing (most pages):**

- **~40 of 45 pages use mock localStorage data** — not connected to real API
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
| **Git branch**          | Currently on`restructureClient` branch — no PR/branch convention visible            |
| **Commit style**        | Descriptive lowercase messages: "now remove the exit btn on the tray on windows", "fixit" |
| **Monorepo tooling**    | No shared tooling (no Turborepo, Nx, etc.). Each service has its own build system.        |
| **Build parity**        | `dotnet run` is NOT a release test — every change must be verified from an installed build; new assets/config/scripts must be bundled by the `publish/*` scripts (see below) |

### Installer-Parity Rule (mandatory)

**Why:** `dotnet run` always reflects the source tree, so it can never catch packaging gaps. The installed app runs from a root-owned dir (`/usr/share/alpha-ai-tracker/` on Linux, Program Files on Windows) with only what the `publish/*` scripts bundled. Recent real incidents: installed app crashed at startup (log written to a root-owned dir), browser-journey tracking dead (extensions/ not bundled), stale binary shipped (publish not rebuilt).

**Every feature/modification must be verified end-to-end in an installed build:**

1. **Always ship-test** — `dotnet run` success is NOT sufficient. Build the installer, install the artifact, and run the new functionality from there:
   ```bash
   cd client
   bash publish/build-installer.sh -b linux   # or win / mac
   sudo dpkg -i installers/alpha-ai-tracker_1.0.0_amd64.deb
   ```
2. **New runtime assets** (icons, JSON, images, fonts) — bundled ONLY if copied by `build-installer.sh` (`bundle_into_publish`) or the platform builders (`build-deb.sh`, `build-dmg.sh`, `installer-windows.iss`). Add the copy step when you add the asset.
3. **New runtime assets** (embedded scripts, JSON, icons) — the accessibility probe is embedded as a C# string in `LinuxAtSpiBrowserReader` (no external file to bundle). Anything file-based must be added to `bundle_into_publish()` or the platform builders.
4. **New scripts under `publish/`** — copied into every publish output automatically. Runtime-referenced scripts must live in `publish/` (or be added to the copy list).
5. **New env vars / config** — must be added to `.env` BEFORE `encrypt-config.sh` runs; installers ship `config.enc` baked at build time. Dev reads `.env` directly — config that works in `dotnet run` is silently missing in the installer. Config changes now auto-propagate: `EnvLoader` replaces a stale user-config copy (`~/.config/alpha-ai-tracker/config.enc`) with the freshly shipped one on next launch when the decrypted contents differ.
6. **Path assumptions** — the installed app's working dir is root-owned / not user-writable. NEVER write files relative to cwd or the exe dir. Use `~/.config/alpha-ai-tracker/` (logs, machine-id) and `~/.local/share/alpha-ai-tracker/` (DB, sockets). `dotnet run` cannot catch this because the dev working dir is writable.
7. **Packaging edits apply to ALL platforms** — when changing build scripts, update `build-installer.sh`, `build-deb.sh`, `build-dmg.sh`, and `installer-windows.iss` consistently.
8. **Stale-binary guard** — `build-installer.sh` aborts if any source file is newer than the published `client.dll`; fix with `dotnet clean && bash publish/build-installer.sh`. This guard covers compiled code only — items 2–6 are the developer's responsibility.

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
| **Mock data dominance**       | 🟠 Medium | ~90% of web pages use mock data, giving a false sense of completeness.                                                                                                           |
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
