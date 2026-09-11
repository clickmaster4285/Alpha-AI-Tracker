# Task Checklist — All Phases

## Phase 0 — Data Repair Migration
- [x] Migration 035 file already present
- [x] Migration 035 already applied to live DB + verified: `schema_migrations` has it AND `OFFLINE & ended_at IS NULL = 0` (2026-09-11)

## Phase 1 — Server
- [x] BOOL_OR fix already in repo (line 907)
- [x] LastActiveAt projection already in repo (line 908)
- [x] AppSessionUsageRow struct already has LastActiveAt
- [x] DTO AppUsageRow already has LastActiveAt
- [x] Per-row stagnation sweep already in session_lifecycle_sweep.go (lines 199-214)
- [x] Service mapper already passes LastActiveAt

## Phase 2 — Web apps page
- [x] Status badge uses hasOpenSession (line 162 — ACTIVE vs CLOSED)
- [x] Expanded row Closed cell uses STALE/OFFLINE→lastSyncAt (lines 268-272)
- [x] Columns: Application | Sessions | Duration | Last Active | Status (line 121)
- [x] Tiles renamed: "Total session time" / "With open sessions" (lines 113-114)
- [x] Edge grouping (msedge/msedgewebview2 collapse to one aggregate via `usageProcessName` + `mergeUsage` in apps/page.tsx)

## Phase 3 — Timeline / Web fixes
- [x] py-px → py-0.5 already fixed (line 151)
- [x] Timeline Closed column STALE/OFFLINE → lastSyncAt (lines 164-166)
- [x] Web page open-tab duration: uses `now − openedAt` when closedAt is null (web/page.tsx)

## Phase 4 — Client shutdown hardening
- [x] SessionEnding fires power_off event
- [x] SessionEnding calls `CloseAllOpenSessionsOnShutdownAsync()` → `CloseSessionsAndAppItemsAsync` for all open sessions
- [x] CloseSessionsAndAppItemsAsync already sets is_synced=0 (DatabaseSchema.cs line 632)
- [x] ReconcileStaleSessionsOnBootAsync calls CloseSessionsAndAppItemsAsync on boot

## Phase 5 — Verification
- [x] go build && go vet — clean (0/0)
- [x] npx tsc --noEmit — clean; next build — succeeded (62 static pages)
- [x] dotnet build — 0 warnings / 0 errors
- [x] Migration — verified already applied (no-op); stranded OFFLINE rows = 0

## Post-checklist extras (plan.md items not in the original checklist)
- [x] 2.6 Visual polish (apps page) — gradient accent bar + `animate-fade-in`
- [x] 3.4 Micro-animation — `SessionStatusBadge` hover scale (`hover:scale-105`) + timeline fade-in
- [x] 5.1 Docs — AGENTS.md + server/ARCHITECTURE.md changelogs updated (incl. migration-tool "latest: 035")
- [x] 5.3 Live DB audit — code invariants hold: 0 ACTIVE-and-closed, 0 groups flagged Running w/o an ACTIVE row; 20 transient STALE rows (<24h window, expected; will close at 24h)
- [x] 5.4 Release note — new `RELEASE_NOTES.md` (incl. Phase 6 ops/monitoring runbook)
- [ ] 4.4 / Phase 6 infra — Windows installer build + power-off smoke test, and Grafana alert/cron provisioning: out-of-repo/infra, documented as deferred in RELEASE_NOTES.md
