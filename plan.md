# Analysis & Implementation Plan — Bug #9 + server `synced_at` NULL

> **Date:** 2026-09-09 · **Branch:** `timeattendance` · **Status:** ⏸️ DRAFT — awaiting user review/acceptance. **No code changed.**
> **v3 (2026-09-09):** folded in external review — ① quarantine switched to **in-memory** (rows stay `is_synced=0`; a rejected row must never look synced), ② `synced_at` backfill **batched**, ③ explicit new-client↔old-server rule, ④ wording: `rejectedIds` *identifies* rejected rows, the quarantine *policy bounds* the retry queue.
> **Mode:** Diagnose (complete) → Implement/build on acceptance.
> **Prepared per `prompt.md` (instruction priority: user requirements → AGENTS.md rules → arch docs → prompt.md → code patterns).**

---

## 0. Scope

| Item | Verdict | Scope |
|---|---|---|
| **Bug #9** — orphan app-items marked synced though the server dropped them | ✅ Still exists (verified in current code) | Fix: server DTO + client mark-sent logic. No DB migration. |
| **User-reported** — most server `synced_at` values are NULL | ✅ Confirmed — root-caused to `app_items` INSERT | Fix: one-line SQL change + idempotent migration 033 backfill. Server-only. |

Out of scope (explicitly, per "do not turn a narrow task into adjacent refactoring"):
API growth cap (§7 known risk), web UI changes, other 10 sync endpoints (no orphan preflight there), client retention rules.

---

## 1. Bug #9 — evidence (current code, not the report)

**Server — `server/internal/repository/new_schema_repo.go`**
- `BulkInsertAppItems` (L416) runs `filterOrphanAppItems()` (L525) per 500-row batch: items whose `app_session_id` is absent from `app_sessions` are **removed from the batch**; only survivors insert. Rows with empty `appSessionId` are treated as orphans.
- Returns survivor count only; `SyncAppItems` (`new_schema_service.go` L453-457) responds `SyncBatchResponse{Synced, Message}`. The in-code comment (L427-432) claims *"The dropped rows are re-sent on the client's next sync"* — **false today**; that is the bug.

**Client — `client/Services/SyncService.cs`**
- `SendAsync` (L682) returns `bool` from `response.IsSuccessStatusCode` only — the **response body is never read**.
- `DrainTableAsync` (L608-613) then calls `markSentFn(ids)` with **all** sent IDs → `MarkAppItemsSentAsync` sets `is_synced=1` for every row, including rows the server refused.
- Net effect: 100-row batch with 81 orphans → server inserts 19, returns `{synced:19}` → client marks all 100 synced. **The 81 orphan rows are permanently lost** (orphaned journey/tab items are not re-created; later cycles create *new* rows, not the lost ones).

**Why orphans occur at all (drain-order fact, verified):** `BuildDrainPass` (SyncService.cs L191-215) drains **app-sessions before app-items** within each pass, so most children follow their parents. The orphan window is real but narrow: a session created locally *after* the session drain (or dropped by the session table's pass budget / a send failure) leaves its items parentless for exactly one pass.

---

## 2. Fix design — Bug #9 (refined)

### 2.1 Server: make acceptance explicit — `rejectedIds`, not counts

The report's Option A ("mark the first N ids") is **rejected**: positional marking breaks the moment batching, dedup, or ordering changes. The report's Option B sketch (`droppedIds`) is adopted with clearer semantics:

- `server/internal/dto/new_schema_dto.go` — extend:
  ```go
  type SyncBatchResponse struct {
      Synced      int      `json:"synced"`
      Message     string   `json:"message"`
      RejectedIds []string `json:"rejectedIds,omitempty"` // present only when the server refused rows
  }
  ```
  **Why rejected- not accepted-side:** acceptance is the norm; the omitted field must mean "all accepted" so the other 11 endpoints and all existing clients are untouched on the wire, payloads stay small, and the field name states its intent ("these rows were refused; resend them"). `Synced` remains the authoritative count (covers `ON CONFLICT DO NOTHING` dedup, which is neither accepted-new nor rejected).
- `new_schema_repo.go` — `BulkInsertAppItems` additionally returns refused item IDs (it already has `orphanIDs`; collect the item IDs mapped from those orphans). `new_schema_service.go` — thread them into `SyncBatchResponse.RejectedIds`. Handler unchanged.
- **Fix the stale comment** at L427-432 to describe the actual contract (docs drift, prompt.md step 8).
- **Correct the AGENTS.md changelog** entry that claims dropped rows "stay is_synced=0 and re-send" — true only after this fix; add a dated changelog line with the implementation.

Wire compatibility, both directions:
| Server \ Client | Old client | New client |
|---|---|---|
| Old server | unchanged | unchanged (field absent ⇒ mark all) |
| New server | ignores unknown JSON field ⇒ old lossy behavior | precise mark-sent |

**Explicit rule (new client ↔ old server):** when `rejectedIds` is absent from an otherwise-2xx response, the client marks **ALL** sent ids — with an old server the new client preserves the old (lossy) behavior by design. Bug #9 is fixed only after the server deploys; the rollout order in §6 enforces server-first.

### 2.2 Client: respect refusals; bound the retry queue — no new schema columns

The v1 draft proposed a `sync_attempts` column with a 5-retry cap. **Dropped** per prompt.md rule 5 (smallest complete solution): a schema column, a store method signature change, and an extra write per send is machinery a *count-only* signal doesn't need. The explicit `rejectedIds` list makes rejected rows *identifiable*; the quarantine policy below is what *bounds* the retry queue — two different jobs.

- `SendAsync` → `Task<(bool Success, string? Body)>`. Call sites other than app-items ignore `Body` (behavior unchanged).
- New client-side DTO + deserialization with `PropertyNameCaseInsensitive = true` (client payloads hand-camelCase anonymous objects; the response must be read case-insensitively).
- In the app-items drain path only: after a 2xx with `rejectedIds` present, `markSentFn(sentIds EXCEPT rejectedIds)`. Refused rows stay `is_synced=0` and re-send next pass.
- **Self-heal path (no retry logic needed):** because sessions drain before items in every pass, a normal orphan lands its parent by the next pass and is then accepted. The capped per-pass budget + existing backoff already bound the work.
- **Poison quarantine (in-memory only — revised per external review):** a row refused by the server `QUARANTINE_RETRIES` (3) consecutive passes is added to an **in-memory** quarantine structure in `SyncService` (`ConcurrentDictionary<string,int>` reject-counts → skip-set consulted by the app-items drain before building the payload). **The SQLite row stays `is_synced=0` — quarantine must never make a rejected row look successfully synced.** Consequences, accepted deliberately:
  - `is_synced=0` keeps its honest meaning ("not delivered"); any future consumer of the flag (re-sync features, diagnostics) sees the truth.
  - Counters reset on process restart: the first 3 passes after a restart re-attempt previously-quarantined rows (~≤3 min at the guaranteed ≤60s cadence, user rule 2026-08-18) and re-quarantine if still refused — which also gives self-heal a fresh chance (e.g. the parent session landed server-side while the client was down).
  - Memory: entries exist only for genuinely refused rows (near-zero in practice); bounded by the orphan rate.
  - Retention interplay (verified rule 2026-08-11): client retention purges only **synced** rows, so quarantined `is_synced=0` rows are never deleted — they persist in SQLite (tiny) as diagnostic evidence. Nothing is deleted client-side.
  - Rationale: if the server's own preflight refuses a parent across 3 consecutive passes, the row is genuinely unparentable (e.g. empty/GONE `app_session_id`) — retrying forever would grow the queue unboundedly at scale. Server WARN log already caps id previews at 20/batch.

### 2.3 Implementation checklist — Bug #9

| # | File | Change |
|---|---|---|
| 1 | `server/internal/dto/new_schema_dto.go` | `RejectedIds []string` (omitempty) on `SyncBatchResponse` |
| 2 | `server/internal/repository/new_schema_repo.go` | Return refused item IDs from `BulkInsertAppItems`; fix stale comment (L427-432) |
| 3 | `server/internal/services/new_schema_service.go` | Thread refused IDs into the response |
| 4 | `client/Services/SyncService.cs` | `SendAsync` returns `(bool, string?)`; app-items drain parses `rejectedIds`, marks only accepted; **in-memory** reject-counter + quarantine skip-list (rows stay `is_synced=0`); WARN + debug counter |
| 5 | `AGENTS.md` | Changelog entry + correction of the incorrect 2026-09-04 claim |
| 6 | Verify | Server: `go build`, `go vet`. Client: `dotnet build` 0 warnings/0 errors. Live: seed an item whose parent isn't sent yet → pass 1 refuses it (`rejectedIds` returned, row stays `is_synced=0`), pass 2 accepts after the parent lands. Poison: item with permanently-missing parent → quarantined after 3 refusals, never re-sent, logged. |

Installer-Parity: client changes compile into `client.dll` — no new assets, **no `config.enc` re-bake** (no new env vars). Not "done" until ship-tested from an installed build (§5).

---

## 3. Server bug — `synced_at` NULL: root cause (verified)

Per-table writer audit of `new_schema_repo.go`:

| Table | DDL | INSERT stamps `synced_at`? | Result |
|---|---|---|---|
| device_hardware_info (006) | nullable | ✅ explicit `time.Now()` | OK |
| installed_applications (006) | nullable | ✅ explicit | OK |
| installed_packages (009) | nullable | ✅ explicit | OK |
| network_info (006) | nullable | ✅ explicit | OK |
| session_events (006) | nullable | ✅ explicit (upsert also stamps) | OK |
| app_sessions (006/020/031) | nullable | ✅ explicit (+ `last_sync_at`) | OK |
| **app_items (008)** | **nullable, NO default** | ❌ **column omitted from INSERT — stamped only on the ON CONFLICT re-sync path** | **NULL on every first insert** |
| hardware_devices (017) | NOT NULL DEFAULT now() | ❌ omitted, but DDL default covers INSERT | OK |
| permission_status (017) | NOT NULL DEFAULT now() | ❌ omitted, DDL default | OK |
| storage_devices (017) | NOT NULL DEFAULT now() | ❌ omitted, DDL default | OK |
| location_samples (029) | NOT NULL DEFAULT now() | ❌ omitted, DDL default | OK |

**Conclusion:** the NULLs the user sees are overwhelmingly **`app_items`** — the highest-volume table in the system (every tab/journey item). Migration 008 gave it no default and the writer omits the column, so only re-synced (conflict-path) rows get a timestamp. Historical rows predating later ALTERs may also be NULL on other tables (defensive backfill covers them).

**Why it matters:** `synced_at` is the ingest-freshness/incremental-export column; NULL breaks freshness queries and any ETL watermark. `app_sessions` lifecycle logic is unaffected (it uses `last_sync_at`, stamped explicitly).

---

## 4. Fix design — `synced_at` (server-only, smallest complete)

| # | File | Change |
|---|---|---|
| 1 | `new_schema_repo.go` — `BulkInsertAppItems` | Add `synced_at` to the INSERT column list with inline `NOW()` in the VALUES template (no extra bind args; one clock evaluation per statement — O(1), no payload cost). ON CONFLICT clause already stamps `NOW()` — unchanged. |
| 2 | `new_schema_repo.go` — hygiene (same principle, one-line each): `BulkUpsertHardwareDevices`, `BulkUpsertPermissionStatus`, `BulkUpsertStorageDevices`, `BulkUpsertLocationSamples` | Add `synced_at` to INSERT columns with `NOW()` so **every** sync writer stamps explicitly instead of leaning on DDL defaults. Keeps the "INSERT must stamp" invariant uniform for future writers. |
| 3 | `server/migrations/033_synced_at_backfill.sql` (new, idempotent) | ① **Batched** backfill in one `DO $$ … $$` block (a single statement — safe under any pgx exec mode): loop `UPDATE app_items SET synced_at = created_at WHERE ctid IN (SELECT ctid FROM app_items WHERE synced_at IS NULL LIMIT 10000)` + `GET DIAGNOSTICS … EXIT WHEN 0` — `created_at` is the server's insert-time stamp: the honest best-known arrival time, no fabricated dates. Same batched pattern, defensively, for the other nullable tables in §3. ② `ALTER TABLE app_items ALTER COLUMN synced_at SET DEFAULT now();` — protects every future writer. ③ `SET NOT NULL` on `app_items.synced_at` after the backfill (no other table produces NULLs today). **Honest transactionality note (verified `postgres.go:78-95`):** the runner wraps each migration file in ONE transaction, so batching bounds per-statement lock/memory pressure but the file still commits once — a single-tx backfill is not free. Mitigation for very large deployments: the backfill UPDATE is idempotent (`WHERE synced_at IS NULL`), so it can be pre-run in small batches via psql before the deploy window; the migration then finds 0 rows and completes instantly. |
| 4 | Verify | `go build`, `go vet`; apply 033 on dev DB (idempotent re-run safe); `SELECT COUNT(*) FROM app_items WHERE synced_at IS NULL` → **0**; restart server → fresh rows arrive stamped. |

Scale rationale: inline `NOW()` is per-statement, zero client change, zero payload growth; the migration is a one-time indexed backfill sized in the millions-of-rows range for `app_items` (acceptable online UPDATE; runs in the deploy window).

---

## 5. Verification matrix (per prompt.md — claims only what is run)

| Check | Command | Tier reached |
|---|---|---|
| Server source build | `go build ./...` && `go vet ./...` | source build verified |
| Client source build | `dotnet build` → 0 warnings / 0 errors (+ non-incremental watch if platform code touched — not expected) | source build verified |
| Migration | apply 033 on dev DB; re-run for idempotency; NULL-count query → 0 | source build verified |
| Sync behavior | dev server + dev client: orphan self-heal pass-1→pass-2; poison quarantined after 3 refusals (in-memory; re-attempted 3× after restart); fresh `app_items` rows stamped | source build verified |
| Installer | `bash publish/build-installer.sh -b linux` (and win if available) | installer built |
| Installed artifact | install the .deb on the Linux test machine, reproduce the orphan scenario from the installed binary | installed artifact verified *(requires user's machine/authorization — will be reported honestly if not possible from this environment)* |

Cross-service contract check: `rejectedIds` present in Go DTO + parsed by client deserializer; serialized contract verified with a live round-trip (curl one app-items sync with a deliberately orphaned entry).

---

## 6. Rollout order

1. **Server first** (additive DTO field + writer stamps + migration 033 in the same deploy window). Old clients unaffected.
2. **Client in the next installer build** (Installer-Parity Rule; no config re-bake).
3. No web deploy required.

## 7. Risks & honest unknowns

- **Old clients + new server:** orphans keep being lost for machines that never update — unchanged behavior, no regression; the server WARN log remains the visibility source until clients update.
- **`SET NOT NULL` on `app_items.synced_at`:** any unknown writer that inserts without the column would now fail loudly instead of silently storing NULL — intentional fail-fast; the DDL default covers plain INSERTs, and all known writers are updated in this change.
- **Installer ship-test** requires the Linux test machine; if unavailable from this environment it will be reported as a blocker, not claimed done.
- Quarantine threshold (3 consecutive refusals) is configurable-in-code constant; no new env knob (avoid `config.enc` re-bake). Revisit only if real-world false-positives appear.

---

*Awaiting acceptance. On approval: implement in checklist order (§2.3, §4), verify per §5, update AGENTS.md changelog, and report with the three-tier verification status.*
