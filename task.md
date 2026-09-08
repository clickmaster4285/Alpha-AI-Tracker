# Alpha Monitoring — ERP Tasks

> **Project:** Alpha Monitoring
> **Goal:** SaaS Based Setup
> **Period:** Jul 24, 2026 → Sep 8, 2026
> **Total Goals:** 25 | **Total Tasks:** ~118
> **Note:** No start/end date falls on a Sunday

---

## Goal 1: Project Foundation & Core Setup
**Start:** Jul 24, 2026 | **End:** Jul 27, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 1.1 | Encrypted config system (AES-256-GCM, transport→machine key migration) | Jul 24, 2026 | Jul 24, 2026 |
| 1.2 | Background service mode (--background headless, auto-start persistence) | Jul 24, 2026 | Jul 25, 2026 |
| 1.3 | Cross-platform process collection (Win/Linux/macOS) | Jul 25, 2026 | Jul 25, 2026 |
| 1.4 | SQLite local storage with relational schema (11 tables) | Jul 25, 2026 | Jul 27, 2026 |
| 1.5 | Session tracking with PID hierarchy + cgroup dedup | Jul 27, 2026 | Jul 27, 2026 |

---

## Goal 2: Browser Journey Tracking
**Start:** Jul 28, 2026 | **End:** Aug 8, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 2.1 | Browser extension for Chrome (MV3, real-time tab navigations) | Jul 28, 2026 | Jul 28, 2026 |
| 2.2 | Native messaging service + NativeMessageService (Unix socket) | Jul 28, 2026 | Jul 28, 2026 |
| 2.3 | Accessibility-based browser journey (AT-SPI Linux, UIA Windows, AX macOS) | Jul 31, 2026 | Aug 5, 2026 |
| 2.4 | Browser history reader fallback (Chromium History + Firefox places.sqlite) | Aug 6, 2026 | Aug 6, 2026 |
| 2.5 | Private/incognito window tracking (Firefox AT-SPI DocURL, Chrome flag) | Aug 6, 2026 | Aug 7, 2026 |
| 2.6 | Dynamic browser registry (IBrowserRegistry replaces hardcoded names) | Aug 20, 2026 | Aug 21, 2026 |
| 2.7 | Embedded webview journey (VS Code Simple Browser, Slack, Electron apps) | Aug 18, 2026 | Aug 18, 2026 |
| 2.8 | Per-page browser_tab records (no more title overwrite) | Aug 6, 2026 | Aug 6, 2026 |

---

## Goal 3: File Explorer Journey Tracking
**Start:** Jul 29, 2026 | **End:** Aug 8, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 3.1 | AT-SPI event watcher (Tmds.DBus.Protocol, focus/window events) | Jul 29, 2026 | Jul 29, 2026 |
| 3.2 | FileSystemEventWatcher + RecentFilesWatcher (XBEL monitor) | Jul 29, 2026 | Jul 29, 2026 |
| 3.3 | EventCoordinator + JourneyEngine (dedup, correlate, normalize) | Jul 29, 2026 | Jul 29, 2026 |
| 3.4 | Windows Explorer watcher (Shell COM, LocationURL, journey-driven watching) | Aug 8, 2026 | Aug 8, 2026 |

---

## Goal 4: Software Inventory Detection
**Start:** Aug 8, 2026 | **End:** Aug 11, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 4.1 | Installed app detection (Linux .desktop, Windows registry+Start Menu, macOS .app) | Aug 8, 2026 | Aug 8, 2026 |
| 4.2 | Windows structural detection (PE subsystem, no hardcoded names) | Aug 8, 2026 | Aug 8, 2026 |
| 4.3 | Package detection (npm/pip/apt/brew/choco/winget/scoop/cargo/snap/flatpak) | Aug 8, 2026 | Aug 8, 2026 |
| 4.4 | InstalledSoftwareWatcher (event-driven, real-time install/uninstall) | Aug 10, 2026 | Aug 10, 2026 |
| 4.5 | Software classification pipeline (dedup, categories, identity) | Jul 30, 2026 | Jul 30, 2026 |
| 4.6 | One row per install cycle (v2 lifecycle, reinstall = new row) | Aug 10, 2026 | Aug 10, 2026 |
| 4.7 | GUI shows only currently-installed software (hide uninstalled history) | Aug 11, 2026 | Aug 11, 2026 |

---

## Goal 5: Client GUI Rebuild
**Start:** Aug 8, 2026 | **End:** Aug 10, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 5.1 | Six-page GUI (Splash, Login, PermissionSetup, Dashboard, SystemSpecs, InstalledApps) | Aug 8, 2026 | Aug 10, 2026 |
| 5.2 | Runtime branding pipeline (APP_IDENTIFIERS → all UI strings) | Aug 10, 2026 | Aug 10, 2026 |
| 5.3 | Nav rail + page router (MainWindow as router, 246px rail) | Aug 10, 2026 | Aug 10, 2026 |

---

## Goal 6: Sync Engine Decoupled
**Start:** Aug 11, 2026 | **End:** Aug 11, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 6.1 | Dedicated SyncService (BackgroundService, byte-bounded chunks, gzip) | Aug 11, 2026 | Aug 11, 2026 |
| 6.2 | Exponential backoff + 5-min per-pass budget | Aug 11, 2026 | Aug 11, 2026 |
| 6.3 | Instant sync on login (RequestImmediateSync) | Aug 11, 2026 | Aug 11, 2026 |
| 6.4 | Client retention + 4 new server sync surfaces (app_status, hardware_devices, permission_status, storage_devices) | Aug 11, 2026 | Aug 11, 2026 |

---

## Goal 7: Auto-Update System
**Start:** Aug 12, 2026 | **End:** Aug 19, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 7.1 | GitHub Releases self-updater (AppUpdateService, auto-check loop) | Aug 12, 2026 | Aug 12, 2026 |
| 7.2 | GUI Check Updates button + update banner + progress bar | Aug 12, 2026 | Aug 12, 2026 |
| 7.3 | Windows auto-install fix (detached .cmd, tree-kill bug) | Aug 12, 2026 | Aug 12, 2026 |
| 7.4 | Linux update handoff (detached bash script, pkexec dpkg) | Aug 19, 2026 | Aug 19, 2026 |

---

## Goal 8: Session Lifecycle (3-State)
**Start:** Sep 2, 2026 | **End:** Sep 5, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 8.1 | 3-state model (ACTIVE / STALE / CLOSED) + migration 031 | Sep 2, 2026 | Sep 2, 2026 |
| 8.2 | Lifecycle sweeper job (session_lifecycle_sweep.go, every 1 min) | Sep 2, 2026 | Sep 2, 2026 |
| 8.3 | Upsert recovery (STALE/CLOSED → ACTIVE on client re-upload) | Sep 2, 2026 | Sep 2, 2026 |
| 8.4 | Foreground/background focus tracking per session (client + server + web) | Aug 18, 2026 | Aug 18, 2026 |
| 8.5 | Atomic cascade-close of app_items with sessions | Jul 31, 2026 | Jul 31, 2026 |

---

## Goal 9: Time & Attendance Foundation
**Start:** Aug 28, 2026 | **End:** Sep 1, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 9.1 | IEventRecorder abstraction + SessionEventRecorder (5s dedup, 2s timeout) | Aug 28, 2026 | Aug 28, 2026 |
| 9.2 | SystemEventWatcher (Linux D-Bus, Windows SystemEvents, macOS stub) | Aug 28, 2026 | Aug 28, 2026 |
| 9.3 | IdleDetector (Mutter.IdleMonitor, X11 XScreenSaver, Windows GetLastInputInfo) | Aug 28, 2026 | Aug 28, 2026 |
| 9.4 | ScheduleCacheService + LocalTimeSkewService | Aug 28, 2026 | Aug 28, 2026 |
| 9.5 | AttendanceAggregator (5-min daily rollup, present/late/absent/off_shift) | Aug 28, 2026 | Aug 28, 2026 |
| 9.6 | A.9/A.10 session-event sync aggregation + S6 row ceiling | Sep 1, 2026 | Sep 1, 2026 |

---

## Goal 10: Server — Shifts, Attendance & Holiday API
**Start:** Aug 31, 2026 | **End:** Sep 1, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 10.1 | Migration 028 (shift timezone, holidays, aggregate event fields) | Aug 31, 2026 | Aug 31, 2026 |
| 10.2 | Schedule CRUD + server-time endpoint + holiday CRUD | Aug 31, 2026 | Aug 31, 2026 |
| 10.3 | Attendance APIs (present/late/absent/off_shift with timezone) | Aug 31, 2026 | Aug 31, 2026 |
| 10.4 | DEFAULT_SHIFT_TIMEZONE env + ShiftService.ApplyDefaultTimezone | Sep 1, 2026 | Sep 1, 2026 |

---

## Goal 11: Web — Employee Journey Pages
**Start:** Aug 17, 2026 | **End:** Aug 18, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 11.1 | EmployeePage shell (header + EmployeeSelector + loading/error states) | Aug 17, 2026 | Aug 18, 2026 |
| 11.2 | Session Timeline page (useInfiniteQuery, FocusTime fg/bg bar) | Aug 18, 2026 | Aug 18, 2026 |
| 11.3 | App Usage page (aggregated per-app fg/bg totals, expandable sessions) | Aug 18, 2026 | Aug 18, 2026 |
| 11.4 | Web Activity page (infinite scroll, domain grouping, browser badges) | Aug 18, 2026 | Aug 18, 2026 |
| 11.5 | Date filters + expandable session groups + structural browser badges | Aug 18, 2026 | Aug 18, 2026 |

---

## Goal 12: Web — Device Specs Pages
**Start:** Aug 17, 2026 | **End:** Aug 18, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 12.1 | Hardware Overview page (specs + storage + network + device status) | Aug 18, 2026 | Aug 18, 2026 |
| 12.2 | Installed Software page (Applications/Packages tabs + search) | Aug 18, 2026 | Aug 18, 2026 |
| 12.3 | Peripherals page (plugged/unplugged cards + DeviceClassIcon) | Aug 18, 2026 | Aug 18, 2026 |
| 12.4 | GET /employees/:id/detail aggregate endpoint (server) | Aug 11, 2026 | Aug 11, 2026 |

---

## Goal 13: Web — Employees Enhancements
**Start:** Aug 18, 2026 | **End:** Aug 18, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 13.1 | Server-side infinite scroll (useInfiniteQuery + IntersectionObserver) | Aug 18, 2026 | Aug 18, 2026 |
| 13.2 | Excel import/export (xlsx download + client-side xlsx parsing + POST /import) | Aug 18, 2026 | Aug 18, 2026 |
| 13.3 | Portal-rendered Radix action menu (View Journey + Device Specs links) | Aug 18, 2026 | Aug 18, 2026 |

---

## Goal 14: Web — Configuration Redesign
**Start:** Aug 20, 2026 | **End:** Aug 22, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 14.1 | Migration 023 (monitoring_types, monitoring_categories, monitoring_sites) | Aug 20, 2026 | Aug 20, 2026 |
| 14.2 | Dynamic Applications + Websites classification pages (ClassifiedItemsTable) | Aug 20, 2026 | Aug 22, 2026 |
| 14.3 | Categories & Types CRUD (two-tab, color picker, kind select) | Aug 20, 2026 | Aug 20, 2026 |
| 14.4 | Manual website creation (POST /monitoring/websites, domain normalization) | Aug 22, 2026 | Aug 22, 2026 |

---

## Goal 15: Dynamic RBAC
**Start:** Aug 25, 2026 | **End:** Aug 25, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 15.1 | Migration 025 (roles, modules, submodules, role_submodule_permissions) | Aug 25, 2026 | Aug 25, 2026 |
| 15.2 | RBACService.SeedCatalog (idempotent boot seeder) | Aug 25, 2026 | Aug 25, 2026 |
| 15.3 | /roles CRUD + GET /modules endpoints (server) | Aug 25, 2026 | Aug 25, 2026 |
| 15.4 | Web: /roles page (per-submodule permission toggles) + RouteGuard + /unauthorized | Aug 25, 2026 | Aug 25, 2026 |

---

## Goal 16: Auth — Refresh Tokens + Profile
**Start:** Aug 25, 2026 | **End:** Aug 28, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 16.1 | Migration 026 (refresh_tokens table, rotating tokens) | Aug 25, 2026 | Aug 25, 2026 |
| 16.2 | POST /auth/refresh endpoint + httpOnly refresh cookie rotation | Aug 25, 2026 | Aug 25, 2026 |
| 16.3 | Self-service Profile page (/settings/profile) | Aug 28, 2026 | Aug 28, 2026 |
| 16.4 | User-Management edit flow + server-projected hasUserLogin flag | Aug 28, 2026 | Aug 28, 2026 |

---

## Goal 17: Web — Dashboard Live API
**Start:** Aug 25, 2026 | **End:** Aug 25, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 17.1 | Remove all localStorage mock data (web/src zero localStorage refs) | Aug 25, 2026 | Aug 25, 2026 |
| 17.2 | Dashboard stat tiles from live API (employees, departments, sessions, pages) | Aug 25, 2026 | Aug 25, 2026 |

---

## Goal 18: Web — URL-Synced Filters
**Start:** Sep 1, 2026 | **End:** Sep 2, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 18.1 | useUrlQueryState + useUrlActivityFilter hooks | Sep 1, 2026 | Sep 1, 2026 |
| 18.2 | Wire URL-synced filters into 13 pages (employees, shifts, timesheets, etc.) | Sep 1, 2026 | Sep 2, 2026 |
| 18.3 | ActivityFilters redesign (date presets, custom range, no Clear button) | Sep 1, 2026 | Sep 1, 2026 |

---

## Goal 19: Web — Attendance & Timesheets
**Start:** Sep 1, 2026 | **End:** Sep 3, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 19.1 | Attendance page (date + status filters, late/present/absent status) | Sep 1, 2026 | Sep 2, 2026 |
| 19.2 | Timesheets page (employee + from + to filters, active time calc) | Sep 1, 2026 | Sep 3, 2026 |
| 19.3 | Shift management (dynamic shifts, timezone defaulting) | Aug 28, 2026 | Aug 28, 2026 |

---

## Goal 20: Cross-Platform — Linux Fixes
**Start:** Jul 31, 2026 | **End:** Sep 5, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 20.1 | Wayland GUI fix (XAUTHORITY migration, BackgroundGuardService) | Aug 31, 2026 | Aug 31, 2026 |
| 20.2 | AT-SPI active state bitmask decode (packed 64-bit, STATE_ACTIVE bit 1) | Aug 18, 2026 | Aug 18, 2026 |
| 20.3 | Snap Firefox AppArmor AT-SPI fix (surgical dbus receive rule) | Aug 6, 2026 | Aug 6, 2026 |
| 20.4 | Flatpak bwrap PPID chain walk in embedded Python probe | Aug 22, 2026 | Aug 22, 2026 |

---

## Goal 21: Cross-Platform — Windows Fixes
**Start:** Aug 8, 2026 | **End:** Aug 18, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 21.1 | Windows auto-install fix (tree-kill bug, detached .cmd updater) | Aug 12, 2026 | Aug 12, 2026 |
| 21.2 | Stale installer cleanup (force-delete updates/ after install) | Aug 18, 2026 | Aug 18, 2026 |
| 21.3 | Duplicate session fix (root-PID grouping, PPID chain walk) | Aug 15, 2026 | Aug 15, 2026 |
| 21.4 | Windows software detection overhaul (PE subsystem, registry, Start Menu .lnk) | Aug 8, 2026 | Aug 8, 2026 |
| 21.5 | Windows file journey (Explorer watcher, Shell COM, LocationURL) | Aug 8, 2026 | Aug 8, 2026 |
| 21.6 | Windows hardware detection (PnP polling, USBSTOR class) | Aug 8, 2026 | Aug 8, 2026 |

---

## Goal 22: Security & Auth Fixes
**Start:** Aug 19, 2026 | **End:** Sep 2, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 22.1 | Device authentication (opaque token, DeviceAuth middleware, 11 sync endpoints) | Aug 19, 2026 | Aug 19, 2026 |
| 22.2 | Clean SIGTERM handling (host.WaitForShutdownAsync, ShutdownSentinel) | Aug 31, 2026 | Aug 31, 2026 |
| 22.3 | Single-instance mutex (machine-wide Global\ namespace) | Aug 31, 2026 | Aug 31, 2026 |
| 22.4 | is_synced=0 reset on every UPDATE (8 write paths fixed) | Aug 12, 2026 | Aug 12, 2026 |
| 22.5 | Background guard watchdog + file logger (dotnetrunlog.txt) | Aug 1, 2026 | Aug 3, 2026 |

---

## Goal 23: Web — App Usage Duration Fix
**Start:** Sep 4, 2026 | **End:** Sep 4, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 23.1 | Client: AccessibilityBrowserTracker.ResolveWindowKey collapse rules (URL match, recency window) | Sep 4, 2026 | Sep 4, 2026 |
| 23.2 | Server: GET /app-sessions/usage endpoint (firstOpenedAt, lastClosedAt, totalDurationSeconds) | Sep 4, 2026 | Sep 4, 2026 |
| 23.3 | Web: useMemo recomputes duration as lastClosedAt - firstOpenedAt (anti-sum regression) | Sep 4, 2026 | Sep 4, 2026 |

---

## Goal 24: Web — Search Query Grouping
**Start:** Sep 5, 2026 | **End:** Sep 5, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 24.1 | Search engine query extraction (Google ?q=, Bing ?q=, Yahoo ?p=, DuckDuckGo ?q=) | Sep 5, 2026 | Sep 5, 2026 |
| 24.2 | Grouped search results with engine badges + expand/collapse dropdowns | Sep 5, 2026 | Sep 5, 2026 |

---

## Goal 25: GPS / Location (Coming Soon)
**Start:** Sep 1, 2026 | **End:** Sep 1, 2026

| # | Task | Start Date | End Date |
|---|------|------------|----------|
| 25.1 | Client GPS collection (ALPHA_LOCATION_ENABLED default false) | Sep 1, 2026 | Sep 1, 2026 |
| 25.2 | Server geofence backend + sync APIs | Sep 1, 2026 | Sep 1, 2026 |
| 25.3 | Web LocationComingSoon page (live code preserved, flag-gated) | Sep 1, 2026 | Sep 1, 2026 |

---

## Summary

| Goal | Tasks | Start | End |
|------|-------|-------|-----|
| 1. Project Foundation | 5 | Jul 24 | Jul 27 |
| 2. Browser Journey | 8 | Jul 28 | Aug 21 |
| 3. File Explorer Journey | 4 | Jul 29 | Aug 8 |
| 4. Software Inventory | 7 | Jul 30 | Aug 11 |
| 5. Client GUI Rebuild | 3 | Aug 8 | Aug 10 |
| 6. Sync Engine | 4 | Aug 11 | Aug 11 |
| 7. Auto-Update | 4 | Aug 12 | Aug 19 |
| 8. Session Lifecycle | 5 | Jul 31 | Sep 2 |
| 9. Time & Attendance | 6 | Aug 28 | Sep 1 |
| 10. Server Shifts/Attendance | 4 | Aug 31 | Sep 1 |
| 11. Web Employee Journey | 5 | Aug 17 | Aug 18 |
| 12. Web Device Specs | 4 | Aug 11 | Aug 18 |
| 13. Web Employees | 3 | Aug 18 | Aug 18 |
| 14. Web Configuration | 4 | Aug 20 | Aug 22 |
| 15. Dynamic RBAC | 4 | Aug 25 | Aug 25 |
| 16. Auth Refresh + Profile | 4 | Aug 25 | Aug 28 |
| 17. Dashboard Live API | 2 | Aug 25 | Aug 25 |
| 18. URL-Synced Filters | 3 | Sep 1 | Sep 2 |
| 19. Attendance & Timesheets | 3 | Aug 28 | Sep 3 |
| 20. Linux Fixes | 4 | Jul 31 | Sep 5 |
| 21. Windows Fixes | 6 | Aug 8 | Aug 18 |
| 22. Security & Auth | 5 | Aug 1 | Sep 2 |
| 23. App Usage Fix | 3 | Sep 4 | Sep 4 |
| 24. Search Query Grouping | 2 | Sep 5 | Sep 5 |
| 25. GPS/Location | 3 | Sep 1 | Sep 1 |
| **TOTAL** | **~118** | **Jul 24** | **Sep 8** |
