# QA / UI-UX Audit — Employee Journey Pages

**Audit target:** 3 web pages against the **live** server + **live PostgreSQL** data.
- `/employee-journey/timeline?preset=7d&employeeId=c1f1d4c3-04ba-4002-972f-a43b41f97863`
- `/employee-journey/apps?preset=today&employeeId=c1f1d4c3-04ba-4002-972f-a43b41f97863`
- `/employee-journey/web?preset=today&employeeId=c1f1d4c3-04ba-4002-972f-a43b41f97863`

**Environment read (from `server/.env` / `client/.env`):**
- Server: `http://192.168.88.35:8080`, `SERVER_HOST=192.168.88.35`
- PostgreSQL: `localhost:5432`, db=`alpha_ai_tracker`, user=`alpha_monitoring`
- Lifecycle env: `SESSION_OFFLINE_AFTER_MINUTES=10`, `SESSION_STALE_AFTER_MINUTES=60`, `SESSION_CLOSE_AFTER_HOURS=24`, `SESSION_SWEEP_INTERVAL_SECONDS=60`
- Client points at the same server; `ALPHA_SYNC_RETENTION_HOURS=168`

**Live-data fingerprint (taken 2026-09-11 ~10:34 PKT / 05:34 UTC):**
- URL `employeeId=c1f1d4c3-04ba-4002-972f-a43b41f97863` resolves to employee **`EMP-10002` "amir"** (EmployeePage matches the URL value against `Employee.id` UUID, then queries sync tables by `employee.employeeId` = `EMP-10002`).
- `app_sessions` for EMP-10002 = **71 rows → ACTIVE 12 / OFFLINE 50 / STALE 0 / CLOSED 9**.
- All 50 OFFLINE rows are the **Sep-10 afternoon/evening** sessions (Chrome 17:50, Alpha AI Tracker 17:49, Edge/msedgewebview2, Unity, VS Code, Task Manager, WhatsApp, pgAdmin…), `ended_at = NULL`, `last_sync_at` ~17:47–18:15 on **Sep 10**.
- Timesheets `/attendance/range` Sep-10 row: `firstActiveAt=15:50:15`, **`lastActiveAt=18:08:10 +05:00`** (= the "Sep 10, 06:08 PM" the user saw). Sep-11 row: `firstActiveAt=09:00:04` → **the machine came back online Sep 11 09:00** and is currently ACTIVE (the user believed the PC was still off).

---

## Issue Register

### I-01 — CRITICAL — Apps page shows green **"Running"** for rows that are OFFLINE/never-finalized
**Where:** `server/internal/repository/new_schema_repo.go` → `AggregateAppSessionsUsage`:
```sql
BOOL_OR(ended_at IS NULL) AS has_open_session
```
and `web/src/app/(app)/employee-journey/apps/page.tsx` (Status col lines 160-171, Last Closed col lines 174-179; also `runningCount` line 77 → "Open Now" tile line 114).

**Why it's wrong:** `hasOpenSession` keys on `ended_at IS NULL`, but the 4-state model means an **OFFLINE** or **STALE** session also has `ended_at = NULL`. 50 of 71 sessions are OFFLINE → every app group that contains one (Chrome, Edge, Unity, VS Code, Task Manager, WhatsApp, pgAdmin, Alpha AI Tracker, OneDrive…) gets `hasOpenSession=true` → the aggregate row renders a green pulsing **"Running"** pill in the Status column AND the literal text **"Running"** in the Last Closed column. **All 5 user questions (Q2/Q3/Q4/Q5) trace to this.**
The timeline page does the RIGHT thing: `SessionStatusBadge` reads the projected `status` field, so OFFLINE rows correctly render **"Offline · X"**.

**Fix:** `BOOL_OR(status = 'ACTIVE' AND ended_at IS NULL) AS has_open_session`. Any row where `status` is `OFFLINE`/`STALE`/`CLOSED` must never contribute to "Running".

---

### I-02 — CRITICAL — 50 sessions stranded in OFFLINE forever (lifecycle can't advance them)
**Where:** `server/internal/jobs/session_lifecycle_sweep.go` (steps 2/3) filters rows by:
```sql
AND machine_id NOT IN (
  SELECT DISTINCT machine_id FROM app_sessions
   WHERE last_sync_at > NOW() - make_interval(secs => $1)
)
```
**Why it's wrong / why it bit us:** the OFFLINE→STALE→CLOSED transitions are **per-machine**: if *any* row for `machine_id=4f7e7ce1…` has synced recently (it did — Sep-11 10:32), the machine is treated as "alive" and **none** of its OFFLINE rows advance. So the Sep-10 evening sessions — which were never finalized by the client (`ended_at = NULL`) — sit in OFFLINE indefinitely even though they are 16 h old. Combined with I-01, they read as "Running" forever on `/apps`.

**Root cause upstream:** the client did not persist `ended_at` for its open sessions at the Sep-10 shutdown. 50 rows reached the server without `ended_at`; once synced (`is_synced=1` locally) they are never re-sent, so the upsert's "resurrect/CLOSE" logic never runs. (Likely candidate: amir's *installed* client predates the Windows `power_off`/`SessionEnding` handling that ships only in a new installer — `SystemEventWatcher` 2026-09-05; and boot-reconcile did not re-close+re-sync them.)

**Fix (layered):**
1. **Server (defense in depth):** don't rely solely on per-machine availability — when a row's own `last_sync_at`/`last_activity_at` is `> CLOSE_AFTER` old **and** `ended_at IS NULL`, the row should be frozen to `CLOSED` (the session genuinely stopped; a live machine only keeps a row ACTIVE if the client keeps re-sending it). I.e. add a per-row stagnation sweep as a companion to the per-machine sweep.
2. **Client:** finalize (`ended_at = now`) all open sessions on graceful shutdown (**already the intent of `CloseSessionsAndAppItemsAsync` + `ShutdownSentinel` on SIGTERM/Ctrl+C** — but this must also fire on the Windows `power_off` path, which is installer-gated), and **on boot** reconcile any open session not seen since last heartbeat + re-sync it (`ReconcileStaleSessionsOnBootAsync` already exists — verify the ended_at update is re-queued `is_synced=0`).
3. **Data repair** for the current 50 rows (see plan Phase 0): backfill `status='CLOSED', ended_at=COALESCE(last_activity_at, last_sync_at, started_at)`.

---

### I-03 — HIGH — Expanded per-session "Closed" column lies (shows "Running" while the Status badge says "Offline")
**Where:** `appSessionsApi.usageSessions` (from `ListAppSessionsForApp`) → apps/page.tsx lines 283-296:
```jsx
<td>{s.endedAt ? formatDateTime(s.endedAt) : <span className="text-warning">Running</span>}</td>
```
**Why:** the "Closed" cell is computed solely from `endedAt`, so an OFFLINE/STALE row (endedAt NULL) renders **"Running" (orange)** in the same row where the last column's `SessionStatusBadge` correctly renders **"Offline"**. One row simultaneously says "Running" and "Offline" — the exact contradiction in Q2 ("12+ sessions and 5+ running but the app is closed"). The timeline page already has the correct helper (`endIso`): CLOSED→`endedAt`, STALE/OFFLINE→`lastSyncAt`, ACTIVE→`now` — the expanded apps table should reuse it.

---

### I-04 — HIGH/UX — Apps page column semantics broken (your Q3)
Current headers (line 121): `Application | Sessions | Duration | Status | First Opened | Last Closed`. Problems:
1. **"Status" and "Last Closed" both render "Running"** (duplicate/misleading) — see I-01.
2. **"First Opened"** is rarely useful on a 100-session aggregate (open-range of the whole group).
3. **"Duration" is not the sum** — it is `lastClosed − firstOpened` (open-range) so chrome 3-tabs×10m = 10m not 30m (intended), **but** the top "Active Time" tile sums `totalDurationSeconds` (the per-session SUM). Two different "duration" semantics appear on one page with identical-looking units → "Active Time" tile can exceed any row's Duration. Confusing.
4. The page has **no "Last Active"** and no honest per-app status.

**Recommendation (matches your instinct):** aggregate row = `Application | Sessions | Duration | Last Active | Status`.
- **Status:** green "Running" only when `status='ACTIVE'` (fix I-01); "Offline"/"Stale"/"Closed" otherwise (reuse `SessionStatusBadge`).
- **Last Active:** `MAX(COALESCE(lastActivityAt, lastSyncAt, endedAt, startedAt))` — directly answers "when did this app last do anything".
- **Duration:** keep open-range (`lastClosed−firstOpened`) to preserve the multi-tab fix, but label it clearly OR rename the tile to "Total session time" and keep the row as "Active range". Surface the per-session open-range in the expanded row instead of the four aggregate columns.
Drop per-app "First Opened"/"Last Closed" (or keep only "Last Active"). This is exactly the "Sessions / Duration / Last Active / Status" shape you proposed.

---

### I-05 — MEDIUM — Timeline page `py-px` typo (invalid Tailwind class)
**Where:** `web/src/app/(app)/employee-journey/timeline/page.tsx` line 151:
```jsx
className="px-1.5 py-px rounded text-[10px] font-mono bg-primary/10 text-primary capitalize"
```
`py-px` is not a valid spacing utility; the platform chip loses vertical padding (renders cramped). Should be `py-0.5` (or `py-1`).

---

### I-06 — MEDIUM — Timeline "Closed" column shows a bare em-dash for STALE/ACTIVE
**Where:** timeline/page.tsx line 162 — `{s.endedAt ? formatDateTime(s.endedAt) : '—'}`. For an OFFLINE/STALE row the Duration column correctly uses `lastSyncAt` but the Closed column shows `—`. Inconsistent: better to render the actual effective end (`lastSyncAt` when STALE/OFFLINE) or leave `—` but document via the Status badge. (Minor.)

---

### I-07 — MEDIUM — Expanded apps "Title" column shows `contextLabel`, not the window title
**Where:** apps/page.tsx lines 271 header `'Title'` vs cell lines 289-291 rendering `s.contextLabel`. On sessions without a workspace/profile label the cell is `—` even when the window had a real title. Either rename the header ("Context" / "Workspace · Profile") or actually map `contextLabel`.

---

### I-08 — LOW — Web page durations undercount currently-open tabs
**Where:** web/page.tsx `visitDurationSeconds` (lines 67-70): returns `0` when `closedAt` is null. An open tab contributes 0 to its group's `Duration`. Consider `now − openedAt` for still-open items (or `lastSyncAt`), matching the 3-state duration end used elsewhere.

---

### I-09 — LOW/INFO — "Open Now" tile is misleading
**Where:** apps/page.tsx lines 77 + 114 — `runningCount = usage.filter(u => u.hasOpenSession).length` labelled "Open Now". Because `hasOpenSession` includes OFFLINE (I-01), "Open Now" overstates live apps. Fix labeling to "With open sessions" or drive it from `status='ACTIVE'`.

---

### I-10 — INFO — Timesheet vs apps page use different data sources (explains the "why" of Q5)
- `/timesheets` rows come from `GET /attendance/range` → client-computed **daily attendance rollup** (`lastActiveAt` = last activity that day → **18:08:10 PKT on Sep 10** = "PC off 06:08 PM").
- `/employee-journey/apps` rows come from `app_sessions` whose **status is decided by the server sweep**, which is strangled by the per-machine rule (I-02).
So the two pages *can* disagree for the same shutdown — not a bug in timesheets, but a consequence of the stranded lifecycle + the apps "Running" bug. **Also note the machine came back online Sep-11 09:00 and is actively syncing** — so "PC off since 10 Sep" is no longer literally true; the real defect is that the Sep-10 sessions should have been finalized/closed, not that the whole PC is off right now.

---

### I-11 — LOW — Windows Edge fragments into multiple aggregate groups (`msedge` vs `msedgewebview2`)
**Where:** apps/page.tsx lines 152-154 + server GROUP BY `(app_display_name, process_name)`. Windows Edge appears as `msedge` (browser) AND `msedgewebview2` (embedded webviews) → several "Microsoft Edge" rows. Not wrong, but adds noise behind I-01's "Edge shows Running" confusion (Q4). Consider a documented display grouping (e.g. collapse `*webview*` into the parent app) — explicitly a backlog item, not part of the quick fix.

---

## Direct answers to your questions

1. **Deep audit of the 3 pages** — see the Issue Register; the pages are otherwise well-built (URL-synced filters, infinite scroll, `keepPreviousData`, Suspense boundaries, honest empty/error states). The functional/UX problems all funnel into I-01..I-04.

2. **"12+ sessions and 5+ running but the app is closed"** — the per-app "Running" is computed from `ended_at IS NULL` (I-01), and the expanded "Closed" column also uses `endedAt` (I-03). Sessions the tracker never finalized are OFFLINE with `ended_at=NULL`, so both render "Running". The timeline's status badge is the honest signal ("Offline"); the apps page isn't using it.

3. **Columns "First Opened / Last Closed" purpose** — they were introduced on 2026-09-04 to expose the open-range that drives the (corrected) multi-tab "Duration". But combined with I-01 they mislead ("Last Closed" literally prints "Running"), and "First Opened" is noise on a big aggregate. Your suggested **Sessions / Duration / Last Active / Status** is better — see I-04 for the exact change.

4. **Closed Edge but still shows Running** — Edge's `ended_at` was never persisted (client was shut down with the machine on Sep-10; those rows are OFFLINE on the server). `hasOpenSession` treats them as open (I-01); plus `msedge` + `msedgewebview2` split Edge into multiple groups (I-11).

5. **Sep-10 05:50 Chrome & 05:49 Alpha AI Tracker still "Running" while the PC "off" at 06:08 PM** — timesheets "06:08 PM" is the *attendance rollup* for Sep-10 (`lastActiveAt`). The apps page sessions are *raw `app_sessions`* whose status the sweep froze at OFFLINE (I-02) and whose `ended_at` is NULL, so the apps page calls them "Running" (I-01). The machine actually resumed Sep-11 09:00. Root defect: the lifecycle can't advance stranded rows on a live machine, and the apps page mislabels OFFLINE as Running.