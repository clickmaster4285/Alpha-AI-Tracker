# Implementation Plan — Fix Employee-Journey "Running" Bugs & Apps-Page UX

**Go/next.js/build gates:** keep `go build`/`go vet`, `npx tsc --noEmit`, `next build`, `dotnet build` (0/0) green after every phase. Deploy order matters (server first, then web, then client installer).

---

## Phase 0 — Data repair (live DB, run once, idempotent)

Backfill the stranded rows so the UI stops lying immediately. Matches the existing migration-034 pattern (status=CLOSED where ended_at not null) extended to long-stale end-less rows:

```sql
-- Freeze sessions that stopped being synced before the close window
UPDATE app_sessions
   SET status   = 'CLOSED',
       ended_at = COALESCE(last_activity_at, last_sync_at, started_at)
 WHERE ended_at IS NULL
   AND status IN ('OFFLINE','STALE')
   AND last_sync_at < NOW() - make_interval(hours => 24);
```
- Run once (as a migration **035** and/or a psql one-liner) for the current 50 rows.
- **Do NOT** `DELETE` — keep them as CLOSED for history; retention already purges closed rows per `RETENTION_DAYS=168`.

---

## Phase 1 — Server (defense in depth, no client needed)

### 1.1 `AggregateAppSessionsUsage.has_open_session` must respect lifecycle state
`server/internal/repository/new_schema_repo.go`:
```sql
-- OLD: BOOL_OR(ended_at IS NULL)  -> OFFLINE/STALE rows falsely count as open
-- NEW:
BOOL_OR(status = 'ACTIVE' AND ended_at IS NULL) AS has_open_session
```
Also add `MAX(COALESCE(last_activity_at, last_sync_at, ended_at, started_at)) AS last_active_at` to the SELECT + `AppSessionUsageRow` so the web can show **Last Active** (I-04) from one endpoint.

### 1.2 Companion per-row stagnation sweep (fix I-02 at the source)
`server/internal/jobs/session_lifecycle_sweep.go` — add a step **after** the per-machine steps:
```
UPDATE app_sessions
   SET status='CLOSED', ended_at=COALESCE(last_activity_at, last_sync_at, started_at)
 WHERE ended_at IS NULL
   AND status IN ('OFFLINE','STALE')
   AND last_sync_at < NOW() - make_interval(hours => closeAfterHours);
```
Rationale: a row whose own last_sync is `>= CLOSE_AFTER` old is never coming back, even if another row on the machine is still active. This prevents the 50-row strangulation permanently.

### 1.3 (Consistency) `ListAppSessionsForApp` / `ListAppSessions` already project `status/lastActivityAt/lastSyncAt` — verify DTO mapper sets them (it does per service mapper; add a regression assertion).

---

## Phase 2 — Web `/employee-journey/apps` (your Q3 + Q2/Q4 rendering)

`web/src/app/(app)/employee-journey/apps/page.tsx`:
1. **Status/Last Closed honesty (I-01, I-03):**
   - Aggregate row: render `SessionStatusBadge` from a new derived status — `ACTIVE` if `hasOpenSession` (now ACTIVE-only), otherwise if no session in range is ACTIVE show "Closed". Never print the literal "Running" in the Last Closed cell.
   - Expanded row "Closed" cell: replace `s.endedAt ? … : Running` with the timeline's `endIso` logic (CLOSED→endedAt, STALE/OFFLINE→lastSyncAt, ACTIVE→now + "Running" only when ACTIVE).
2. **Column redesign (I-04):** `Application | Sessions | Duration | Last Active | Status` — drop "First Opened"/"Last Closed" from the aggregate (or keep just "Last Active"). Wire Last Active to the new `lastActiveAt` field from 1.1.
3. **Tiles (I-09):** rename "Open Now" → "With open sessions" OR recompute from ACTIVE-only count; disambiguate "Active Time" (sum) vs row "Duration" (range) by re-labelling the tile "Total session time" or the column "Active range".
4. **Title column (I-07):** rename header to "Context" (it renders `contextLabel`) or re-render an actual title.

---

## Phase 3 — Web timeline + web (small)

`web/src/app/(app)/employee-journey/timeline/page.tsx`:
- Fix `py-px` → `py-0.5` (I-05).
- Closed column: show `lastSyncAt` for STALE/OFFLINE instead of `'—'` (I-06), consistent with Duration.

`web/src/app/(app)/employee-journey/web/page.tsx`:
- `visitDurationSeconds`: for `closedAt == null`, use `now − openedAt` (or the session `lastSyncAt`) so open tabs contribute duration (I-08).

---

## Phase 4 — Client (installer-gated; root-cause fix)

1. **Finalize open sessions on shutdown** — on the Windows `power_off` path (`SystemEventWatcher` `SessionEnding`, 2026-09-05) and within `ShutdownSentinel`, call the existing `CloseSessionsAndAppItemsAsync()` so every open session gets `ended_at` before the OS kills the process. (Linux SIGTERM path already does this; Windows is the gap. Ships only in a new installer build.)
2. **Boot reconcile re-sync** — verify `ReconcileStaleSessionsOnBootAsync` re-queues the closed rows (`is_synced=0`) so the server upsert finalizes them (I-02 fix point 2).
3. Re-bake `config.enc` if any env knob changes (none required for this fix).

---

## Phase 5 — Docs & verification

1. Update `AGENTS.md` / `server/ARCHITECTURE.md` changelogs: "apps page `hasOpenSession` is ACTIVE-only", "per-row stagnation sweep", "apps columns = Sessions/Duration/Last Active/Status".
2. Verify:
   - `go build` && `go vet` clean
   - `npx tsc --noEmit` clean && `next build` passes
   - `dotnet build` 0 warnings / 0 errors
   - Live: re-run the audit probe → EMP-10002's 71 sessions should now read **CLOSED 59 / ACTIVE 12**, Chrome/Edge/Unity no longer "Running"
   - Installer: `bash publish/build-installer.sh -b linux` for the client change (per Installer-Parity Rule).