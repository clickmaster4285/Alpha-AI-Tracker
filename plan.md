# Analysis & Implementation Plan

> **Date:** 2026-09-09 · **Branch:** `timeattendance` · **Status:** ⏸️ AWAITING USER APPROVAL — do NOT implement until accepted

---

## 1. Bug #9 — Orphan app-items silently lost: client marks all sent IDs as synced

### 1.1 Does the bug still exist? ✅ YES — verified in current code

I analyzed the current source (not just the report) and confirmed every claim:

**Server side — `server/internal/repository/new_schema_repo.go`:**
- `BulkInsertAppItems` (line 416) runs `filterOrphanAppItems()` per 500-row batch before inserting.
- Orphans (items whose `app_session_id` has no parent row in `app_sessions` yet) are **dropped from the batch**; only survivors are inserted.
- Returns `{synced: <survivors>, message: "Synced X of Y entries"}` via `SyncBatchResponse` (new_schema_service.go:453-457).
- The in-code comment (lines 427-432) claims "The dropped rows are re-sent on the client's next sync" — **this is false today** (that's the bug).

**Client side — `client/Services/SyncService.cs`:**
- `DrainTableAsync` (line ~565) → `SendAsync` returns `bool` only, `response.IsSuccessStatusCode` is the only success check (line ~682). The response **body is never read**.
- After a 2xx, it calls `markSentFn(ids)` with **ALL sent IDs** → `MarkAppItemsSentAsync` sets `is_synced=1` for every row in the slice.
- Net effect: if a 100-row batch has 81 orphans, the server inserts 19 and returns `{synced: 19}` — but the client marks **all 100** as `is_synced=1`. The 81 orphans are **permanently lost** (they never re-send; orphaned app_items carry no reconstructable data on the client side).

**Severity re-assessment:** the report says LOW-MEDIUM. I agree with LOW-MEDIUM but note the practical impact is worse than "data is re-created next cycle": orphaned `browser_tab` / journey items are *not* re-created — the client re-creates *new* rows with fresh GUIDs on later collection cycles, but the exact rows dropped are lost forever. Still recoverable-by-behavior, not recoverable-by-data.

### 1.2 Why the current design is not scalable / production-ready

1. **Silent data loss is invisible to operators.** Nothing surfaces "server dropped N rows" anywhere except a server WARN log that the client never reads.
2. **The fix depends on positional semantics.** "Mark the first N ids as synced" (Option A in the report) is fragile: the server inserts survivors in batch order, but positional marking breaks the moment any endpoint reorders, dedups, or batch-splits differently.
3. **No observability.** There is no counter/metric for dropped vs accepted rows; a widespread orphan problem would be undetectable.
4. **Unbounded retry risk if handled naively.** If the client blindly re-sends unaccepted rows every pass, a *permanently* invalid row (e.g. empty `appSessionId`) would retry forever (poison row) and grow the queue.

### 1.3 Scalable / production-ready fix (chosen design)

**Contract change (server):** make the acceptance result explicit and self-describing instead of positional.

- **Migration-free** — no schema change needed on the server.
- Extend `SyncBatchResponse` DTO (`server/internal/dto/new_schema_dto.go`):

  ```go
  type SyncBatchResponse struct {
      Synced      int      `json:"synced"`
      Message     string   `json:"message"`
      AcceptedIds []string `json:"acceptedIds,omitempty"` // NEW — present when partial acceptance occurs
  }
  ```

- In `BulkInsertAppItems` + `SyncAppItems` service path: return the **survivor row IDs** (not just a count) up to the handler. `filterOrphanAppItems` already computes `survivorIdx` — thread `[]models.AppItem` → return `(inserted, survivorIDs, err)`.
- Keep `AcceptedIds` `omitempty` so the other 11 sync endpoints' responses are unchanged on the wire (they accept 100% of rows; no client change needed there).
- Update the stale comment at new_schema_repo.go:436-438 to describe the new contract.

**Client (SyncService):** stop marking rows the server didn't accept.

- `SendAsync` returns `(bool success, string? body)` instead of `bool` (single call-site pattern; all 11 endpoints unaffected — they ignore the body).
- New per-endpoint post-send hook in `DrainTableAsync`: for `app-items` only, parse `SyncBatchResponse`. If `acceptedIds` is present and shorter than the sent ids:
  - mark ONLY `acceptedIds` as sent (`MarkAppItemsSentAsync(acceptedIds)`),
  - log a WARN with dropped count (mirrors server log),
  - count dropped rows in a new `_droppedRows` diagnostic counter.
- **Poison-row guard (scalability):** cap re-send attempts. Add `attempt_count` (default 0) + `sync_attempts` columns... actually simpler: a new client SQLite column `sync_attempts INTEGER NOT NULL DEFAULT 0` on `app_items` (idempotent `MigrateSql` ALTER, matching the existing idempotent-ALTER convention), incremented on every send that comes back without acceptance; when `sync_attempts >= 5`, the row is flagged `is_synced=1` + a `sync_note` marker so it stops being retried and shows up in a diagnostic query. This prevents a permanently-broken row from growing the unbounded retry queue forever (production concern at 50k+ backlogs).
- **Backoff-free by design:** unaccepted rows stay `is_synced=0`, so they re-send on the next drain pass — no new scheduling machinery needed; the existing per-pass budget and backoff already bound the work.

**Why this is scalable:**
- Explicit ID list = order-independent, works with any batching/dedup strategy.
- `omitempty` keeps wire format backward-compatible; old clients ignore the extra field; new client + old server = old behavior (no regression).
- Poison-row cap bounds worst-case retry growth (O(1) per row instead of unbounded).
- One WARN log per affected batch (already capped at 20 ids server-side) + a client counter = observable without new metrics infra.

### 1.4 Implementation checklist (Bug #9)

| # | File | Change |
|---|------|--------|
| 1 | `server/internal/dto/new_schema_dto.go` | Add `AcceptedIds []string` (omitempty) to `SyncBatchResponse` |
| 2 | `server/internal/repository/new_schema_repo.go` | `BulkInsertAppItems` returns `(int, []string, error)` — survivor IDs; fix stale comment |
| 3 | `server/internal/services/new_schema_service.go` | `SyncAppItems` threads survivor IDs into `SyncBatchResponse.AcceptedIds` |
| 4 | `client/Services/SyncService.cs` | `SendAsync` → returns `(bool, string?)`; app-items drain parses response, marks only accepted IDs, WARN + counter |
| 5 | `client/Storage/SqliteLogStore.cs` + `Core/Abstractions/ILogStore.cs` | Add `sync_attempts` column (idempotent ALTER) + `MarkAppItemsPartiallySentAsync(accepted, attempted)` updating both |
| 6 | `client/Storage/DatabaseSchema.cs` | `sync_attempts` in schema + idempotent MigrateSql |
| 7 | Verification | `go build`/`go vet`; `dotnet build` 0/0; live sync test: seed an orphan app_item, confirm it re-sends after parent lands, and confirm a poison row stops after 5 attempts |

**Installer-Parity note (client change):** item 4-6 compile into `client.dll` — no new assets. But per the Installer-Parity Rule, the client change is NOT "done" until verified from an installed build. No `config.enc` re-bake needed (no new env knobs).

---

## 2. Server bug (found by user) — most `synced_at` values are NULL

### 2.1 Root cause — verified in code

`synced_at` semantics are inconsistent per table because each `BulkInsert*` writes it differently. Concretely:

| Table | Migration DDL | INSERT statement | Result |
|---|---|---|---|
| `device_hardware_info` | 006: nullable | `..., collected_at, synced_at)` VALUES `..., e.CollectedAt, time.Now()` — explicit | ✅ SET |
| `installed_applications` | 006: nullable | explicit `time.Now()` | ✅ SET |
| `installed_packages` | 009: nullable | explicit `time.Now()` | ✅ SET |
| `network_info` | 006: nullable | explicit `time.Now()` | ✅ SET |
| `session_events` | 006: nullable | explicit `time.Now()` | ✅ SET |
| `app_sessions` | 006: nullable | explicit `now` | ✅ SET |
| **`app_items`** | **008: nullable, no default** | **INSERT column list omits `synced_at` entirely — only the ON CONFLICT UPDATE path sets `synced_at = NOW()`** | ❌ **NULL on first insert** |
| **`hardware_devices`** | 017: `NOT NULL DEFAULT now()` | **INSERT omits `synced_at`** — only the ON CONFLICT UPDATE sets it | ✅ by DDL default (ok) |
| `permission_status` | 017: NOT NULL DEFAULT now() | INSERT omits it | ✅ by DDL default |
| **`storage_devices`** | 017: `NOT NULL DEFAULT now()` | INSERT omits it | ✅ by DDL default |
| `location_samples` | 029: NOT NULL DEFAULT now() | INSERT omits it | ✅ by DDDL default |
| `app_status` | no synced_at column | n/a | n/a |

So the bulk of NULLs come from **`app_items`** — the highest-volume table in the system (every browser tab / journey item). `hardware_devices` is fine *only* because migration 017 added a DEFAULT; `app_items` (migration 008) has no default, so **every first insert lands with `synced_at = NULL`** and only re-synced rows (conflict path) get a timestamp. That matches the user's observation "most of the data synced_at are null" — `app_items` dominates row counts.

Secondary contributors:
- Rows inserted **before** a later ALTER added the column/default keep their NULL (historical rows).
- **Rows that fail the orphan preflight** never reach any INSERT, so they have no server row at all (related to Bug #9 — fixing #9 reduces orphan churn but those rows were never stored, so this is not a NULL source; noted for completeness).

### 2.2 Why this matters for production

1. **`session_lifecycle_sweep` and staleness logic.** `app_sessions` computes staleness from `last_sync_at` (031 backfill used `COALESCE(synced_at, started_at)`) — `app_sessions` is fine, but anything reading `app_items.synced_at` for freshness/lag metrics gets NULL → undefined behavior for dashboards/monitoring built on it.
2. **Ops/monitoring.** "How fresh is the ingest?" queries can't distinguish "never synced" from "synced but timestamp missing".
3. **Retention/audit.** `synced_at` is the natural filter for incremental exports/ETL; NULL rows break incremental consumers.

### 2.3 Fix design — make the server always stamp `synced_at` on INSERT

**Principle (production rule):** a table that records "when the server received this row" must never store NULL. Stamp at INSERT; the ON CONFLICT path refreshes it.

| # | File | Change |
|---|------|--------|
| 1 | `server/internal/repository/new_schema_repo.go` — `BulkInsertAppItems` | Add `synced_at` to the INSERT column list, `NOW()` as value (one extra arg per row — or cheaper: since all rows in a batch get the same value, keep it as a single constant `$N` arg per batch or just inline `NOW()` in the VALUES template — inline `NOW()` is simplest and avoids arg-count churn). ON CONFLICT clause stays `synced_at = NOW()`. |
| 2 | `server/internal/repository/new_schema_repo.go` — `BulkUpsertHardwareDevices` | (Optional hygiene) add `synced_at` to INSERT column list + value `NOW()` so it doesn't rely on the DDL default (keeps all writers consistent). |
| 3 | `server/migrations/033_synced_at_not_null.sql` | **Data repair + constraint**: ① backfill `UPDATE app_items SET synced_at = created_at WHERE synced_at IS NULL` (created_at is set on insert server-side, the best known arrival time); ② `UPDATE hardware_devices SET synced_at = created_at WHERE synced_at IS NULL` (historical pre-017 rows); ③ same for `installed_applications`, `installed_packages`, `network_info`, `session_events`, `app_sessions`, `device_hardware_info` (defensive: any historical NULLs); ④ `ALTER TABLE app_items ALTER COLUMN synced_at SET DEFAULT now();` ⑤ Optionally `SET NOT NULL` where the backfill guarantees no NULLs — apply `NOT NULL` only to `app_items` (the only table that produces NULLs today); others already have defaults. |
| 4 | Verification | `go build`/`go vet` clean; migration applies cleanly on dev DB; spot-check `SELECT COUNT(*) FROM app_items WHERE synced_at IS NULL` → 0; restart server → new app_items rows arrive with `synced_at` stamped. |

**Why this is scalable:** stamping `NOW()` inline in SQL is O(1) per statement (server clock, one evaluation per statement), no extra payload bytes from the client, no client change required, and the migration is a one-time indexed backfill. Adding the DDL DEFAULT protects against every future INSERT path that forgets the column.

**Data-loss safety:** the backfill uses `created_at` — never fabricated "now" for historical rows, so audit data stays honest ("arrival time as best-known").

### 2.4 Explicitly NOT in scope (to keep the change surgical)

- Backfilling `synced_at` on tables that already default it (no NULLs exist there in practice — the migration includes a defensive backfill anyway).
- Any web UI changes (web already types `syncedAt?` as optional — `web/src/lib/api.ts`).
- No client change for the synced_at fix (server-only).

---

## 3. Verification plan (both fixes)

1. **Server:** `go build` + `go vet` clean.
2. **Client:** `dotnet build` → 0 warnings, 0 errors.
3. **Migration:** apply 033 on the dev DB; confirm row counts of backfilled rows; re-run idempotently (safe).
4. **End-to-end (sync pipeline):** with server + client dev builds running:
   - Create an app_item whose parent session is not yet synced (simulate orphan) → confirm the first pass drops it server-side, client leaves it `is_synced=0` with `sync_attempts=1`, and after the parent session syncs, the item lands and is marked sent.
   - Poison test: manually set an app_item's `app_session_id` to a nonexistent GUID → confirm it stops retrying after 5 attempts.
   - Confirm fresh `app_items` inserts now carry non-NULL `synced_at`.
5. **Installer parity (client change only):** rebuild installer + verify from installed build per the Installer-Parity Rule (no `config.enc` re-bake needed — no new env vars).

---

## 4. Rollout order (safe deploy sequence)

1. **Deploy server first** (DTO field is additive; old clients unaffected).
2. **Ship client in next installer build** (Installer-Parity Rule).
3. **Run migration 033** on production DB during the server deploy window (idempotent, online backfill).

---

*Plan saved. No code has been changed — awaiting user review and acceptance before implementation begins.*
