# Plan — Employee Device Lifecycle (machine_id everywhere + remove-account / remove-device flows)

> **Status:** proposal (not implemented). This document is the execution plan for `request.txt`.
> **Branch:** `enhance-livestreamV4` · **Date:** 2026-10-19
> **Services touched:** `server/` (Go + PostgreSQL), `client/` (.NET desktop), `web/` (Next.js).
> **Mandatory rules in play:** Installer-Parity (§6), Machine-Scoped Data Rule (§6),
> Retired-Device History Rule (§6), Client-vs-Web API Auth Separation Rule (§6),
> Web Infinite-Scroll Rule / URL-Synced Filters Rule (§6), Branding-Single-Source (§6).

---

## 1. Requirements (from `request.txt`)

| # | Requirement |
|---|---|
| **R1** | Manage devices per employee — define exactly what happens when an employee changes device/system. |
| **R2** | Link **every table** with `machine_id` on the client local DB **and** the server DB. |
| **R3** | New server table storing `employee_id` + `machine_id` + `status` (`active`\|`old`) + `first_track_at` + `last_track_at`. |
| **R4** | Client `client.exe --remove-account`: **first sync all unsynced data** to the server **with a "remove the account" signal**, **then** wipe the local DB and machine id. |
| **R5** | Server receives "remove the account" → set employee `tracking_status = untracked`, machine `status = old`, update `last_track_at`. |
| **R6** | Server "Remove current device" → on the employee's **next sync** the server **first persists the incoming data**, then **responds** telling the client to wipe its local DB + machine id; the client wipes. |
| **R7** | On re-login, **create a new machine id** and save it on both DBs. |
| **R8** | Web `/device-specs`, `/device-specs/software`, `/device-specs/peripherals`, `/device-specs/permissions`: when an employee has **multiple machines**, show a **dropdown machine list with status**. |

---

## 2. Current state (evidence) — what already exists and what is missing

A large part of the foundation already landed on 2026-10-17 (migrations 046/047, per-device Device
Specs, `POST /employees/:id/devices/remove`) and 2026-10-17/19 (migration 048,
`--remove-account` CLI). The work below **extends** that foundation; it does not restart it.

### 2.1 Already implemented (reuse, do not rebuild)

- **Server `machine_id` on the device-scoped sync tables** — `device_hardware_info`, `network_info`,
  `storage_devices`, `hardware_devices`, `permission_status`, `app_status`, and the two
  `employee_installed_*` junctions (migration `046_device_machine_scope.sql`). `app_sessions` has had
  it since 006. Stamped server-side from `DeviceAuth` (`device_machine_id`) except `app_sessions`
  (client-supplied).
- **Legacy attribution** — migration `047_attribute_legacy_machine_rows.sql` re-attributes `''` rows
  from the `employee_devices` activity window.
- **Device registry** — `employee_devices` (021 + 048: `is_active_device`, `remove_at`).
- **Device removal (admin)** — `POST /api/v1/employees/:id/devices/remove` →
  `NewSchemaService.RemoveEmployeeDevices` revokes devices, closes open sessions/items, sets
  `tracking_status='untracked'`, `is_online=false`; returns counts.
- **Per-device Device Specs** — `GET /employees/:id/detail?deviceId=` and the URL-synced
  `?deviceId=` selector in `web/src/components/employees/EmployeePage.tsx`.
- **Client `--remove-account`** — three paths in `Program.cs` (cold-start / background /
  running-instance) that wipe local data and machine id; `WipeLocalAccountDataAsync` deletes every
  SQLite row (incl. `device_token`).

### 2.2 Gaps and defects found (must be fixed for the requirements)

| ID | Gap / defect | Evidence |
|---|---|---|
| **G1** | **No `employee_machines` table** (R3). `employee_devices` has no `status`, `first_track_at`, or `last_track_at`. | `migrations/021`, `048`; `models/device.go` |
| **G2** | **`machine_id` coverage is partial.** Server: `session_events`, `app_items`, `location_samples`, `terms_consent` have none. Client: **only `app_sessions`** has it locally — every other local table (device_hardware_info, storage_devices, installed_applications, installed_packages, network_info, hardware_devices, session_events, app_items, app_status, permission_status, location_samples) lacks it. | `client/Storage/DatabaseSchema.cs`; `server/migrations/006,008,029,036` |
| **G3** | **`--remove-account` does not sync, and does not notify the server.** `RequestImmediateSync()` is called **before** `host.StartAsync()` (so `SyncService.ExecuteAsync` isn't running); no HTTP "remove account" call exists anywhere. | `client/Program.cs:527-589`; grep for remove-account in `server/internal` → none |
| **G4** | **No server→client "wipe" instruction.** `dto.SyncBatchResponse = {synced, message, rejectedIds?, missingSessionIds?}`; the client parses no `action`/`wipe` field. | `server/internal/dto/new_schema_dto.go:15-31`; `client/Services/SyncService.cs:81-88` |
| **G5** | **Revoked device tokens are rejected by `DeviceAuth`**, so a removed machine's sync falls back to the employee Bearer JWT and its rows land with `machine_id = ''`. There is no way to persist-then-instruct. | `middleware/device_auth.go:45-49,70-90` |
| **G6** | **Detail `devices` list loses status.** The shared `ListEmployeeDevices` SELECT omits `is_active_device`/`remove_at`, so `isActiveDevice` is always `false`. | `repository/new_schema_repo.go:1352-1386` vs `repository/device_repo.go:137-169` |
| **G7** | **Web/server key mismatch.** Server emits `isActiveDevice`; the web type/consumers read `isActive` → always `undefined` → every device shows "removed". | `server/internal/dto/employee_dto.go:234-246`; `web/src/lib/api.ts:474-491`; `EmployeePage.tsx:204`; `device-specs/page.tsx:25` |
| **G8** | **Every login rotates the device row** (`UpsertDevice` revokes the prior `(employee,machine)` row then inserts a new one), so one physical machine appears as many `employee_devices` rows — the dropdown can therefore duplicate a machine. | `repository/device_repo.go:23-66` |
| **G9** | **Journey sessions use the hostname as `machine_id`**, not `AppConfig.ClientId` — inconsistent machine identity. | `client/Core/DesktopEventBus/JourneyEngine.cs:110` |
| **G10** | **No relogin-new-machine-id guarantee after a server-initiated wipe** — the client has no code path that regenerates the id outside `--remove-account`. | `AppConfig.cs:259`, `Program.cs` |

---

## 3. Design decisions

### D1 — Machine lifecycle registry: new table `employee_machines` (satisfies R3)

Create a dedicated table exactly as requested; it becomes the **canonical, one-row-per-machine**
lifecycle record used by the web dropdown and by the active→old transition.

```sql
-- 049_employee_machines.sql
CREATE TABLE IF NOT EXISTS employee_machines (
  id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  employee_id    VARCHAR(20) NOT NULL REFERENCES employees(employee_id) ON DELETE CASCADE,
  machine_id     VARCHAR(255) NOT NULL,
  status         VARCHAR(10) NOT NULL DEFAULT 'active'
                   CHECK (status IN ('active','old')),
  first_track_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  last_track_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  wipe_requested_at TIMESTAMPTZ,           -- set by "Remove current device" (R6)
  created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  deleted_at     TIMESTAMPTZ
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_employee_machines_emp_machine
  ON employee_machines(employee_id, machine_id) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_employee_machines_employee
  ON employee_machines(employee_id);
```

- Relationship to `employee_devices`: **`employee_machines` = lifecycle/status (one row per physical
  machine); `employee_devices` = auth-token registry (may rotate on re-login).** They are joined on
  `(employee_id, machine_id)`. This resolves G8 for the dropdown (dedupe to one row per machine) and
  keeps token rotation untouched.
- Why not just add columns to `employee_devices`: the user asked for a new table, and token rotation
  (G8) would keep producing duplicate lifecycle rows. One table per concern is the smaller, clearer
  fix for the dropdown/lifecycle semantics.

### D2 — "Accept revoked token, but flag it" (satisfies R6, fixes G5)

`DeviceAuth` gains a lookup that returns a device even when `revoked_at IS NOT NULL`
(`GetByTokenHashIncludingRevoked`). When revoked, the middleware still sets
`employee_id` / `device_id` / `device_machine_id`, additionally sets `device_removed = true`, and
**skips `TouchLastSeen`**. Every sync handler, *after* it has persisted the batch, sets
`resp.Action = "wipe_local"` when `device_removed` is true. Net effect: **data is stored first, then
the wipe instruction is returned** — exactly R6. It is inherently one-shot: once the client wipes, it
no longer holds the token.

- The alternative (rely on the Bearer fallback) is rejected: it loses `machine_id` on those rows,
  which violates R2/R6.

### D3 — Remove-account is an explicit DeviceAuth endpoint, not a sync flag (satisfies R4/R5)

`POST /api/v1/devices/self-remove` (under the `syncGroup` / `DeviceAuth`) authenticates the calling
machine and marks **that** machine old for **its own** employee. The client calls it **after** the
drain completes. Keeping it off the sync payload means it works even when there is nothing left to
sync, and the drain can be observed/timed independently.

### D4 — One shared removal routine on the client

All `--remove-account` entry points (cold-start, background, running-instance, GUI) converge on one
`IAccountRemovalService` routine:

1. stop tracking / pause collection;
2. `SyncService.RequestImmediateSync()` → **await drain completion** (bounded timeout);
3. best-effort `POST /devices/self-remove` (short timeout; failure never blocks the wipe);
4. `WipeLocalAccountDataAsync()` (wipes all rows incl. `device_token`) + delete `.machine-id` +
   `ResetMachineId()` + `EncryptedConfigService.ResetCache()`;
5. exit / land on sign-in.

The server-initiated wipe (R6) reuses steps 1 + 4 + 5 (the data is already stored).

---

## 4. Data model changes

### 4.1 Server migrations

| Migration | Contents |
|---|---|
| **049_employee_machines.sql** | New `employee_machines` table (D1). |
| **050_machine_id_coverage.sql** | `ADD COLUMN machine_id TEXT NOT NULL DEFAULT ''` to `session_events`, `app_items`, `location_samples` (and `terms_consent` — see open question Q2); per-table `(employee_id, machine_id, …)` indexes; best-effort backfill from the owning `app_sessions.machine_id` for `app_items`, and from `employee_devices` activity windows for `session_events`/`location_samples` (047 pattern). |
| *(no migration)* | `wipe_requested_at` lives on `employee_machines` in 049. |

- Keep `employee_devices.is_active_device` / `remove_at` (048) — token-level state.
- Backfill `employee_machines` from `employee_devices`: one row per `(employee_id, machine_id)` with
  `first_track_at = MIN(created_at)`, `last_track_at = MAX(last_seen_at)`, `status = active` when any
  unrevoked row exists else `old`. Idempotent (`ON CONFLICT DO NOTHING`).

### 4.2 Client local SQLite (R2)

Extend `DatabaseSchema.MigrateSql` with idempotent `ALTER TABLE … ADD COLUMN machine_id TEXT NOT NULL DEFAULT ''`
(the runner already swallows "duplicate column"; see `SqliteLogStore.RunMigrationsAsync`). Tables to add:

`device_hardware_info`, `storage_devices`, `installed_applications`, `installed_packages`,
`network_info`, `hardware_devices`, `session_events`, `app_items`, `app_status`, `permission_status`,
`location_samples`, `client_terms`; add `employee_info.machine_id` (durable record of the current
machine, the "save on both DBs" half of R7). `app_sessions.machine_id` already exists.

Plus, in `DatabaseSchema.CreateTableSql`, add the column to the fresh-DB definitions so new installs
match.

- **Stamping:** every insert path writes `_config.ClientId` (single source, cached). Fix G9 by using
  `AppConfig.ClientId` in `JourneyEngine` instead of `Environment.MachineName`.
- Local reads that need per-machine scoping filter `machine_id = <current>`; aggregate reads ignore it.

---

## 5. API contract changes

All client-facing routes are mounted under **`syncGroup` / `DeviceAuth`** (Client-vs-Web Auth
Separation Rule). Web routes stay under `protected` / `JWTAuth`.

| Endpoint | Group | Change |
|---|---|---|
| `POST /api/v1/devices/self-remove` | DeviceAuth | **New.** Marks the calling machine `old`, updates `last_track_at`, revokes its device rows, closes sessions/items, sets employee `untracked`. Returns `{removed:true, machineId, lastTrackAt}`. |
| `POST /api/v1/*/sync` (all 11 + location/terms) | DeviceAuth | Response `SyncBatchResponse` gains `action,omitempty` (`"wipe_local"`). Set when `device_removed`. **Incoming rows are persisted before the flag is returned.** |
| `GET /api/v1/employees/:id/detail?deviceId=` | JWTAuth | `devices[]` now sourced from `employee_machines` (one row/machine) and each entry carries `status` (`active`\|`old`), `firstTrackAt`, `lastTrackAt`, and the corrected `isActiveDevice`/`removeAt` (fixes G6). |
| `POST /api/v1/employees/:id/devices/remove` | JWTAuth | Also sets `employee_machines.status='old'`, `last_track_at=NOW()`, `wipe_requested_at=NOW()` (R6 trigger). Behaviour/response otherwise unchanged. |

DTOs (`server/internal/dto/`):
- `SyncBatchResponse` — add `Action string \`json:"action,omitempty"\``.
- `EmployeeDeviceDto` — add `Status`, `FirstTrackAt`, `LastTrackAt`.
- New `SelfRemoveAccountResponse`.

Web types (`web/src/lib/api.ts`):
- `EmployeeDevice` — replace `isActive: boolean` with `status: 'active' | 'old'`, keep
  `isActiveDevice: boolean`, add `firstTrackAt`, `lastTrackAt` (fixes G7).

---

## 6. Phased implementation

### Phase 0 — Schema foundation
1. `server/migrations/049_employee_machines.sql` (+ backfill from `employee_devices`).
2. `server/migrations/050_machine_id_coverage.sql` (+ backfill).
3. Client: `DatabaseSchema.cs` — `machine_id` on the tables in §4.2 (fresh + `MigrateSql` ALTERs).

**Verify:** migrations apply to the dev DB (dry-run then apply; row counts logged); client
`RunMigrationsAsync` is idempotent on an existing DB.

### Phase 1 — Server
4. `models/device.go` + `repository/device_repo.go`:
   - `EmployeeMachine` model; `UpsertEmployeeMachineActive`, `TouchEmployeeMachineLastTrack` (bulk
     update keyed by `device_id`), `MarkEmployeeMachineOld(employeeID, machineID)`.
   - `GetByTokenHashIncludingRevoked`.
5. `middleware/device_auth.go`: revoked-token acceptance + `device_removed` context flag + skip
   `TouchLastSeen` (D2).
6. `handlers/auth_handler.go` (`EmployeeLogin`): upsert `employee_machines` **before** returning
   (status `active`, `first_track_at` preserved, `last_track_at = NOW()`).
7. `handlers/new_schema_handler.go`: helper `withDeviceAction(c, resp)` that sets `action=wipe_local`
   on every sync response when `device_removed`; new `SelfRemoveAccount` handler reading
   `employee_id` + `device_machine_id` from context; stamp `machine_id` in `SyncSessionEvents`,
   `SyncAppItems`, `SyncLocationSamples` (G2).
8. `services/new_schema_service.go`: `RemoveAccount(employeeID, machineID)` (reuse the
   `RemoveEmployeeDevices` core: `RevokeEmployeeDevices` + `CloseOpenActivity` +
   `tracking_status='untracked'`), plus `MarkEmployeeMachineOld`. Extend `RemoveEmployeeDevices` to
   mark the machine old + `wipe_requested_at`. Fix the `devices[]` mapping to read
   `employee_machines` and include `status`/`firstTrackAt`/`lastTrackAt` (G6).
9. `repository/device_repo.go` `TouchLastSeen` (or the middleware touch path): also advance
   `employee_machines.last_track_at`.
10. `router/router.go`: register `syncGroup.POST("/devices/self-remove", newSchemaHandler.SelfRemoveAccount)`.

**Verify:** `go build ./...`, `go vet ./...`, `go test ./internal/...`. Live curl: login → machine
`active`; device-hardware sync → row stamped with the device's `machine_id`; admin remove →
`status='old'` + `wipe_requested_at`; the same device token syncs again → data stored **and**
`{"action":"wipe_local"}` returned; `self-remove` → `untracked` + `old` + `last_track_at` advanced.

### Phase 2 — Client
11. Stamp `machine_id` on every insert path (§4.2); fix `JourneyEngine` (G9).
12. `Services/SyncService.cs`:
    - `SyncBatchResponse` gains `Action`; parse it in `SendAsync`.
    - New `event Action? OnServerRequestedWipe`; raise once when `action == "wipe_local"`.
    - New `Task<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken)` (completion signal +
      `is_synced=0` count) so `--remove-account` can await the drain.
13. New `Services/AccountRemovalService.cs` implementing D4 (requests drain, calls
    `/devices/self-remove`, wipes, exits). Extract the wipe+kill logic currently duplicated in four
    places in `Program.cs` / `MainViewModel.cs`.
14. `Program.cs`:
    - **Cold-start & background `--remove-account`:** start the host **first**, then run
      `AccountRemovalService` (fixes G3). Cold-start keeps its "kill siblings" tail.
    - **Running-instance branch:** keep the named-pipe signal; the running instance now executes the
      full routine (headless handler ≈ `Program.cs:657-676`).
    - Wire `SyncService.OnServerRequestedWipe` → `AccountRemovalService` (both background and GUI).
15. `ViewModels/MainViewModel.cs`: `RemoveAccountAsync` delegates to `AccountRemovalService`.
    After a wipe, force `ResetMachineId()` + delete `.machine-id` so the **next login generates a
    new machine id** (R7). Persist the new id into `employee_info.machine_id` on save.
16. `Configuration/AppConfig.cs`: add a drain timeout knob (see Q4) and ensure
    `RemoveMachineAndDeviceIdentifiers()` is the single deletion path.

**Verify:** `dotnet build` (0/0). Unit/manual: DB with unsynced rows → `--remove-account` → server
receives the rows **and** the self-remove call, then local DB + `.machine-id` are gone; a fresh login
sends a **different** `machineId` and the server records a new `employee_machines` row (`active`).

### Phase 3 — Web
17. `web/src/lib/api.ts`: fix `EmployeeDevice` (G7) and add `status`/`firstTrackAt`/`lastTrackAt`.
18. `web/src/components/employees/EmployeePage.tsx`: dropdown renders when `devices.length > 1`
    (already) — show the machine status (`Active` / `Old · removed`, with `lastTrackAt` in the
    label/tooltip). Keep the existing URL-synced `?deviceId=` (`useUrlQueryState`), the
    `'{employee} ({hostname||deviceName||machineId})'` label rule, and `__all__` aggregate.
19. The four pages: `device-specs/page.tsx` uses `status === 'active'` (not `isActive`) for the
    removed-device empty state; `software`/`peripherals`/`permissions` inherit the shell's device
    scope (query re-key) and render per-machine data. No `localStorage`; filters stay in the URL.

**Verify:** `npx tsc --noEmit`, `next build`. Manual: employee with 2 machines → dropdown lists both
with correct statuses; selecting an old machine shows its last-known data (Retired-Device History
Rule) and the truthful empty state; selecting a live machine hides its uninstalled apps.

### Phase 4 — Contract + docs + installer parity
20. Update `server/ARCHITECTURE.md`, `client/ARCHITECTURE.md`, `web/ARCHITECTURE.md`, and
    `AGENTS.md` §4/§5/§6 (changelog entry + the machine-lifecycle contract). Add a **Device Lifecycle
    Rule** subsection documenting: login → `employee_machines` active; remove-device → old +
    `wipe_requested_at`; next sync persists then returns `wipe_local`; remove-account → self-remove
    endpoint; relogin → new machine id.
21. Installer parity: the client half is **not done** until `bash publish/build-installer.sh -b <os>`
    is built and smoke-tested. If a new env knob is added (Q4), add it to `.env`/`.env.example`
    **before** `encrypt-config.sh` so `config.enc` carries it (re-bake mandatory).

---

## 7. Verification matrix

| Check | Command / method | Expected |
|---|---|---|
| Server build/vet/test | `cd server && go build ./... && go vet ./... && go test ./internal/...` | clean |
| Migrations | dry-run + apply on dev DB | 049/050 apply; backfills report non-zero row counts; re-run is a no-op |
| Client build | `cd client && dotnet build` | 0 warnings / 0 errors |
| Client schema | boot on an existing DB | `machine_id` columns present; no "duplicate column" crash |
| Web | `cd web && npx tsc --noEmit && npm run build` | clean; routes registered |
| E2E remove-device (R6) | admin remove → device token syncs | batch persisted **then** `{"action":"wipe_local"}`; client wipes; relogin = new machine id |
| E2E remove-account (R4/R5) | `client.exe --remove-account` (installed build) | drain delivered, `untracked` + `old` + `last_track_at`, local DB + `.machine-id` gone |
| Relogin (R7) | re-login after either wipe | new `machineId`; new `employee_machines` row `active`; old row stays `old` |
| Web dropdown (R8) | 2-machine employee | both machines listed with status; `?deviceId=` scoping correct |
| Installer parity | build + install + run the flows | behavior identical to `dotnet run` |

---

## 8. Risks, invariants, open questions

**Invariants to preserve**
- `machine_id` is **server-stamped from the authenticated device**, never trusted from the request
  body (Machine-Scoped Data Rule).
- Removal is **non-destructive on the server** — history is preserved; only statuses change
  (Retired-Device History Rule). The *client* wipe is intentional and local.
- Removed devices keep showing last-known data; device-scoped reads include `is_active=false` links
  when the machine has no live registration.
- No unrelated refactoring; keep `app_sessions.machine_id` client-supplied as today.

**Risks**
- **R-Token:** accepting revoked tokens (D2) means a removed machine can keep syncing until it
  wipes. Mitigation: it is the only way to satisfy "persist then instruct"; optionally bound the
  window (Q3).
- **R-Drain:** a client with a huge backlog may exceed the drain timeout. Mitigation: bounded wait
  then proceed (current semantics already proceed on failure).
- **R-WipeRace:** wiping SQLite while collectors write. Mitigation: the routine stops tracking/
  collection first and runs the wipe on the owner process thread (as the running-instance handler
  does today).
- **R-Backfill:** server `session_events`/`location_samples` legacy rows may not attribute cleanly —
  they stay `''` (honest "unknown machine"), same rule as 047.

**Open questions (decide before Phase 0)**
- **Q1 — `employee_machines` vs extending `employee_devices`.** Plan assumes a new table (R3). Confirm.
- **Q2 — `terms_consent.machine_id`.** Consent is arguably per-employee, not per-machine. Include or
  skip? (Plan: skip unless you want literal "every table".)
- **Q3 — revoked-token acceptance window.** Accept indefinitely until the client wipes, or expire
  after N days? (Plan: indefinitely; add an expiry only if you want one.)
- **Q4 — new env knob?** A bounded `ALPHA_REMOVE_ACCOUNT_SYNC_TIMEOUT_SEC` would require a
  `config.enc` re-bake. Add it, or hardcode the timeout?

---

## 9. File-level change index

**Server**
- `migrations/049_employee_machines.sql` *(new)*, `050_machine_id_coverage.sql` *(new)*
- `internal/models/device.go`
- `internal/repository/device_repo.go`, `new_schema_repo.go`
- `internal/middleware/device_auth.go`
- `internal/handlers/auth_handler.go`, `new_schema_handler.go`
- `internal/services/new_schema_service.go`
- `internal/dto/new_schema_dto.go`, `employee_dto.go`
- `internal/router/router.go`
- `ARCHITECTURE.md`

**Client**
- `Storage/DatabaseSchema.cs`, `Storage/SqliteLogStore.cs`
- `Configuration/AppConfig.cs`
- `Services/SyncService.cs`, `Services/AccountRemovalService.cs` *(new)*
- `Program.cs`, `App.axaml.cs`, `ViewModels/MainViewModel.cs`
- `Services/LogCollectorService.cs`, `Core/BrowserAccessibility/AccessibilityBrowserTracker.cs`,
  `Core/DesktopEventBus/JourneyEngine.cs`
- insert paths for device_hardware/network/storage/hardware_devices/session_events/app_items/
  app_status/permission_status/location_samples/installed_*
- `ARCHITECTURE.md`

**Web**
- `src/lib/api.ts`
- `src/components/employees/EmployeePage.tsx`
- `src/app/(app)/device-specs/page.tsx` (+ `software/`, `peripherals/`, `permissions/`)
- `ARCHITECTURE.md`

**Root** — `AGENTS.md` (changelog + new Device Lifecycle Rule).
