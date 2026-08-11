# Server Architecture — Alpha AI Tracker API

> **Last audited:** 2026-08-11 (employee detail endpoint + web user-detail page)
> **Changelog:**
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
├── migrations/                  # SQL migration files, run in sorted order on startup (001,002,004–016 = 15 files; 003 deleted)
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
│
└── internal/
    ├── config/config.go         # Loads env vars, builds Config struct (incl. LINK_STALE_DAYS)
    ├── database/postgres.go     # pgxpool creation, migration runner
    ├── redis/redis.go           # Redis client wrapper (StoreSecret, ValidateSecret, DeleteSecret)
    ├── jobs/staleness_sweep.go  # Hourly background job deactivating stale employee↔catalog links
    │
    ├── models/                  # Database models (structs with db/json tags)
    │   ├── user.go              # User + UserPublic (safe for API)
    │   ├── employee.go          # Employee + EmployeePublic
    │   ├── device_hardware_info.go # DeviceHardwareInfo, InstalledApplication, NetworkInfo, InstalledPackage, SessionEvent (+ unused ShellCommand)
    │   ├── app_session.go       # AppSession + AppItem (replaces BrowserContext/FileExplorerContext/UrlRecord/UrlVisit)
    │   └── employee_app_link.go # EmployeeInstalledApplication / EmployeeInstalledPackage junction rows
    │
    ├── dto/                     # Request/Response DTOs
    │   ├── user_dto.go          # LoginRequest, CreateUserRequest, UserResponse, etc.
    │   ├── employee_dto.go      # EmployeeLoginRequest, GenerateSecretResponse, etc.
    │   └── new_schema_dto.go    # Sync DTOs for all 7 tables + AppSessionListResponse + AppItemListResponse
    │                             # (still contains unused legacy DTOs: BrowserContext/FileExplorerContext/Url/UrlVisit/ShellCommand)
    │
    ├── repository/              # Data access layer (raw SQL via pgx)
    │   ├── user_repo.go         # User CRUD, CountCompanyAdmins, IsUniqueEmail
    │   ├── employee_repo.go     # Employee CRUD, GenerateEmployeeID, GetDepartments
    │   ├── department_repo.go   # Department CRUD with employee count (LEFT JOIN)
    │   └── new_schema_repo.go   # Bulk inserts for 7 tables, ListAppSessions/ListAppItems, Begin() tx + 4 catalog-upsert methods
    │
    ├── services/                # Business logic layer
    │   ├── auth_service.go      # Login, token generation/validation, EnsureCompanyAdmin
    │   ├── user_service.go      # User CRUD with email uniqueness checks
    │   ├── employee_service.go  # Employee CRUD, GenerateSecret (Redis)
    │   ├── department_service.go # Department CRUD
    │   ├── new_schema_service.go # Sync handlers for 7 tables (catalog-upsert for apps/packages) + 2 list queries
    │   └── redis_interface.go   # Interface for Redis operations (decouples auth handler)
    │
    ├── handlers/                # HTTP handlers (Echo context)
    │   ├── auth_handler.go      # Login, Logout, Me, CheckAuth, EmployeeLogin
    │   ├── user_handler.go      # List, Get, Create, Update, Delete users
    │   ├── employee_handler.go  # List, Get, Create, Update, Delete, GenerateSecret
    │   ├── department_handler.go # List, Create, Update, Delete departments
    │   └── new_schema_handler.go # 7 sync endpoints + ListAppSessions + ListAppItems
    │
    ├── middleware/auth.go       # JWTAuth (required), OptionalAuth (optional)
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

All endpoints are under `/api/v1`. Full route inventory (30 routes):

### Health

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/health` | None | Returns `{status: "ok"}` — **bug: timestamp uses client IP instead of actual time** |

### Auth (Public)

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/auth/login` | None | Web admin login. Sets httpOnly cookie. |
| POST | `/auth/employee-login` | None | Employee desktop login. Returns JWT. |

### Auth (Semi-Protected — validates token if present)

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/auth/check` | Optional | Returns `{authenticated, user?}` |

### Auth (Protected)

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/auth/me` | Required | Returns current user profile |
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
| GET | `/employees` | List employees (paginated, filterable, JOIN department) |
| GET | `/employees/:id` | Get employee by ID |
| GET | `/employees/:id/detail` | Aggregate machine picture (hardware, storage, network, apps, packages, peripherals, permissions, stats) |
| POST | `/employees` | Create employee |
| PUT | `/employees/:id` | Update employee |
| DELETE | `/employees/:id` | Soft-delete employee |
| POST | `/employees/:id/generate-secret` | Generate one-time secret → Redis (5-min TTL) |

### Departments (Protected)

| Method | Path | Purpose |
|---|---|---|
| GET | `/departments` | List with employee count |
| POST | `/departments` | Create |
| PUT | `/departments/:id` | Rename |
| DELETE | `/departments/:id` | Soft-delete |

### Client Ingestion (Public — JWT in body)

Employee token is carried in the request body (`{employeeId, token, entries: [...]}`), not a cookie. Server validates the token and returns `SyncBatchResponse{accepted, rejected}`.

| Method | Path | Table | Notes |
|---|---|---|---|
| POST | `/device-hardware/sync` | `device_hardware_info` | Bulk upsert |
| POST | `/installed-apps/sync` | `installed_applications` + `employee_installed_applications` | Upsert catalog-then-link in ONE tx |
| POST | `/installed-packages/sync` | `installed_packages` + `employee_installed_packages` | Upsert catalog-then-link in ONE tx |
| POST | `/network-info/sync` | `network_info` | Bulk upsert |
| POST | `/session-events/sync` | `session_events` | Bulk upsert |
| POST | `/app-sessions/sync` | `app_sessions` | Bulk upsert |
| POST | `/app-items/sync` | `app_items` | Bulk upsert |
| POST | `/app-status/sync` | `app_status` | Upsert by (employee_id, key) |
| POST | `/hardware-devices/sync` | `hardware_devices` | Bulk upsert |
| POST | `/permission-status/sync` | `permission_status` | Bulk upsert by check_id |
| POST | `/storage-devices/sync` | `storage_devices` | Bulk upsert |

### App Sessions & App Items (Protected — web admin reads)

| Method | Path | Purpose |
|---|---|---|
| GET | `/app-sessions` | List sessions (paginated, filterable, replaces old activity-logs listing) |
| GET | `/app-items` | List items (paginated, filterable by session/itemType/search) |

### Employee Detail (Protected)

| Method | Path | Purpose |
|---|---|---|
| GET | `/employees/:id/detail` | **Aggregate machine picture** for one employee: employee record, latest `device_hardware_info`, `storage_devices`, latest `network_info`, currently-installed apps/packages (active junction links), `hardware_devices` peripherals, `permission_status`, `app_status` map and activity stats. Consumed by the web `/users/[id]` page. |

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

### Tables (16 tracked tables + `schema_migrations`)

> 2026-08-11: +4 sync tables — `app_status` (key/value per employee), `hardware_devices`
> (USB/peripheral hotplug), `permission_status` (one row per permission method), and
> `storage_devices` (children of `device_hardware_info`). Migration 017.

**`users`** — Web admin users (company_admin role)
```
id              UUID PK (gen_random_uuid())
employee_id     VARCHAR(20) UNIQUE (EMP-XXXXX)
name            VARCHAR(255) NOT NULL
email           VARCHAR(255) UNIQUE NOT NULL
password_hash   VARCHAR(255) NOT NULL (bcrypt)
role            VARCHAR(50) DEFAULT 'employee'
department      VARCHAR(100) DEFAULT 'Engineering'
shift           VARCHAR(20) DEFAULT 'Day'
tracking_enabled BOOLEAN DEFAULT true
tracking_status VARCHAR(20) DEFAULT 'untracked'
is_online       BOOLEAN DEFAULT false
avatar          VARCHAR(10) (auto-generated initials)
avatar_color    VARCHAR(10) (random from palette)
is_company_admin BOOLEAN DEFAULT false
created_at / updated_at / deleted_at TIMESTAMPTZ (updated_at auto-trigger)
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

**`session_events`** — Login/logout/lock/unlock events (migration 006). The client now emits only `login` (via `StartTracking`); `logout` stopped being emitted when the employee-disconnect flow was removed 2026-08-10.
```
id TEXT PK · employee_id FK · event_type · os_username
event_at / synced_at / created_at / deleted_at
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

### Migration Tool

**Custom runner** in `database/postgres.go`. Reads `.sql` files from `migrations/` directory (currently 15 files: 001, 002, 004–016), tracks applied migrations in `schema_migrations` table, runs in transaction order. Each file runs in its own transaction.

---

## 6. Auth & Security Model

### Web Admin Auth Flow

1. Admin submits email + password to `POST /api/v1/auth/login`
2. Server looks up user by email, compares bcrypt hash
3. Valid: generates JWT with claims `{userId}`, signs with HMAC-SHA256, then **encrypts the signed JWT with AES-256-GCM** using a key derived from JWT secret
4. Encrypted token is set as an httpOnly cookie (`auth_token`)
5. Subsequent requests: `JWTAuth` middleware reads cookie, decrypts AES layer, then validates JWT signature
6. **Double encryption rationale**: protects against JWT secret leakage from log files (JWT is AES-encrypted until the middleware decrypts it)

### Employee Auth Flow

1. Admin generates a one-time secret via `POST /api/v1/employees/:id/generate-secret`
2. Server stores secret in Redis with key `employee_secret:<emp_id>` and 5-min TTL
3. Employee enters emp_id + secret in desktop client
4. Client sends `POST /api/v1/auth/employee-login` with `{employeeId, secretKey}`
5. Server validates secret from Redis, deletes it (one-time use)
6. Returns an encrypted JWT (same mechanism as admin, but with issuer `alpha-ai-tracker-employee`)
7. Client embeds this token in all sync requests

### Token Security

| Aspect | Implementation |
|---|---|
| **Signing** | HMAC-SHA256 via `golang-jwt/jwt/v5` |
| **Encryption** | AES-256-GCM, key = SHA256(JWT_SECRET) |
| **Expiry** | Configurable, default 24h (`JWT_ACCESS_EXPIRY`) |
| **Cookie** | httpOnly, SameSite=Lax, Secure=false in dev |
| **Employee token** | Returned in response body, stored locally by client |

### Missing Security Controls

- **No rate limiting** on login or any endpoint
- **No CSRF protection** (SameSite=Lax provides partial coverage)
- **No brute-force protection** for login attempts
- **No session invalidation** beyond clearing the cookie
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

**One job exists:**
- `jobs/staleness_sweep.go` — hourly goroutine (started in `main.go`) that sets `is_active = false` on `employee_installed_applications` / `employee_installed_packages` rows whose `last_seen_at` is older than `LINK_STALE_DAYS` (default 7). This is link-lifecycle management, NOT data pruning.

**Still missing:**
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
