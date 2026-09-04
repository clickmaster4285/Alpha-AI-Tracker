# Server Architecture — Alpha AI Tracker API

> **Last audited:** 2026-09-04 (per-app usage aggregate endpoint)
> **Changelog:**
> - 2026-09-04: **`GET /api/v1/app-sessions/usage` — per-app aggregate for the web "App Usage" page.** A chrome window with 3 tabs × 10 min used to render as "30 min" because the old page summed per-row `endedAt - startedAt` across every `app_sessions` row with the same `appDisplayName`. New endpoint returns ONE row per `(app_display_name, process_name)` with `MIN(started_at)` (`firstOpenedAt`), `MAX(COALESCE(ended_at, last_sync_at, started_at))` (`lastClosedAt`), `COUNT(*)` (`sessionCount`), and `SUM(EXTRACT(EPOCH FROM (COALESCE(ended_at, last_sync_at, started_at) - started_at)))` (`totalDurationSeconds` — kept for the cross-app "Active Time" tile; the per-app Duration cell uses `lastClosedAt - firstOpenedAt`, NOT the sum, so multi-tab windows never inflate the total). Filters: `employeeId`, `search`, `platform`, `dateFrom`, `dateTo`, `page`, `perPage` — same shape as `ListAppSessions`. The 3-state lifecycle is honored via the `COALESCE(ended_at, last_sync_at, started_at)` expression (CLOSED → `ended_at`, STALE/ACTIVE → `last_sync_at` — never `NOW()`). New repo `AggregateAppSessionsUsage` + service `ListAppSessionsUsage` + handler `ListAppSessionsUsage` + DTO `AppUsageRow` / `AppUsageListResponse`. **Migration 032** adds a composite index `idx_app_sessions_employee_started_name` on `(employee_id, started_at DESC, app_display_name)` so the WHERE + GROUP BY plans without a sort. The route is registered BEFORE the existing `GET /app-sessions` in `router.go` so a future `GET /app-sessions/:id` cannot swallow it. Cross-service contract: web `lib/api.ts` exposes `AppUsageRow` + `appSessionsApi.usage(params)`; web `/employee-journey/apps` switched from a 5×100 raw-row fan-out (silent 500-row truncation bug) to a single `usage()` call and now shows "First Opened" + "Last Closed" columns so the open range is visible directly. The page also re-derives `totalDurationSeconds = lastClosedAt - firstOpenedAt` in JS (defense in depth — if a future server change reintroduces the sum, the page stays correct). Result: chrome 3 tabs × 10 min renders as **Duration: 10m, Sessions: 3**; chrome where tab1 stays open 9:00→9:12 with the others at 9:00→9:10 renders as **Duration: 12m, Sessions: 3**. Verified: `go build`/`go vet` clean, `npx tsc --noEmit` clean, `next build` passes (`/employee-journey/apps` 2.89 kB). No env knobs. Companion client-side fix lives in `client/ARCHITECTURE.md` (`ResolveWindowKey` URL-match + recency rules, ships in next installer build).
> - 2026-09-02: **3-state app_sessions lifecycle (ACTIVE / STALE / CLOSED) — orphan "Running forever" bug fixed at the schema level.**
>   Migration **031** adds 3 columns to `app_sessions` — `status TEXT NOT NULL DEFAULT 'ACTIVE'`, `last_activity_at TIMESTAMPTZ`, `last_sync_at TIMESTAMPTZ` — plus 2 indexes (`status, last_sync_at DESC` and a partial `WHERE status='ACTIVE'`) and backfills 1131 pre-existing rows. New background job `internal/jobs/session_lifecycle_sweep.go` runs every minute, transitioning `ACTIVE → STALE` (no sync for > `SESSION_STALE_AFTER_MINUTES`, default 10) and `STALE → CLOSED` (no sync for > `SESSION_CLOSE_AFTER_HOURS`, default 24). When transitioning to CLOSED, the sweep freezes `ended_at = COALESCE(last_activity_at, last_sync_at, started_at)` so duration reflects real activity. **Upsert recovery in `BulkInsertAppSessions`:** a `ON CONFLICT (id) DO UPDATE` CASE flips STALE/CLOSED back to ACTIVE and clears the premature server-side `ended_at` when a live client re-uploads the row with `ended_at=NULL`; a non-NULL `ended_at` from the client keeps the row CLOSED with the new value. `AppSession` model + DTO + list query updated. Live-tested: 1131 ACTIVE→STALE then 1131 STALE→CLOSED in 62 ms; a live `POST /api/v1/app-sessions/sync` with `ended_at=NULL` flipped a CLOSED row back to ACTIVE in one round-trip.
> - 2026-09-01: **Attendance late/present uses shift IANA timezone; legacy UTC rows auto-migrated.**
>   Shifts created before admins set a timezone stayed on migration 028's `UTC` default while
>   `session_events` carried local offsets — status math disagreed with the web table (e.g. 09:08
>   PKT displayed as on-time when compared to 09:00 UTC). New `DEFAULT_SHIFT_TIMEZONE` in
>   `config.Load()`; `ShiftRepo.ApplyDefaultTimezone` runs at boot via `ShiftService` when set;
>   `AttendanceResponse.timezone` echoes the shift zone used for `present`/`late`/`half_day`;
>   `ShiftService.Create` falls back to `DEFAULT_SHIFT_TIMEZONE` when the payload omits timezone.
>   Operators must set this to the company's wall-clock zone (e.g. `Asia/Karachi`) or edit each
>   shift's timezone on `/shifts`.
> - 2026-08-31: **Time & Attendance Phase 2 server contract.** Migration 028 adds an IANA
>   timezone to shifts, the company-holiday calendar, and aggregate-compatible
>   `session_events` fields (`event_count`, `first_at`, `last_at`). Device-authenticated
>   clients can read `GET /api/v1/schedules/me`; `GET /api/v1/server-time` is public;
>   JWT-protected admins can manage `/holidays` and read `/attendance/today` or
>   `/attendance/range`. Attendance is computed on read from session events, the latest
>   heartbeat, the assigned shift (**in `shifts.timezone` — not the admin's browser zone**), and
>   holidays; idle and lock intervals are unioned. `late` = earliest active-marker `first_at` is
>   strictly after `shift_start + grace_minutes` in that IANA zone. Response includes `timezone`
>   for web display parity.
>   The range response is paginated for web infinite scrolling. Unit tests cover the
>   lowercase weekly-pattern contract and overlapping inactive intervals.
> - 2026-08-18: **Server-side date-range filtering on app-sessions + app-items list endpoints.**
>   `GET /app-sessions` and `GET /app-items` now accept `dateFrom` and `dateTo` query parameters (RFC3339
>   or date-only like `2026-08-18`). Sessions filter on `started_at`, items on `opened_at`. Combined with
>   the existing `search`/`platform` params — all filters compose in one SQL WHERE. Zero-value/absent params
>   mean "no bound" (backward compatible). The app-items search query was also extended to match `url` and
>   `domain` columns (so searching "youtube" finds the exact page, not just the domain group).
>   New repo params (`DateFrom`, `DateTo` as `*time.Time`) in `ListAppSessionsParams` and
>   `ListAppItemsParams`; handler parses query params with `time.Parse`. Verified: `go build`/`go vet` clean,
>   filtered SQL verified live against Postgres (EMP-10002 today = 70 sessions / 100 browser pages).
> - 2026-08-11: **Employee detail aggregate endpoint** — `GET /api/v1/employees/:id/detail` (protected)
>   returns the full machine picture for one employee in a single response: `employee` (UUID-resolved,
>   then all sync-table reads keyed on the `EMP-XXXXX` id), latest `deviceHardware` + `storageDevices`
>   + `networkInfo`, currently-installed `applications`/`packages` (active junction links joined with
>   catalog rows — version/path/date from the junction, identity from the catalog), `hardwareDevices`
>   peripherals, `permissions` checks, `appStatus` key/value map and activity `stats`
>   (sessions/items/last-activity). New repo read methods in `new_schema_repo.go`, service method
>   `GetEmployeeDetail`, handler `GetEmployeeDetail`, route 31 → 32. The web dashboard consumes it
>   from the new `/users/[id]` page.
> - 2026-08-10: **Employee disconnect endpoint removed** — `POST /api/v1/auth/employee-disconnect`
>   (handler `EmployeeDisconnect`, DTO `EmployeeDisconnectRequest`, route) was deleted along with the
>   client-side Disconnect button. The web admin `POST /api/v1/auth/logout` is unrelated and
>   unchanged. Route count 31 → 30. Client consequence: no `logout` session event is emitted anymore
>   (only `login`, from `StartTracking`) and the Windows anti-sleep state now persists for the whole
>   process lifetime (the OS clears it at process exit — no leak).
> - 2026-08-01: **Docs audit** — removed stale `activity_logs` / `shell_commands` references (both dropped server-side), corrected migration inventory to 001–016 (15 files), added `jobs/staleness_sweep.go`, added `employee_app_link.go` junction models, corrected the API surface (7 sync endpoints + 2 list endpoints, no `/activity-logs`, no `/shell-commands`), and documented all 12 Postgres tables including the app/package catalogs.
> - 2026-07-31: Migrations 013–016 + catalog/link sync rewrite. 013 adds `installed_app_id`/`installed_package_id`/`grouped_by`/`cgroup_scope`/`context_label` to `app_sessions`. 014 adds `process_id` + 9 journey fields to `app_items`. 015/016 build company-global app/package catalogs (`app_fingerprint = desktop_id|binary_name`, `package_fingerprint = package_name|source_manager`) with per-employee junction tables `employee_installed_applications`/`employee_installed_packages` (version/path/install_date + first/last_seen_at + is_active) — two employees with the same app now share ONE catalog row. `SyncInstalledApps`/`SyncInstalledPackages` rewritten to upsert-catalog-then-link inside one tx. New `internal/jobs/staleness_sweep.go` hourly deactivates links idle > `LINK_STALE_DAYS` (default 7, configurable). `NewSchemaRepo` gains `Begin()` + 4 upsert methods. App-session/item bulk inserts + list queries extended with the new fields.
> - 2026-07-29: Browser/terminal/headless filtering arrived client-side only; server DTOs extended with `url`/`domain` (migration 011) and installed-application identity columns (migration 012).
> **Service completion (honest):** ~50%

---

## 1. Responsibility & Scope

**Owns:**
- All persistent data storage (PostgreSQL) and temporary auth data (Redis)
- Authentication for both web admins (email/password → httpOnly cookie) and employees (secret key → JWT)
- REST API for CRUD operations on users, employees, departments
- Ingestion from desktop clients: device hardware, installed apps, installed packages, network info, session events, app sessions, app items (bulk insert with dedup)
- App/package catalog dedup + per-employee link tracking (`employee_installed_applications` / `employee_installed_packages`)
- Company admin auto-initialization on first startup
- Migration management (runs `.sql` migration files on startup)
- Background staleness sweep (hourly, deactivates stale catalog links)

**Does NOT own:**
- Any frontend rendering (served by the Next.js web app)
- Shell command storage or API (**removed from the product** — client no longer collects or sends shell commands, migration 007 drops the legacy table)
- Activity log storage (**removed** — replaced by relational `app_sessions`/`app_items`, migration 006 drops the legacy table)
- Real-time data delivery (no WebSocket, SSE, or polling endpoints)
- Rate limiting (none implemented)

---

## 2. Tech Stack Detail

| Component | Library | Version |
|---|---|---|
| **Language** | Go | 1.25 (go.mod requires `go 1.25.0`) |
| **HTTP Framework** | github.com/labstack/echo/v4 | v4.15.4 |
| **PostgreSQL Driver** | github.com/jackc/pgx/v5 | v5.10.0 |
| **Redis Client** | github.com/redis/go-redis/v9 | v9.21.0 |
| **JWT** | github.com/golang-jwt/jwt/v5 | v5.3.1 |
| **Password Hashing** | golang.org/x/crypto (bcrypt) | v0.54.0 |
| **Env Loading** | github.com/joho/godotenv | v1.5.1 |

### Middleware Stack (in order)

1. `middleware.LoggerWithConfig` — JSON-format request logging
2. `middleware.Recover()` — panic recovery
3. `middleware.CORSWithConfig` — configurable origins with credentials
4. `appMiddleware.OptionalAuth` (semi-protected routes) — validates JWT if present, doesn't fail if absent
5. `appMiddleware.JWTAuth` (protected routes) — requires valid JWT from cookie or Authorization header

---

## 3. Project Structure

```
server/
├── cmd/server/main.go           # Entry point. Config loading, DI setup, auto-init admin, graceful shutdown, starts staleness sweep
├── Makefile                     # build, run, dev (air hot-reload), test, clean, deps, setup
├── go.mod / go.sum              # Go module: github.com/alpha-ai-tracker/server
├── .env.example                 # Environment variable template
│
├── migrations/                  # SQL migration files, run in sorted order on startup (latest: 028)
│   ├── 001_init.sql             # users, departments, employee_id_seq, triggers, seed departments
│   ├── 002_employees.sql        # Separate employees table, migrate non-admin users out of users
│   ├── 004_employee_department_id.sql  # FK employees.department_id → departments.id
│   ├── 005_soft_delete.sql      # deleted_at on users/employees/departments
│   ├── 006_new_schema.sql       # device_hardware_info, installed_applications, network_info, session_events, app_sessions + DROP legacy tables
│   ├── 007_shell_commands.sql   # DROP TABLE shell_commands CASCADE (shell tracking removed)
│   ├── 008_app_items.sql        # generic self-referencing app_items table
│   ├── 009_installed_packages.sql   # CLI tools/runtimes/libraries table
│   ├── 010_session_process_ids.sql  # process_id / parent_process_id on app_sessions
│   ├── 011_app_items_url_domain.sql # url / domain on app_items
│   ├── 012_installed_applications_identity.sql # binary_name / is_browser / desktop_id / categories
│   ├── 013_session_identity.sql # installed_app_id / installed_package_id / grouped_by / cgroup_scope / context_label
│   ├── 014_app_items_journey.sql# process_id + 9 journey fields on app_items
│   ├── 015_employee_app_links.sql   # app_fingerprint + employee_installed_applications junction
│   └── 016_employee_package_links.sql # package_fingerprint + employee_installed_packages junction
│   ├── 017_status_hardware_permission_storage.sql # app_status, hardware_devices, permission_status, storage_devices
│   ├── 018_drop_employee_role.sql / 019_drop_employee_department.sql # denormalized columns removed
│   ├── 020_app_session_focus.sql    # foreground_seconds / background_seconds on app_sessions
│   ├── 021_employee_devices.sql     # opaque device tokens for sync endpoints (DeviceAuth middleware)
│   ├── 022_sequence_retention_indexes.sql # retention-purge support indexes
│   ├── 023_monitoring_config.sql    # monitoring_types, monitoring_categories + classification columns + monitoring_sites
│   ├── 024_catalog_merge.sql        # cross-OS catalog dedup by normalized name
│   ├── 025_rbac_roles_modules.sql   # roles/modules/submodules/role_submodule_permissions; users.role_id sole source of truth
│   ├── 026_refresh_tokens.sql        # rotating web refresh-token persistence
│   ├── 027_shifts.sql               # relational shift catalog + employee assignment
│   ├── 028_time_attendance_phase2.sql # timezone, holidays, aggregate event fields
│   ├── 029_location_samples.sql    # location_samples table (Phase 1 of GPS / geofence)
│   ├── 030_geofence_zones.sql      # geofence_zones table
│   └── 031_app_sessions_status.sql # 3-state lifecycle: status + last_activity_at + last_sync_at + 2 indexes (backfill of existing rows)
│   └── 032_app_sessions_usage_index.sql # composite index (employee_id, started_at DESC, app_display_name) for /app-sessions/usage aggregation
│
└── internal/
    ├── config/config.go         # Loads env vars, builds Config struct (incl. LINK_STALE_DAYS, DEFAULT_SHIFT_TIMEZONE)
    ├── database/postgres.go     # pgxpool creation, migration runner
    ├── redis/redis.go           # Redis client wrapper (StoreSecret, ValidateSecret, DeleteSecret)
    ├── jobs/staleness_sweep.go  # Hourly background job deactivating stale employee↔catalog links
    ├── jobs/retention_sweep.go  # Hourly purge of stale app_items and ended app_sessions (RETENTION_DAYS)
    ├── jobs/session_lifecycle_sweep.go # 1-min sweep: ACTIVE→STALE→CLOSED by last_sync_at; honors SESSION_STALE_AFTER_MINUTES / SESSION_CLOSE_AFTER_HOURS
    │
    ├── models/                  # Database models (structs with db/json tags)
    │   ├── user.go              # User + UserPublic (safe for API)
    │   ├── employee.go          # Employee + EmployeePublic
    │   ├── device_hardware_info.go # DeviceHardwareInfo, InstalledApplication, NetworkInfo, InstalledPackage, SessionEvent (+ unused ShellCommand)
    │   ├── app_session.go       # AppSession + AppItem (replaces BrowserContext/FileExplorerContext/UrlRecord/UrlVisit)
    │   ├── employee_app_link.go # EmployeeInstalledApplication / EmployeeInstalledPackage junction rows
    │   ├── status_tables.go     # AppStatus, PermissionStatus rows
    │   ├── device.go            # EmployeeDevice (opaque device-token auth)
    │   └── rbac.go              # Role, Module, Submodule, RoleSubmodulePermission
    │
    ├── dto/                     # Request/Response DTOs
    │   ├── user_dto.go          # LoginRequest, CreateUserRequest (roleId), UserResponse (+ permissions), etc.
    │   ├── employee_dto.go      # EmployeeLoginRequest, GenerateSecretResponse, etc.
    │   ├── new_schema_dto.go    # Sync DTOs for all 7 tables + AppSessionListResponse + AppItemListResponse
    │                             # (still contains unused legacy DTOs: BrowserContext/FileExplorerContext/Url/UrlVisit/ShellCommand)
    │   └── rbac_dto.go          # ModuleTreeResponse, RoleResponse (submoduleIds + permissions keys), role CRUD payloads
    │
    ├── repository/              # Data access layer (raw SQL via pgx)
    │   ├── user_repo.go         # User CRUD (roles JOIN; qualified u.* columns), CountUsersWithRole, IsUniqueEmail
    │   ├── employee_repo.go     # Employee CRUD, GenerateEmployeeID, GetDepartments
    │   ├── department_repo.go   # Department CRUD with employee count (LEFT JOIN)
    │   ├── new_schema_repo.go   # Bulk inserts for 7 tables, ListAppSessions/ListAppItems, Begin() tx + catalog-upsert methods
    │   ├── device_repo.go       # UpsertDevice / ListByEmployeeID / RevokeDevice (employee_devices)
    │   ├── monitoring_repo.go   # Types/categories CRUD, classified apps/sites listing + SyncWebsiteDomains
    │   └── rbac_repo.go         # Module/submodule catalog, roles CRUD, grants replace, PermissionKeysForUser, SeedCatalog queries
    │
    ├── services/                # Business logic layer
    │   ├── auth_service.go      # Login, token generation/validation, EnsureCompanyAdmin, attachPermissions
    │   ├── user_service.go      # User CRUD with email uniqueness checks
    │   ├── employee_service.go  # Employee CRUD, GenerateSecret (Redis)
    │   ├── department_service.go # Department CRUD
    │   ├── new_schema_service.go # Sync handlers for 7 tables (catalog-upsert for apps/packages) + 2 list queries
    │   ├── monitoring_service.go # Monitoring configuration business rules (409 type-in-use guard etc.)
    │   ├── rbac_service.go      # SeedCatalog (idempotent module/submodule/grant seeding) + roles CRUD orchestration
    │   └── redis_interface.go   # Interface for Redis operations (decouples auth handler)
    │
    ├── handlers/                # HTTP handlers (Echo context)
    │   ├── auth_handler.go      # Login, Logout, Me, CheckAuth, EmployeeLogin, device listing/revocation
    │   ├── user_handler.go      # List, Get, Create, Update, Delete users
    │   ├── employee_handler.go  # List, Get, Create, Update, Delete, GenerateSecret, Import/Export
    │   ├── department_handler.go # List, Create, Update, Delete departments
    │   ├── new_schema_handler.go # 11 sync endpoints + ListAppSessions + ListAppItems
    │   ├── monitoring_handler.go # Types/categories/apps/websites configuration endpoints
    │   └── rbac_handler.go      # ListModules, roles List/Create/Update/Delete
    │
    ├── middleware/auth.go       # JWTAuth (required), OptionalAuth (optional)
    ├── middleware/device_auth.go # Device <token> auth for the 11 sync endpoints
    │
    └── router/router.go         # Route definitions grouped by auth level
```

### Architecture Layering

```
┌──────────────┐
│   Router     │  Route definitions, middleware application
├──────────────┤
│  Handlers    │  HTTP concerns: parse request, call service, return response
├──────────────┤
│  Services    │  Business logic, validation, orchestration
├──────────────┤
│ Repositories │  Data access: SQL queries, transactions
├──────────────┤
│  PostgreSQL  │  Persistent storage
└──────────────┘
```

---

## 4. API Surface

All endpoints are under `/api/v1`. Full route inventory (~46 routes):

### Health

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/health` | None | Returns `{status: "ok"}` — **bug: timestamp uses client IP instead of actual time** |

### Auth (Public)

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/auth/login` | None | Web admin login. Sets `auth_token` (15-min access) + `refresh_token` (30-day) httpOnly cookies. |
| POST | `/auth/refresh` | Refresh cookie | Validates + ROTATES the refresh token, re-mints both cookies. 401 ⇒ session unrecoverable. |
| POST | `/auth/employee-login` | None | Employee desktop login. Returns long-lived JWT + device token. |

### Auth (Semi-Protected — validates token if present)

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/auth/check` | Optional | Returns `{authenticated, user?}` |

### Auth (Protected)

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/auth/me` | Required | Returns current user profile |
| GET | `/auth/profile` | Required | Aggregate profile payload (user + role + RBAC module breakdown + linked employee) for `/settings/profile` — single-shot, no client-side fan-out |
| POST | `/auth/logout` | Required | Clears httpOnly cookie |

### Users (Protected)

| Method | Path | Purpose |
|---|---|---|
| GET | `/users` | List users (paginated, filterable) |
| GET | `/users/:id` | Get user by ID |
| POST | `/users` | Create user |
| PUT | `/users/:id` | Update user |
| DELETE | `/users/:id` | Soft-delete user |

### Employees (Protected)

| Method | Path | Purpose |
|---|---|---|
| GET | `/employees` | List employees (paginated, filterable, JOIN department; every row projects `hasUserLogin` — see below) |
| GET | `/employees/:id` | Get employee by ID (with `hasUserLogin`) |
| GET | `/employees/:id/detail` | Aggregate machine picture (hardware, storage, network, apps, packages, peripherals, permissions, stats) |
| POST | `/employees` | Create employee (RETURNING includes `hasUserLogin`) |
| PUT | `/employees/:id` | Update employee (RETURNING includes `hasUserLogin`) |
| DELETE | `/employees/:id` | Soft-delete employee |
| POST | `/employees/:id/generate-secret` | Generate one-time secret → Redis (5-min TTL) |

> **`hasUserLogin` projection (2026-08-28).** Every employee SELECT path
> (`List`, `GetByID`, `GetByEmployeeID`, `GetByEmail`, `ListAll`, `Create` and `Update`
> `RETURNING`, and the `scanEmployeeRow` helper) adds
> `EXISTS(SELECT 1 FROM users u WHERE u.employee_id = e.employee_id AND u.deleted_at IS NULL) AS has_user_login`
> to the projection. The probe runs against the UNIQUE `users.employee_id` index
> — it does **not** scan the `users` table. The cost is one indexed lookup per
> page row (10/page by default), so it stays O(1) regardless of the total
> employee or user count. The web employees page uses this flag to hide the
> "Login Credential" dropdown item for employees who already have a login
> account, and the employees `updateMutation` uses `updated.hasUserLogin` from
> `PUT /employees/:id` to decide whether to propagate the name/email change to
> the linked user — no extra round-trips, no client-side maps.

### Departments (Protected)

| Method | Path | Purpose |
|---|---|---|
| GET | `/departments` | List with employee count |
| POST | `/departments` | Create |
| PUT | `/departments/:id` | Rename |
| DELETE | `/departments/:id` | Soft-delete |

### Monitoring Configuration (Protected — web admin reads)

Classification of the detected app catalog and observed website domains. Types are the Productive/Unproductive/Neutral-style references; categories are scoped by kind (`application` | `website` | `both`). `PATCH` classification bodies use nullable presence — `{"typeId": n}` sets, `{"typeId": null}` clears, an absent key leaves the value untouched.

| Method | Path | Purpose |
|---|---|---|
| GET | `/monitoring/types` | List types (seeded + custom) |
| POST | `/monitoring/types` | Create type |
| PUT | `/monitoring/types/:id` | Update type |
| DELETE | `/monitoring/types/:id` | Soft-delete (409 while any app/site references it) |
| GET | `/monitoring/categories?kind=` | List categories, optionally by kind |
| POST | `/monitoring/categories` | Create category |
| PUT | `/monitoring/categories/:id` | Update category |
| DELETE | `/monitoring/categories/:id` | Soft-delete (classifications detach via ON DELETE SET NULL) |
| GET | `/monitoring/apps` | List classified catalog apps (`search`, `typeId`, `categoryId`, `unclassified`, `page`, `perPage`) |
| PATCH | `/monitoring/apps/:id` | Set/clear an app's type + category |
| GET | `/monitoring/websites` | List classified observed domains (auto-syncs new domains from `app_items` first; same filters) |
| PATCH | `/monitoring/websites/:id` | Set/clear a site's type + category |

### RBAC (Protected — web admin manages)

Dynamic role-based access control backed by migration 025 (`roles`, `modules`, `submodules`,
`role_submodule_permissions`; `users.role_id`). The module/submodule catalog + the SYSTEM
`company_admin` role's grants are re-seeded idempotently on every boot (`RBACService.SeedCatalog`).
A granted `(role, submodule)` pair means "allowed"; submodule keys are what the web client receives
as `user.permissions`.

| Method | Path | Purpose |
|---|---|---|
| GET | `/modules` | Full module→submodule tree (the permission catalog for the roles UI + nav guards) |
| GET | `/roles` | List roles with `userCount`, granted `submoduleIds` and derived `permissions` keys |
| POST | `/roles` | Create role `{name, description?, submoduleIds?}` |
| PUT | `/roles/:id` | Partial update — nil fields untouched; `submoduleIds` present ⇒ grants replaced wholesale |
| DELETE | `/roles/:id` | Soft-delete; SYSTEM roles and roles with assigned users are rejected |

Users carry `roleId` on create/update; every user-bearing response (`login`, `/auth/me`,
`/auth/check`, users CRUD) embeds the resolved permission keys via
`RBACRepo.PermissionKeysForUser`. ⚠️ These permissions are NOT enforced by any middleware yet —
they only drive the web client's sidebar/route gating.

### Client Ingestion (Public — JWT in body)

Employee token is carried in the request body (`{employeeId, token, entries: [...]}`), not a cookie. Server validates the token and returns `SyncBatchResponse{accepted, rejected}`.

| Method | Path | Table | Notes |
|---|---|---|---|
| POST | `/device-hardware/sync` | `device_hardware_info` | Bulk upsert |
| POST | `/installed-apps/sync` | `installed_applications` + `employee_installed_applications` | Upsert catalog-then-link in ONE tx |
| POST | `/installed-packages/sync` | `installed_packages` + `employee_installed_packages` | Upsert catalog-then-link in ONE tx |
| POST | `/network-info/sync` | `network_info` | Bulk upsert |
| POST | `/session-events/sync` | `session_events` | Bulk upsert; accepts optional count/firstAt/lastAt aggregates |
| GET | `/schedules/me` | `shifts`, `company_holidays` | Device-auth schedule mirror |
| POST | `/app-sessions/sync` | `app_sessions` | Bulk upsert |
| POST | `/app-items/sync` | `app_items` | Bulk upsert |
| POST | `/app-status/sync` | `app_status` | Upsert by (employee_id, key) |
| POST | `/hardware-devices/sync` | `hardware_devices` | Bulk upsert |
| POST | `/permission-status/sync` | `permission_status` | Bulk upsert by check_id |
| POST | `/storage-devices/sync` | `storage_devices` | Bulk upsert |

### App Sessions & App Items (Protected — web admin reads)

| Method | Path | Purpose |
|---|---|---|
| GET | `/app-sessions/usage` | Per-app aggregate (paginated, filterable: `search`, `platform`, `dateFrom`, `dateTo`, `employeeId`). One row per `(appDisplayName, processName)` with `firstOpenedAt`, `lastClosedAt`, `sessionCount`, `totalDurationSeconds`. Powers the web `/employee-journey/apps` page; page renders `lastClosed - firstOpened` so multi-tab windows never inflate the per-app total. Registered BEFORE `/app-sessions` (since 2026-09-04). |
| GET | `/app-sessions` | List sessions (paginated, filterable: `search`, `platform`, `dateFrom`, `dateTo`, `employeeId`). Each row carries `status` (`ACTIVE`/`STALE`/`CLOSED`), `lastActivityAt`, `lastSyncAt` (3-state lifecycle — see below) |
| GET | `/app-items` | List items (paginated, filterable: `search` matches title/identifier/url/domain, `dateFrom`, `dateTo`, `itemType`, `session`) |

> **3-state app_sessions lifecycle (migration 031 + `jobs/session_lifecycle_sweep.go`).** Every `app_sessions` row carries a server-projected `status` field (`ACTIVE` / `STALE` / `CLOSED`) plus `lastActivityAt` and `lastSyncAt` timestamps. A 1-minute background sweeper promotes `ACTIVE → STALE` when no sync has been received for `SESSION_STALE_AFTER_MINUTES` (default 10), then `STALE → CLOSED` when no sync has been received for `SESSION_CLOSE_AFTER_HOURS` (default 24). Only CLOSED is terminal. The sweep freezes `ended_at = COALESCE(last_activity_at, last_sync_at, started_at)` at the moment of CLOSE so the duration reflects real activity. **A live client re-uploading a STALE/CLOSED row with `ended_at=NULL` flips it back to ACTIVE** in the upsert (the `ON CONFLICT` CASE in `BulkInsertAppSessions`) and clears the premature server-side `ended_at` — so a network outage never destroys information that may still exist on the client. When the client supplies a non-NULL `ended_at`, the row stays CLOSED with the new value. Pre-031 rows default `status='ACTIVE'`; the web's `sessionStatus()` helper falls back to the legacy `endedAt ? CLOSED : ACTIVE` interpretation for forward compatibility.

### Employee Detail (Protected)

| Method | Path | Purpose |
|---|---|---|
| GET | `/employees/:id/detail` | **Aggregate machine picture** for one employee: employee record, latest `device_hardware_info`, `storage_devices`, latest `network_info`, currently-installed apps/packages (active junction links), `hardware_devices` peripherals, `permission_status`, `app_status` map and activity stats. Consumed by the web `/users/[id]` page. |

### Time & Attendance (Protected — web admin reads)

| Method | Path | Purpose |
|---|---|---|
| GET | `/attendance/today?employeeId=` | Today's row: `timezone`, `firstActiveAt`, `lastActiveAt`, `status`, `lateMinutes`, active/idle/off-shift seconds |
| GET | `/attendance/range?employeeId=&from=&to=&page=&perPage=` | Paginated daily rows (timesheets; infinite scroll on web) |
| GET/POST/PUT/DELETE | `/holidays` | Company holiday calendar CRUD |
| GET/POST/PUT/DELETE | `/shifts` | Shift catalog CRUD — each shift carries an IANA `timezone` |

> **Shift timezone rule (2026-09-01).** Late/present math runs in `shifts.timezone`, not the admin
> browser's local zone. Legacy rows still on `UTC` are rewritten at boot when
> `DEFAULT_SHIFT_TIMEZONE` is set in `.env`. The web shift form defaults new shifts to the admin
> browser's IANA zone.

### Missing Endpoints (sync-only tables with no standalone listing API)

| Expected Endpoint | Purpose | Status |
|---|---|---|
| `GET /installed-apps` | List installed apps (all employees) | ❌ Sync only — per-employee view available via `/employees/:id/detail` |
| `GET /installed-packages` | List installed packages (all employees) | ❌ Sync only — per-employee view via `/employees/:id/detail` |
| `GET /device-hardware` | List device hardware (all employees) | ❌ Sync only — latest-per-employee via `/employees/:id/detail` |
| `GET /network-info` | List network info (all employees) | ❌ Sync only — latest-per-employee via `/employees/:id/detail` |
| `GET /session-events` | List session events | ❌ Sync only |

---

## 5. Database Schema

### Tables (16 tracked tables + RBAC/monitoring/status tables + `schema_migrations`)

> 2026-08-11: +4 sync tables — `app_status` (key/value per employee), `hardware_devices`
> (USB/peripheral hotplug), `permission_status` (one row per permission method), and
> `storage_devices` (children of `device_hardware_info`). Migration 017.
>
> 2026-08-25: migration 025 adds the RBAC cluster (`roles`, `modules`, `submodules`,
> `role_submodule_permissions`) and drops the legacy `users.role` / `users.department` /
> `users.is_company_admin` columns — **`users.role_id` is now the sole role source of truth**.

**`users`** — Web admin users (role via role_id)
```
id              UUID PK (gen_random_uuid())
employee_id     VARCHAR(20) UNIQUE (EMP-XXXXX)
name            VARCHAR(255) NOT NULL
email           VARCHAR(255) UNIQUE NOT NULL
password_hash   VARCHAR(255) NOT NULL (bcrypt)
role_id         INTEGER → FK → roles(id)          ← migration 025; legacy role/is_company_admin dropped
shift           VARCHAR(20) DEFAULT 'Day'
tracking_enabled BOOLEAN DEFAULT true
tracking_status VARCHAR(20) DEFAULT 'untracked'
is_online       BOOLEAN DEFAULT false
avatar          VARCHAR(10) NULL (auto-generated initials)
avatar_color    VARCHAR(10) NULL (random from palette)
created_at / updated_at / deleted_at TIMESTAMPTZ (updated_at auto-trigger)
```

**`roles`** — RBAC role reference (migration 025)
```
id              SERIAL PK
name            VARCHAR(100) UNIQUE ('company_admin' seeded as SYSTEM, is_system=true)
description     TEXT
is_system       BOOLEAN (system roles cannot be deleted)
created_at / updated_at / deleted_at TIMESTAMPTZ
```

**`modules`** — Navigation module groups (General, HR, Monitoring, Settings, …)
```
id              SERIAL PK
key             VARCHAR(100) UNIQUE (permission module key)
name            VARCHAR(100) NOT NULL
sort_order      INTEGER DEFAULT 0
created_at / updated_at TIMESTAMPTZ (updated_at auto-trigger; no soft delete — catalog is seeded code-side)
```

**`submodules`** — Concrete permission keys under a module (carry route_path for nav guards)
```
id              SERIAL PK
module_id       INTEGER → FK → modules(id) ON DELETE CASCADE
key             VARCHAR(100) UNIQUE  ← what ships to the client as user.permissions entries
name            VARCHAR(100) NOT NULL
route_path      VARCHAR(200) (web route the key guards)
sort_order      INTEGER DEFAULT 0
created_at / updated_at TIMESTAMPTZ
```

**`role_submodule_permissions`** — Junction: a granted (role, submodule) pair means "allowed"
```
role_id         INTEGER → FK → roles(id) ON DELETE CASCADE
submodule_id    INTEGER → FK → submodules(id) ON DELETE CASCADE
PRIMARY KEY (role_id, submodule_id)
```

**`refresh_tokens`** — Web-admin rotating refresh tokens, hashed at rest (migration 026)
```
id              BIGSERIAL PK
user_id         UUID → FK → users(id) ON DELETE CASCADE
token_hash      VARCHAR(64) UNIQUE NOT NULL   ← hex(sha256(raw_token)); raw value lives ONLY in the cookie
expires_at      TIMESTAMPTZ NOT NULL          ← JWT_REFRESH_EXPIRY (default 30d) from creation
revoked_at      TIMESTAMPTZ                   ← set on rotation or logout; NULL = live
created_at / deleted_at TIMESTAMPTZ
```

**`departments`** — Reference table for employee departments
```
id              SERIAL PK
name            VARCHAR(100) UNIQUE NOT NULL
deleted_at      TIMESTAMPTZ (soft delete)
```

**`employees`** — Tracked employees (desktop client users)
```
id              UUID PK (gen_random_uuid())
employee_id     VARCHAR(20) UNIQUE (EMP-XXXXX)
name            VARCHAR(255) NOT NULL
email           VARCHAR(255) NOT NULL DEFAULT ''
department      VARCHAR(100) DEFAULT 'Engineering'
department_id   INTEGER NOT NULL DEFAULT 1 → FK → departments(id)
role            VARCHAR(50) DEFAULT 'employee'
shift           VARCHAR(20) DEFAULT 'Day'
tracking_enabled BOOLEAN DEFAULT true
tracking_status VARCHAR(20) DEFAULT 'untracked'
is_online       BOOLEAN DEFAULT false
avatar          VARCHAR(10)
avatar_color    VARCHAR(10)
created_at / updated_at / deleted_at TIMESTAMPTZ
```

**`device_hardware_info`** — Hardware snapshot per employee (migration 006)
```
id              TEXT PK (client-generated GUID)
employee_id     VARCHAR(20) NOT NULL → FK → employees(employee_id)
device_id       TEXT NOT NULL DEFAULT ''
mac_address     TEXT NOT NULL DEFAULT ''
hostname        TEXT NOT NULL DEFAULT ''
os_name         TEXT NOT NULL DEFAULT ''
os_version      TEXT NOT NULL DEFAULT ''
cpu_model       TEXT NOT NULL DEFAULT ''
cpu_cores       INTEGER NOT NULL DEFAULT 0
ram_total_mb    BIGINT NOT NULL DEFAULT 0
storage_devices JSONB NOT NULL DEFAULT '[]'
gpu_model       TEXT NOT NULL DEFAULT ''
gpu_vram_mb     BIGINT NOT NULL DEFAULT 0
collected_at / synced_at / created_at / deleted_at TIMESTAMPTZ
```

**`installed_applications`** — Company-global GUI app catalog (migrations 006, 012, 015)
```
id              TEXT PK (client-generated GUID)
employee_id     VARCHAR(20) NOT NULL → FK → employees(employee_id)   -- "owner" who first synced it (legacy)
app_name        TEXT NOT NULL
app_version     TEXT NOT NULL DEFAULT ''
publisher       TEXT NOT NULL DEFAULT ''
install_path    TEXT NOT NULL DEFAULT ''
install_date    TIMESTAMPTZ
uninstall_string TEXT NOT NULL DEFAULT ''
change_type     TEXT NOT NULL DEFAULT 'seen'
detected_at / synced_at / created_at / deleted_at TIMESTAMPTZ
binary_name     TEXT NOT NULL DEFAULT ''   (012)
is_browser      BOOLEAN NOT NULL DEFAULT false (012)
desktop_id      TEXT NOT NULL DEFAULT ''   (012)
categories      TEXT NOT NULL DEFAULT ''   (012)
app_fingerprint TEXT UNIQUE                (015)  = desktop_id|binary_name
```
Per-install metadata (version/path/date/publisher) now lives on `employee_installed_applications`, so the catalog row is shared across employees. The legacy `employee_id` column is retained for backward compat but is no longer the dedup key.

**`employee_installed_applications`** — employee↔app junction (migration 015)
```
id                      BIGSERIAL PK
employee_id             VARCHAR(20) NOT NULL → FK → employees(employee_id)
installed_application_id TEXT NOT NULL → FK → installed_applications(id)
app_version / publisher / install_path / install_date
first_seen_at / last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now()
is_active               BOOLEAN NOT NULL DEFAULT true
UNIQUE (employee_id, installed_application_id)
```

**`network_info`** — Network snapshots per employee (migration 006)
```
id TEXT PK · employee_id FK · public_ip · private_ip · mac_address
network_interface_name · collected_at / synced_at / created_at / deleted_at
```

**`session_events`** — Power/login/lock/idle telemetry (migrations 006, 028).
```
id TEXT PK · employee_id FK · event_type · os_username
event_at · event_count · first_at · last_at / synced_at / created_at / deleted_at
```

**`app_sessions`** — Relational app sessions (migrations 006, 010, 013)
```
id               TEXT PK (client-generated GUID)
employee_id      VARCHAR(20) NOT NULL → FK → employees(employee_id)
process_name     TEXT NOT NULL
app_display_name TEXT NOT NULL DEFAULT ''
started_at / ended_at TIMESTAMPTZ
machine_id       TEXT NOT NULL DEFAULT ''
session_id       TEXT NOT NULL DEFAULT ''
platform         TEXT NOT NULL DEFAULT ''
synced_at / created_at / deleted_at TIMESTAMPTZ
process_id       INTEGER          (010)
parent_process_id INTEGER         (010)
installed_app_id  TEXT            (013)  -- no FK: may reference an unsynced client row
installed_package_id TEXT         (013)
grouped_by       TEXT             (013)  -- 'cgroup' | 'pid' | NULL
cgroup_scope     TEXT             (013)  -- systemd app-*.scope
context_label    TEXT             (013)  -- VS Code workspace / Chrome profile
```

**`app_items`** — Generic self-referencing child of app_sessions (migrations 008, 010, 011, 014)
```
id                TEXT PK
employee_id       VARCHAR(20) NOT NULL → FK → employees(employee_id)
app_session_id    TEXT NOT NULL → FK → app_sessions(id)
parent_item_id    TEXT → FK → app_items(id)
item_type         TEXT NOT NULL DEFAULT ''   ('tab', 'browser_tab', 'browser_navigation', 'terminal', 'folder', 'file')
title / identifier TEXT NOT NULL DEFAULT ''
url / domain      TEXT NOT NULL DEFAULT ''   (011)
opened_at / closed_at TIMESTAMPTZ
process_id        INTEGER          (014)
object_type / action / journey_id / previous_path / current_path / metadata_json (014)
sequence          INTEGER NOT NULL DEFAULT 0 (014)
window_id / tab_id INTEGER        (014)
synced_at / created_at / deleted_at TIMESTAMPTZ
```

**`installed_packages`** — Company-global CLI tool/runtime/library catalog (migrations 009, 016)
```
id               TEXT PK
employee_id      VARCHAR(20) NOT NULL → FK → employees(employee_id)  -- legacy owner column
package_name     TEXT NOT NULL
version          TEXT NOT NULL DEFAULT ''
category         TEXT NOT NULL DEFAULT 'tool'
source_manager   TEXT NOT NULL DEFAULT ''
install_path / publisher / description TEXT NOT NULL DEFAULT ''
detected_at / synced_at / created_at / deleted_at TIMESTAMPTZ
package_fingerprint TEXT UNIQUE (016)  = package_name|source_manager
```

**`employee_installed_packages`** — employee↔package junction (migration 016)
```
id BIGSERIAL PK · employee_id FK · installed_package_id FK
version / publisher / install_path
first_seen_at / last_seen_at · is_active BOOLEAN
UNIQUE (employee_id, installed_package_id)
```

**`schema_migrations`** — Migration tracking
```
filename        VARCHAR(255) PK
applied_at      TIMESTAMPTZ DEFAULT NOW()
```

### Removed Tables

| Table | Migration | Status |
|---|---|---|
| `activity_logs` | 006 (DROP CASCADE) | Replaced by `app_sessions`/`app_items` |
| `browser_contexts` | 006 (DROP CASCADE) | Replaced by `app_items` |
| `file_explorer_contexts` | 006 (DROP CASCADE) | Replaced by `app_items` |
| `urls` / `url_visits` | 006 (DROP CASCADE) | Replaced by `app_items` |
| `shell_commands` | 007 (DROP CASCADE) | Shell tracking removed from product |

### Indexes

- All tables have `deleted_at` partial indexes for soft-delete filtering
- app_sessions: `(employee_id, started_at DESC)`, `(started_at DESC)`, `(process_id)`, `(parent_process_id)`, `(employee_id, started_at)`
- app_items: `(employee_id, opened_at DESC)`, `(app_session_id)`, `(parent_item_id)`, `(employee_id, item_type, identifier)`, `(journey_id, sequence)`, `(url)`, `(domain)`
- installed_applications: `app_fingerprint` UNIQUE, `binary_name`, `(employee_id, detected_at DESC)`, `app_name`
- installed_packages: `package_fingerprint` UNIQUE, `source_manager`, `category`, `(employee_id, detected_at DESC)`
- refresh_tokens: `(user_id)`, `(expires_at)`

### Migration Tool

**Custom runner** in `database/postgres.go`. Reads all `.sql` files from `migrations/` in filename order (latest: 028), tracks applied migrations in `schema_migrations`, and runs each file in its own transaction.

---

## 6. Auth & Security Model

### Web Admin Auth Flow

1. Admin submits email + password to `POST /api/v1/auth/login`
2. Server looks up user by email, compares bcrypt hash
3. Valid: generates access JWT with claims `{userId}`, signs with HMAC-SHA256, then **encrypts the signed JWT with AES-256-GCM** using a key derived from JWT secret
4. Two httpOnly cookies are set:
   - `auth_token` — the encrypted access JWT, **short-lived** (`JWT_ACCESS_EXPIRY`, default **15m**)
   - `refresh_token` — an opaque 32-byte random value (base64url); only its SHA-256 hash is stored in `refresh_tokens` with `expires_at = now + JWT_REFRESH_EXPIRY` (default **30 days**)
5. Subsequent requests: `JWTAuth` middleware reads `auth_token`, decrypts the AES layer, then validates the JWT signature
6. When the access token expires, any API call gets 401 → the web client calls `POST /auth/refresh`: server validates the presented hash (not revoked/expired/deleted), loads + re-authenticates the user, mints a NEW access JWT AND a replacement refresh token (**rotation** — old row revoked after the replacement insert succeeds, so a mid-refresh crash can never lock the user out), and re-mints both cookies
7. A 401 from `/auth/refresh` itself means the session is gone (refresh expired/revoked/user deleted) — the handler also clears both cookies and the web client force-redirects to `/login`
8. `POST /auth/logout` revokes the presented refresh row (it can never mint another token) and clears both cookies
9. **Double encryption rationale**: protects against JWT secret leakage from log files (JWT is AES-encrypted until the middleware decrypts it)

### Employee Auth Flow

1. Admin generates a one-time secret via `POST /api/v1/employees/:id/generate-secret`
2. Server stores secret in Redis with key `employee_secret:<emp_id>` and 5-min TTL
3. Employee enters emp_id + secret in desktop client
4. Client sends `POST /api/v1/auth/employee-login` with `{employeeId, secretKey}`
5. Server validates secret from Redis, deletes it (one-time use)
6. Returns an encrypted JWT (same mechanism as admin, but issuer `alpha-ai-tracker-employee`) with its OWN TTL (`JWT_EMPLOYEE_ACCESS_EXPIRY`, default 24h)
7. ⚠️ Desktop clients have NO refresh mechanism — they embed this one token in every sync request body until the process stops. Never lower this TTL to the web-admin value.
8. Additionally returns a non-expiring opaque device token (`dev_tok_…`, hash stored in `employee_devices`) used by the DeviceAuth middleware on all 11 sync endpoints

### Token Security

| Aspect | Implementation |
|---|---|
| **Signing** | HMAC-SHA256 via `golang-jwt/jwt/v5` |
| **Encryption** | AES-256-GCM, key = SHA256(JWT_SECRET) |
| **Web-admin access expiry** | `JWT_ACCESS_EXPIRY`, default **15m** (`auth_token` cookie) |
| **Web-admin refresh expiry** | `JWT_REFRESH_EXPIRY`, default **720h / 30 days** (`refresh_token` cookie, rotated on every refresh) |
| **Employee token expiry** | `JWT_EMPLOYEE_ACCESS_EXPIRY`, default 24h (body-carried, no refresh) |
| **Cookies** | httpOnly, SameSite=Lax, Secure=false in dev |
| **Refresh storage** | `refresh_tokens` table — SHA-256 hex hash UNIQUE, `revoked_at` on rotate/logout, FK → users ON DELETE CASCADE |
| **Employee token** | Returned in response body, stored locally by client |

### Missing Security Controls

- **No rate limiting** on login/refresh or any endpoint
- **No CSRF protection** (SameSite=Lax provides partial coverage)
- **No brute-force protection** for login attempts
- **Refresh-token reuse detection not implemented** — a revoked-but-still-presented token should ideally revoke the whole family; today rotation simply fails the stale token
- **Expired `refresh_tokens` rows are not auto-purged yet** (one row per login/rotation; retention hook exists as `RefreshTokenRepo.DeleteExpired`)
- **No audit log** of who performed what action
- **No permission model** on the server — any authenticated user can access all endpoints

---

## 7. Client Ingestion Path

All 7 sync endpoints share the same flow — the app-sessions path shown here is representative:

```
┌──────────┐     POST /api/v1/app-sessions/sync    ┌──────────────┐
│  Client   │───────────────────────────────────────▶│  Handler      │
│  (C#)     │  {employeeId, token, entries: [...]}   │  SyncAppSessions() │
└──────────┘                                         └──────┬───────┘
                                                             │
                                                             ▼
                                                    ┌──────────────┐
                                                    │  Auth Check   │
                                                    │  ValidateToken│
                                                    │  (encrypted   │
                                                    │   JWT →       │
                                                    │   signed JWT) │
                                                    └──────┬───────┘
                                                             │ valid
                                                             ▼
                                                    ┌──────────────┐
                                                    │  Service      │
                                                    │  SyncAppSessions() │
                                                    │  - Verify emp │
                                                    │  - Parse ts   │
                                                    │  - BulkInsert │
                                                    └──────┬───────┘
                                                             │
                                                             ▼
                                                    ┌──────────────┐
                                                    │  Repository   │
                                                    │  BulkInsert() │
                                                    │  - Batch of   │
                                                    │    500        │
                                                    │  - ON CONFLICT│
                                                    │    DO NOTHING │
                                                    └──────┬───────┘
                                                             │
                                                             ▼
                                                    ┌──────────────┐
                                                    │  PostgreSQL   │
                                                    │  app_sessions │
                                                    └──────────────┘
```

> **Special case — installed apps/packages:** `SyncInstalledApps`/`SyncInstalledPackages` use a two-step tx: (1) upsert the catalog row by fingerprint (`UpsertApplicationCatalog`/`UpsertPackageCatalog`), (2) upsert the employee junction link with per-install metadata (`UpsertEmployeeAppLink`/`UpsertEmployeePackageLink`) — sharing ONE catalog row across employees with the same app.

### Validation

- **Token**: decrypted and JWT-validated in the handler
- **Employee**: looked up by `employeeId` from the decoded JWT — **but the handler also accepts `employeeId` from the request body and verifies the employee exists. The JWT userId and the body employeeId are NOT cross-checked** — meaning an employee with a valid token could send data under a different `employeeId`
- **Timestamps**: parsed as RFC3339, falls back to `time.Now()` on failure
- **Data**: no sanitization or length limits on process names, window titles, URLs

---

## 8. Web-Facing Read Path

```
┌──────────┐     GET /api/v1/app-sessions             ┌──────────────┐
│  Web App  │─────────────────────────────────────────▶│  Handler      │
│  (Next.js)│  ?employeeId=&search=&page=&perPage=...   │  ListAppSessions() │
└──────────┘                                           └──────┬───────┘
                                                               │
                                                               ▼
                                                     ┌──────────────┐
                                                     │  Repository   │
                                                     │  List()       │
                                                     │  - Dynamic    │
                                                     │    SQL build  │
                                                     │  - Manual     │
                                                     │    row scan   │
                                                     │  - Pagination │
                                                     └──────┬───────┘
                                                               │
                                                               ▼
                                                     ┌──────────────┐
                                                     │  PostgreSQL   │
                                                     └──────────────┘
```

The same pattern applies to `GET /app-items`. The web dashboard's Logs/Comprehensive page queries `app-sessions` (there is no `activity-logs` API anymore).

### Caching Layer

**None.** Every request hits PostgreSQL directly. No Redis caching, no in-memory cache, no CDN.

### Pagination Approach

- Page/perPage query parameters
- Default: page=1, perPage=10 (employees), perPage=20 (app sessions / app items)
- Returns `{data, total, page, perPage, totalPages}`
- SQL: `SELECT ... LIMIT $n OFFSET $m` with a separate `SELECT COUNT(*)` for total

---

## 9. Scalability Considerations

### Statelessness

The server is **mostly stateless**:
- JWT validation requires only the shared `JWT_SECRET` — no session store
- Employee secrets are in Redis (external), so any instance can validate
- **Bottleneck**: PostgreSQL connection pool (25 max open connections per instance by default)

### Can It Run Multiple Instances?

**Yes**, with caveats:
- Employee secrets: Redis is external, so any instance can validate
- Ingestion: no ordering requirement, so round-robin works
- **Sequences**: `employee_id_seq` uses `NEXTVAL` — safe across instances
- **No sticky sessions required** — cookie-based auth is stateless

### Connection Pooling

- pgxpool configured in `database/postgres.go`
- Default: MaxConns=25, MinConns=10, MaxConnLifetime=5min
- These are hardcoded in config with env overrides (`DB_MAX_OPEN_CONNS`, etc.)

### Background Jobs

**Jobs started in `main.go`:**
- `jobs/staleness_sweep.go` — hourly goroutine that sets `is_active = false` on `employee_installed_applications` / `employee_installed_packages` rows whose `last_seen_at` is older than `LINK_STALE_DAYS` (default 7). Link-lifecycle only, NOT data pruning.
- `ShiftService.ApplyDefaultTimezone` — runs once at boot when `DEFAULT_SHIFT_TIMEZONE` is set; updates all non-deleted shifts whose `timezone` is still `UTC` to that IANA value (idempotent).

**Environment (attendance-related):**

| Variable | Default | Purpose |
|---|---|---|
| `LINK_STALE_DAYS` | `7` | Catalog junction staleness window |
| `DEFAULT_SHIFT_TIMEZONE` | *(empty)* | Company IANA zone applied to legacy `UTC` shifts at boot; create-shift fallback when timezone omitted |

**Still missing (data jobs):**
- No data-pruning job — app_sessions/app_items/device_hardware/etc. grow unbounded
- No data aggregation/pre-computation

---

## 10. Production-Readiness Gaps

| Gap | Severity | Details |
|---|---|---|
| **No tests** | 🔴 High | 0 test files. `go test ./... -v -count=1` runs nothing. |
| **No structured logging** | 🟠 Medium | Uses `log.Printf` instead of a structured logger (zap, slog, logrus). No log levels beyond text prefix `[server]`, `[database]`, `[auth]`. |
| **No request validation** | 🟠 Medium | DTOs have `validate:` tags but **no validation library is imported**. Echo's `c.Bind(&req)` only deserializes JSON — it doesn't validate. All validation is manual `if req.Field == ""` checks. |
| **No rate limiting** | 🟠 Medium | Login endpoint and sync endpoints are completely unprotected. Brute-force / DoS is trivial. |
| **Health check bug** | 🟢 Low | Returns `c.RealIP()` as the timestamp value. Should use `time.Now()`. |
| **No graceful Redis degradation** | 🟠 Medium | If Redis is down, `employeeService` is constructed with a nil Redis client and employee secret generation/validation fails. No fallback (e.g., DB-stored secrets). |
| **No data-pruning job** | 🟢 Low | App sessions/items accumulate indefinitely. The staleness sweep only deactivates catalog links, it does not delete old data. |
| **No permission model** | 🟠 Medium | Server has no role-based access control. Any authenticated user can access all endpoints. |
| **Cross-account data injection** | 🟠 Medium | Sync handlers validate the JWT but don't verify the `employeeId` in the body matches the JWT subject. |
| **No graceful shutdown** | 🟢 Low | `Shutdown()` is called with a 10s context, but the stale-sweep goroutine isn't joined and in-flight requests may be dropped. |
| **Dead DTOs** | 🟢 Low | `new_schema_dto.go` still ships unused `BrowserContextEntry`, `UrlEntry`, `UrlVisitEntry`, `ShellCommandEntry` types. Harmless, but misleading. |
| **Legacy catalog owner column** | 🟢 Low | `installed_applications.employee_id`/`installed_packages.employee_id` are now just "first uploader" markers (catalog dedup is fingerprint-based). Keeping them allows legacy queries but is semantically stale. |

---

## 11. Immediate Next Steps

1. **Add rate limiting** — at minimum on `/auth/login` and sync endpoints
2. **Fix the health check bug** — `c.RealIP()` is not a timestamp
3. **Add structured logging** — replace `log.Printf` with `slog` (Go 1.21+ standard library)
4. **Add request validation** — use `go-playground/validator` or similar
5. **Cross-check employeeId in sync** — ensure the JWT userId matches the request employeeId
6. **Add tests** — start with service-layer tests using mock repositories, then handler integration tests
7. **Add data cleanup job** — periodic deletion of old app_sessions/app_items data
8. **Add server-side permissions** — even a simple role check before allowing writes
9. **Add listing endpoints for the remaining sync tables** — installed-apps, installed-packages, device-hardware, network-info, session-events
10. **Prune dead DTOs** — remove `BrowserContext*`, `Url*`, `UrlVisit*`, `ShellCommand*` types from `new_schema_dto.go`
