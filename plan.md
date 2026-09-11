# Implementation Plan — Fix Employee-Journey "Running" Bugs & Apps-Page UX (Improved Scalability)

**Build gates:** after each phase run `go build && go vet`, `npx tsc --noEmit`, `next build`, `dotnet build` (0 warnings).

---

## Phase 0 — Data Repair (Scalable Migration)
- [ ] **Create migration `035_fix_stranded_sessions.sql`** (already present) that:
  ```sql
  UPDATE app_sessions
     SET status = 'CLOSED',
         ended_at = COALESCE(last_activity_at, last_sync_at, started_at)
   WHERE ended_at IS NULL
     AND status IN ('OFFLINE','STALE')
     AND last_sync_at < NOW() - make_interval(hours => 24);
  ```
- [ ] **Run migration on all environments** using the standard `goose`/`sql-migrate` pipeline to guarantee consistency.
- [ ] **Verification:** query `SELECT COUNT(*) FROM app_sessions WHERE status='OFFLINE' AND ended_at IS NULL;` – must be 0.

---

## Phase 1 — Server Defensive State Machine
- **1.1** Update `AggregateAppSessionsUsage` (`server/internal/repository/new_schema_repo.go`):
  ```go
  BOOL_OR(status = 'ACTIVE' AND ended_at IS NULL) AS has_open_session
  ```
- **1.2** Add projection `last_active_at`:
  ```go
  MAX(COALESCE(last_activity_at, last_sync_at, ended_at, started_at)) AS last_active_at
  ```
- **1.3** Extend `AppSessionUsageRow` struct with `LastActiveAt time.Time` and ensure JSON DTO includes it.
- **1.4** Implement **per‑row stagnation sweep** in `session_lifecycle_sweep.go` after the machine‑level sweep:
  ```go
  UPDATE app_sessions
     SET status='CLOSED', ended_at=COALESCE(last_activity_at, last_sync_at, started_at)
   WHERE ended_at IS NULL
     AND status IN ('OFFLINE','STALE')
     AND last_sync_at < NOW() - make_interval(hours => $1);
  ```
- **1.5** Add unit test `TestAggregateHasOpenSession` covering ACTIVE vs OFFLINE rows.
- **1.6** Consistency check: ensure DTO mapping in `ListAppSessionsForApp` respects new fields.

---

## Phase 2 — Web `/employee-journey/apps` UX & Accuracy
- **2.1** Status & Last Closed rendering:
  - Aggregate row: use `SessionStatusBadge` based on `status` (only ACTIVE shows green "Running").
  - Expanded row "Closed" column: replace literal "Running" with the same helper used on the timeline (`endIso`).
- **2.2** Column redesign (I‑04):
  - Headers → `Application | Sessions | Duration | Last Active | Status`.
  - Wire `Last Active` to the new `last_active_at` field.
- **2.3** Tile redesign (I‑09):
  - Rename "Open Now" → "With open sessions" (compute from ACTIVE rows).
  - Rename "Active Time" → "Total session time" (sum of `totalDurationSeconds`).
- **2.4** Title column (I‑07): rename header to "Context" and keep rendering `s.contextLabel`.
- **2.5** Edge grouping (I‑11): add a client‑side map that normalises `process_name` values `msedge` and `msedgewebview2` to a single display name.
- **2.6** Apply visual polish per design guidelines (gradient header, subtle hover effects, modern font).

---

## Phase 3 — Timeline & Web Fixes
- **3.1** Fix Tailwind typo `py-px` → `py-0.5` in `timeline/page.tsx`.
- **3.2** Closed column: show `lastSyncAt` for STALE/OFFLINE rows instead of an em‑dash.
- **3.3** Web page durations (I‑08): for open tabs use `now - openedAt` (or `lastSyncAt`).
- **3.4** Add micro‑animation on row hover (fade‑in background, scale‑up badge).

---

## Phase 4 — Client Root‑Cause Fixes (Installer‑Gated)
- **4.1** Windows shutdown (`SystemEventWatcher` → `SessionEnding`) – ensure `CloseSessionsAndAppItemsAsync()` is called to stamp `ended_at`.
- **4.2** Boot reconciliation – verify `ReconcileStaleSessionsOnBootAsync` re‑queues rows with `is_synced=0` after adding `ended_at`.
- **4.3** Add defensive logging around the shutdown path (log when `ended_at` is written).
- **4.4** Build installer (`bash publish/build-installer.sh -b windows`) and run a smoke test that powers off the VM and checks the DB for closed rows.

---

## Phase 5 — Documentation & Verification
- **5.1** Update `AGENTS.md` & `server/ARCHITECTURE.md` with change summary.
- **5.2** Run full verification suite:
  - Server: `go build && go vet`
  - Web: `npx tsc --noEmit && next build`
  - Client: `dotnet build`
- **5.3** Live DB audit – re‑run the audit CLI and assert:
  - No OFFLINE rows with `ended_at IS NULL`
  - `has_open_session` matches only ACTIVE rows.
- **5.4** Publish a short release note for the ops team.

---

## Phase 6 — Monitoring (Post‑deployment Safety Net)
- **6.1** Add Grafana alert: `SELECT COUNT(*) FROM app_sessions WHERE status='OFFLINE' AND ended_at IS NULL` > 0 → fire.
- **6.2** Schedule a nightly job (`cron '0 2 * * *'`) that runs the per‑row sweep with a 48‑hour window as a safety fallback.

---

**All phases are checklist‑driven; once every box is ticked the next phase may commence.**