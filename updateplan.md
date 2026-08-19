# Alpha AI Tracker — Master Security, Reliability, Data Integrity, and Scalability Plan

> **Last Updated:** 2026-08-19  
> **Status:** Approved for Implementation  
> **Scope:** Full End-to-End Implementation across `client/`, `server/`, and `web/`.

---

## Executive Architecture & Core Rules

1. **Installer-Parity Rule (Mandatory):** A desktop change is only complete when verified against an installed package (`sudo dpkg -i` on Linux). `dotnet run` is strictly for rapid iteration.
2. **Single Source of Truth & Branding:** Product identifiers derive exclusively from `client/APP_IDENTIFIERS` and `client/VERSION`.
3. **No-Hardcoded-Names Rule:** System and process classification must use structural OS metadata (PE Subsystem, cgroups, AT-SPI roles, `.desktop` categories), never hardcoded product names.
4. **Server-Side Identity Verification:** Sync ingestion must authenticate the device credential server-side via middleware and bind activity strictly to the authenticated employee identity. Client-supplied `employeeId` in JSON bodies is not trusted.

---

## Goal 0 — Linux Detached Update Handoff & Headless Resumption (P0)

### Problem Definition
On Linux, selecting an update runs `pkexec dpkg -i` directly inside the running process. Replacing `client.dll` while the .NET runtime is executing corrupts in-memory handles, and when the old process terminates, data collection stops completely until a user manually opens the GUI.

### Architectural Solution
1. **Detached Linux Updater Helper (`aat_update_linux.sh`):**
   - `AppUpdateService` writes a detached bash script to `/tmp/aat_update_<guid>.sh` and spawns it with `nohup ... &`.
   - The script waits for the parent process PID to terminate cleanly.
   - Executes `pkexec dpkg -i "<installer_path>"`.
   - Verifies `dpkg` exit code (`0`).
   - Relaunches the newly installed binary `/usr/bin/alpha-ai-tracker` (or target executable) with `--background --restart`.
   - Force-cleans the `updates/` directory and removes itself.
2. **Single-Instance Handoff & Boot Hydration:**
   - `Program.cs` handles `--restart` by retrying single-instance mutex lock for 10 seconds while the old process exits.
   - On launch with `--background`, the process restores persisted employee credentials, initializes `LogCollectorService` and `SyncService`, and collects activity headlessly without spawning a window.

---

## Goal 1 & Goal 2 — Centralized Device Authorization & Revocable Opaque Device Tokens (P0/P1)

### Problem Definition
1. **Sync Authorization Gap:** `SyncAppSessions`, `SyncAppItems`, and other 11 sync endpoints validate JWT expiration, but do not compare the JWT `UserID` against `req.EmployeeID` in the body.
2. **Expiring JWTs:** Employee JWTs expire, causing silent sync dropouts when standard re-login is required.

### Architectural Solution
1. **Database Schema (`021_employee_devices.sql`):**
   - Create `employee_devices` table:
     ```sql
     CREATE TABLE IF NOT EXISTS employee_devices (
         id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
         employee_id VARCHAR(20) NOT NULL REFERENCES employees(employee_id) ON DELETE CASCADE,
         machine_id VARCHAR(255) NOT NULL,
         platform VARCHAR(50) NOT NULL,
         client_version VARCHAR(50) NOT NULL,
         device_name VARCHAR(255) NOT NULL DEFAULT '',
         token_hash VARCHAR(64) NOT NULL UNIQUE,
         created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
         last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
         expires_at TIMESTAMPTZ,
         revoked_at TIMESTAMPTZ
     );
     CREATE INDEX idx_employee_devices_auth ON employee_devices(token_hash) WHERE revoked_at IS NULL;
     CREATE UNIQUE INDEX idx_employee_devices_active ON employee_devices(employee_id, machine_id) WHERE revoked_at IS NULL;
     ```
2. **Device Authentication Middleware (`internal/middleware/device_auth.go`):**
   - Intercepts requests to `/api/v1/*/sync`.
   - Parses `Authorization: Device <token>` or `Authorization: Bearer <token>`.
   - Computes SHA-256 hash of token, queries active `employee_devices`.
   - Touches `last_seen_at`.
   - Sets Echo context `c.Set("employee_id", device.EmployeeID)` and `c.Set("device_id", device.ID)`.
   - Rejects unauthenticated/revoked requests with `401 Unauthorized`.
3. **Employee Login Update (`POST /api/v1/auth/employee-login`):**
   - Accepts `machineId`, `platform`, `clientVersion`, `deviceName` in request DTO.
   - Generates a cryptographically secure 256-bit random opaque device token (`dev_tok_...`).
   - Hashes and stores token in `employee_devices` (upserting any active device for `(employee_id, machine_id)`).
   - Returns `{ employee, token, deviceId }`.
4. **Desktop Client & SyncEngine Persistence:**
   - Client stores `device_token` in `employee_info` SQLite table.
   - `SyncService` attaches `Authorization: Device <device_token>` header on all 11 sync endpoints.
   - Deprecates token/employeeId fields in JSON payload (server uses authenticated context).
5. **Web Admin Device Management:**
   - API endpoints: `GET /api/v1/employees/:id/devices` and `POST /api/v1/devices/:id/revoke`.
   - Web UI: Devices table under Employee Specs / Journey with a "Revoke Access" action.

---

## Goal 3 — Database Scalability, Keyset Pagination, and Retention Engine (P1)

### Problem Definition
1. **Employee ID Sequence Cycle:** `employee_id_seq` has `MAXVALUE 99999 CYCLE`, which causes duplicate ID collisions after 99,999 employees.
2. **Unbounded Storage Growth:** Server activity tables grow endlessly without background retention sweeps.
3. **Query Latency:** `COUNT(*)` and unindexed queries on multi-million row activity tables degrade dashboard performance.

### Architectural Solution
1. **Database Schema & Sequence Repair (`022_sequence_retention_indexes.sql`):**
   - `ALTER SEQUENCE employee_id_seq NO CYCLE MAXVALUE 9223372036854775807;`
   - Add partial composite indexes:
     ```sql
     CREATE INDEX IF NOT EXISTS idx_app_sessions_emp_started ON app_sessions(employee_id, started_at DESC) WHERE deleted_at IS NULL;
     CREATE INDEX IF NOT EXISTS idx_app_items_session_opened ON app_items(app_session_id, opened_at DESC);
     CREATE INDEX IF NOT EXISTS idx_app_items_emp_opened ON app_items(employee_id, opened_at DESC);
     ```
2. **Server Background Retention Worker (`jobs/retention_sweep.go`):**
   - Periodic hourly job purging closed `app_sessions` and `app_items` older than `RETENTION_DAYS` (default 30 days).
   - Operates in small chunks (e.g. 1,000 rows/batch) with explicit logging of deleted row count and duration to prevent DB lock contention.
3. **Optimized Pagination Query Paths:**
   - Update `ListAppSessions` and `ListAppItems` repositories to support keyset filtering on `(started_at, id)` / `(opened_at, id)`.

---

## Goal 4 — Operational Observability & Scaling Readiness

1. **Ingestion Metrics & Health Diagnostics:**
   - Track ingestion throughput, batch byte sizes, latency, sync auth failures, and active device counts.
   - Expose metrics in `GET /api/v1/health`.
2. **System Log Standard:**
   - Standardize JSON structured logging across `server/` and `client/` for updating, auth, and retention passes.

---

## Complete Implementation Checklist

- [x] **Phase 0:** Update `updateplan.md` with complete architectural details.
- [ ] **Phase 1 (Server Auth & Devices):**
  - Migration `021_employee_devices.sql`
  - Models, DTOs, Repository (`DeviceRepo`)
  - `DeviceAuth` Middleware
  - Updated `EmployeeLogin` and device endpoints (`/employees/:id/devices`, `/devices/:id/revoke`)
  - Handlers update to use authenticated context
- [ ] **Phase 2 (Client Updates & Sync Engine):**
  - Updated `AppUpdateService` with Linux detached updater script handoff (`aat_update_linux.sh`)
  - Updated `SqliteLogStore` for `device_token` persistence
  - Updated `SyncService` to pass `Authorization: Device <token>` header
- [ ] **Phase 3 (Server Retention, Sequence & Performance):**
  - Migration `022_sequence_retention_indexes.sql`
  - Retention job `jobs/retention_sweep.go` and server startup wiring
  - Repositories updated with composite indexes & keyset pagination support
- [ ] **Phase 4 (Web Dashboard Device Management):**
  - Device list & Revoke action in Employee detail / Device Specs view
- [ ] **Phase 5 (Verification & Packaging):**
  - `go build` / `go vet` verification
  - `tsc --noEmit` / `next build` verification
  - `dotnet build` verification
  - Build `.deb` package (`bash publish/build-installer.sh -b linux`) and verify installed parity
