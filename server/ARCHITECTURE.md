# Server Architecture — Alpha AI Tracker API

> **Last audited:** 2026-07-27 (installed_packages)  
> **Changelog:** 2026-07-27: Added migration 009 (installed_packages table). New model/DTO/handler/service/repo for installed_packages. Split from installed_applications (GUI apps only). New route POST /api/v1/installed-packages/sync.  
> **Service completion (honest):** ~47%

---

## 1. Responsibility & Scope

**Owns:**
- All persistent data storage (PostgreSQL) and temporary auth data (Redis)
- Authentication for both web admins (email/password → httpOnly cookie) and employees (secret key → JWT)
- REST API for CRUD operations on users, employees, departments, and activity logs
- Activity log ingestion from desktop clients (bulk insert with dedup)
- Company admin auto-initialization on first startup
- Migration management (runs `.sql` migration files on startup)

**Does NOT own:**
- Any frontend rendering (served by the Next.js web app)
- Shell command storage or API (**missing** — client sends data but server has no endpoint)
- Background job processing (no job queue, no cron)
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
├── cmd/server/main.go           # Entry point. Config loading, DI setup, auto-init admin, graceful shutdown
├── Makefile                     # build, run, dev (air hot-reload), test, clean, deps, setup
├── go.mod / go.sum              # Go module: github.com/alpha-ai-tracker/server
├── .env.example                 # Environment variable template
│
├── migrations/                  # SQL migration files, run in sorted order on startup
│   ├── 001_init.sql             # Initial schema: users, departments, sequences, triggers
│   ├── 002_employees.sql        # Separate employees table, migrate non-admin users
│   ├── 003_activity_logs.sql    # [REMOVED — was activity_logs table]
│   ├── 004_employee_department_id.sql  # FK from employees → departments
│   ├── 005_soft_delete.sql      # deleted_at column on all tables
│   └── 006_new_schema.sql       # 9 new tables + DROP TABLE activity_logs CASCADE
│
└── internal/
    ├── config/config.go         # Loads env vars, builds Config struct with typed fields
    ├── database/postgres.go     # pgxpool creation, migration runner
    ├── redis/redis.go           # Redis client wrapper (StoreSecret, ValidateSecret, DeleteSecret)
    │
    ├── models/                  # Database models (structs with db/json tags)
    │   ├── user.go              # User + UserPublic (safe for API)
    │   ├── employee.go          # Employee + EmployeePublic
    │   ├── device_hardware_info.go # Phase 1: DeviceHardwareInfo, InstalledApplication, NetworkInfo, SessionEvent
    │   └── app_session.go         # Phase 2: AppSession, BrowserContext, FileExplorerContext, UrlRecord, UrlVisit
    │
    ├── dto/                     # Request/Response DTOs
    │   ├── user_dto.go          # LoginRequest, CreateUserRequest, UserResponse, etc.
    │   ├── employee_dto.go      # EmployeeLoginRequest, GenerateSecretResponse, etc.
    │   └── new_schema_dto.go    # Phase 1 & 2: all sync request/response DTOs + AppSessionListResponse
    │
    ├── repository/              # Data access layer (raw SQL via pgx)
    │   ├── user_repo.go         # User CRUD, CountCompanyAdmins, IsUniqueEmail
    │   ├── employee_repo.go     # Employee CRUD, GenerateEmployeeID, GetDepartments
    │   ├── new_schema_repo.go   # Phase 1 & 2: bulk insert for 9 tables + ListAppSessions
    │   └── department_repo.go   # Department CRUD with employee count (LEFT JOIN)
    │
    ├── services/                # Business logic layer
    │   ├── auth_service.go      # Login, token generation/validation, EnsureCompanyAdmin
    │   ├── user_service.go      # User CRUD with email uniqueness checks
    │   ├── employee_service.go  # Employee CRUD, GenerateSecret (Redis)
    │   ├── new_schema_service.go # Phase 1 & 2: sync handlers for 9 tables + ListAppSessions
    │   ├── department_service.go    # Department CRUD
    │   └── redis_interface.go   # Interface for Redis operations (decouples auth handler)
    │
    ├── handlers/                # HTTP handlers (Echo context)
    │   ├── auth_handler.go      # Login, Logout, Me, CheckAuth, EmployeeLogin, EmployeeDisconnect
    │   ├── user_handler.go      # List, Get, Create, Update, Delete users
    │   ├── employee_handler.go  # List, Get, Create, Update, Delete, GenerateSecret
    │   ├── new_schema_handler.go # Phase 1 & 2: sync endpoints (9 total) + ListAppSessions
    │   └── department_handler.go    # List, Create, Update, Delete departments
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

All endpoints are under `/api/v1`. Full route inventory:

### Health

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/health` | None | Returns `{status: "ok"}` — **bug: timestamp uses client IP instead of actual time** |

### Auth (Public)

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/auth/login` | None | Web admin login. Sets httpOnly cookie. |
| POST | `/auth/employee-login` | None | Employee desktop login. Returns JWT. |
| POST | `/auth/employee-disconnect` | None | Employee disconnect. Sets untracked + offline. |

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
| POST | `/employees` | Create employee |
| PUT | `/employees/:id` | Update employee |
| DELETE | `/employees/:id` | Soft-delete employee |
| POST | `/employees/:id/generate-secret` | Generate one-time secret → Redis (5-min TTL) |

### Activity Logs

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/activity-logs/sync` | Public* | Client log sync (JWT in body) |
| GET | `/activity-logs` | Protected | List logs (paginated, filterable) |

### Installed Packages (Public — JWT in body)

| Method | Path | Purpose |
|---|---|---|
| POST | `/installed-packages/sync` | CLI tools/runtimes/libs sync from client |

### Departments (Protected)

| Method | Path | Purpose |
|---|---|---|
| GET | `/departments` | List with employee count |
| POST | `/departments` | Create |
| PUT | `/departments/:id` | Rename |
| DELETE | `/departments/:id` | Soft-delete |

### Missing Endpoints

| Expected Endpoint | Purpose | Status |
|---|---|---|
| `POST /api/v1/shell-commands/sync` | Shell command sync from client | ❌ **Not implemented** |

---

## 5. Database Schema

### Tables

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
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ (auto-trigger)
deleted_at      TIMESTAMPTZ (soft delete)
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
department_id  INTEGER NOT NULL DEFAULT 1 → FK → departments(id)
role            VARCHAR(50) DEFAULT 'employee'
shift           VARCHAR(20) DEFAULT 'Day'
tracking_enabled BOOLEAN DEFAULT true
tracking_status VARCHAR(20) DEFAULT 'untracked'
is_online       BOOLEAN DEFAULT false
avatar          VARCHAR(10) (auto-generated initials)
avatar_color    VARCHAR(10) (random from palette)
created_at      TIMESTAMPTZ
updated_at      TIMESTAMPTZ (auto-trigger)
deleted_at      TIMESTAMPTZ (soft delete)
```

**`activity_logs`** — Activity data synced from desktop clients
```
id              TEXT NOT NULL
employee_id     VARCHAR(20) NOT NULL → FK → employees(employee_id)
machine_id      TEXT NOT NULL DEFAULT ''
timestamp       TIMESTAMPTZ
process_name    TEXT NOT NULL
window_title    TEXT
process_id      INTEGER
cpu_percent     REAL
memory_bytes    BIGINT
is_foreground   BOOLEAN
user_name       TEXT
platform        TEXT
session_id      TEXT
employee_name   TEXT
synced_at       TIMESTAMPTZ
created_at      TIMESTAMPTZ
PRIMARY KEY (id, employee_id)
```

**`installed_packages`** — CLI tools, runtimes, and libraries from package managers (migration 009)
```
id              TEXT PK
employee_id     VARCHAR(20) NOT NULL → FK → employees(employee_id)
package_name    TEXT NOT NULL
version         TEXT NOT NULL DEFAULT ''
category        TEXT NOT NULL DEFAULT 'tool'
source_manager  TEXT NOT NULL DEFAULT ''
install_path    TEXT NOT NULL DEFAULT ''
publisher       TEXT NOT NULL DEFAULT ''
description     TEXT NOT NULL DEFAULT ''
detected_at     TIMESTAMPTZ
synced_at       TIMESTAMPTZ
created_at      TIMESTAMPTZ
deleted_at      TIMESTAMPTZ (soft delete)
```

**`schema_migrations`** — Migration tracking
```
filename        VARCHAR(255) PK
applied_at      TIMESTAMPTZ DEFAULT NOW()
```

### Indexes

- All tables have `deleted_at` partial indexes for soft-delete filtering
- Activity logs: composite indexes on `(employee_id, timestamp DESC)`, `(machine_id, timestamp DESC)`, `(employee_id, is_foreground, timestamp DESC)`
- Users/employees: indexes on email, employee_id, department, role

### Migration Tool

**Custom runner** in `database/postgres.go`. Reads `.sql` files from `migrations/` directory (currently 9 files: 001–009), tracks applied migrations in `schema_migrations` table, runs in transaction order.

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

```
┌──────────┐     POST /api/v1/activity-logs/sync     ┌──────────────┐
│  Client   │─────────────────────────────────────────▶│  Handler      │
│  (C#)     │  {employeeId, token, logs: [...]}        │  SyncLogs()   │
└──────────┘                                          └──────┬───────┘
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
                                                     │  SyncLogs()   │
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
                                                     │    100       │
                                                     │  - ON CONFLICT│
                                                     │    DO NOTHING │
                                                     └──────┬───────┘
                                                              │
                                                              ▼
                                                     ┌──────────────┐
                                                     │  PostgreSQL   │
                                                     │  activity_    │
                                                     │  logs table   │
                                                     └──────────────┘
```

### Validation

- **Token**: decrypted and JWT-validated in the handler
- **Employee**: looked up by `employeeId` from the decoded JWT — **but the handler also accepts `employeeId` from the request body and verifies the employee exists. The JWT userId and the body employeeId are NOT cross-checked** — meaning an employee with a valid token could send logs under a different `employeeId`
- **Timestamps**: parsed as RFC3339, falls back to `time.Now()` on failure
- **Data**: no sanitization or length limits on process names, window titles

---

## 8. Web-Facing Read Path

```
┌──────────┐     GET /api/v1/activity-logs             ┌──────────────┐
│  Web App  │──────────────────────────────────────────▶│  Handler      │
│  (Next.js)│  ?employeeId=&search=&page=&perPage=...   │  ListLogs()   │
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

### Caching Layer

**None.** Every request hits PostgreSQL directly. No Redis caching, no in-memory cache, no CDN.

### Pagination Approach

- Page/perPage query parameters
- Default: page=1, perPage=10 (employees), perPage=20 (activity logs)
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
- Activity log ingestion: no ordering requirement, so round-robin works
- **Sequences**: `employee_id_seq` uses `NEXTVAL` — safe across instances
- **No sticky sessions required** — cookie-based auth is stateless

### Connection Pooling

- pgxpool configured in `database/postgres.go`
- Default: MaxConns=25, MinConns=10, MaxConnLifetime=5min
- These are hardcoded in config with env overrides (`DB_MAX_OPEN_CONNS`, etc.)

### Background Jobs

**None.** No job queue, no cron, no scheduled tasks. Everything is request-driven:
- Activity log sync is triggered by client POST
- No cleanup job for old logs (data grows unbounded)
- No data aggregation/pre-computation

---

## 10. Production-Readiness Gaps

| Gap | Severity | Details |
|---|---|---|
| **No tests** | 🔴 High | 0 test files. `go test ./... -v -count=1` runs nothing. |
| **No structured logging** | 🟠 Medium | Uses `log.Printf` instead of a structured logger (zap, slog, logrus). No log levels beyond text prefix `[server]`, `[database]`, `[auth]`. |
| **No request validation** | 🟠 Medium | DTOs have `validate:` tags but **no validation library is imported**. Echo's `c.Bind(&req)` only deserializes JSON — it doesn't validate. All validation is manual `if req.Field == ""` checks. |
| **No rate limiting** | 🟠 Medium | Login endpoint and sync endpoint are completely unprotected. Brute-force / DoS is trivial. |
| **Health check bug** | 🟢 Low | Returns `c.RealIP()` as the timestamp value. Should use `time.Now()`. |
| **No graceful Redis degradation** | 🟠 Medium | If Redis is down, employee login returns 500. No fallback (e.g., DB-stored secrets). |
| **No cleanup cron** | 🟢 Low | Activity logs accumulate indefinitely. No job to prune old data. |
| **No permission model** | 🟠 Medium | Server has no role-based access control. Any authenticated user can access all endpoints. |
| **Cross-account log injection** | 🟠 Medium | SyncLogs validates the JWT but doesn't verify the `employeeId` in the body matches the JWT subject. |
| **No graceful shutdown** | 🟢 Low | `Shutdown()` is called but in-flight requests may be dropped (no `ShutdownWithContext` with draining). |

---

## 11. Immediate Next Steps

1. ~~**Add shell commands table and sync endpoint**~~ — deferred (shell commands removed from client)
2. **Add rate limiting** — at minimum on `/auth/login` and sync endpoints
3. **Fix the health check bug** — `c.RealIP()` is not a timestamp
4. **Add structured logging** — replace `log.Printf` with `slog` (Go 1.21+ standard library)
5. **Add request validation** — use `go-playground/validator` or similar
6. **Cross-check employeeId in sync** — ensure the JWT userId matches the request employeeId
7. **Add tests** — start with service-layer tests using mock repositories, then handler integration tests
8. **Add data cleanup job** — periodic deletion of old data
9. **Add server-side permissions** — even a simple role check before allowing writes
10. **Add installed packages listing endpoint** — for web dashboard to view packages
