# Dashboard Redesign Plan

> **Scope:** `web/src/app/(app)/dashboard/page.tsx` + new `web/src/components/dashboard/*`  
> **Goal:** Ops command center for admins — live data only, actionable, scalable.  
> **Audited:** 2026-10-05 against live Postgres `alpha_ai_tracker` + web/server API surface.  
> **Constraint:** No fake productivity / executive scores. Scaffolding pages are quick-links at most.

---

## 0. Live DB inventory (what the dashboard should actually show)

Snapshot from the running DB (soft-deleted rows excluded where applicable):

| Table / signal | Live count | Dashboard relevance |
|---|---:|---|
| `employees` | **53** | Headcount KPI |
| · tracked (`tracking_status='tracked'`) | **8** | Fleet onboarding gap — **primary action** |
| · untracked | **45** | CTA → generate secret / install client |
| · `is_online = true` (DB flag) | **8** | Stale vs presence — **do not trust alone** |
| `departments` | **12** | Dept tile + optional headcount bar |
| `employee_devices` (active) | **11** | Fleet health / versions |
| · seen last 15 min | **2** | True “recently syncing” signal |
| · seen last 24 h | **4** | |
| · stale ≥ 7 d | **5** | “Needs attention” |
| Client versions in field | 1.0.0×4, 1.2.34×3, mixed older | Version fragmentation tile |
| `app_sessions` | **3,509** | Activity core |
| · today / 24h | **186** | KPI |
| · `ACTIVE` open | **21** | “Apps open now” |
| · `STALE` | **18** | Session health (orphan risk) |
| · `CLOSED` | **3,470** | |
| `app_items` | **94,113** | Web + journey volume |
| · browser_tab today | **827** | KPI |
| · browser_navigation today | **690** | Secondary |
| `installed_applications` | **278** | Classification backlog |
| · unclassified (type OR category null) | **278 (100%)** | **High-value admin task** |
| `monitoring_sites` | **197** | |
| · unclassified | **197 (100%)** | Same backlog |
| `monitoring_types` / `categories` | 3 / **0** | Types seeded; categories empty |
| `session_events` (today) | idle/power/lock mix | Attendance-ish raw fuel — **no org API yet** |
| `location_samples` | **0** | Skip GPS widgets |
| `company_holidays` | **0** | Skip holiday widget |
| `users` / `roles` | 1 / 1 | Not workforce KPIs |

**Top apps today (org SQL):** Chrome 79 · VS Code 21 · pgAdmin 14 · Search 13 · Edge 12…  
**Top domains today:** github.com · google.com · LAN hosts · alphamonitoring… · chatgpt.com · youtube.com · web.whatsapp.com  
**Open sessions now:** concentrated on 2 employees (MU-17, MU-90).

### What this means for the home page

1. **Fleet gap first** — 45/53 untracked dominates; dashboard must push install/track, not fake productivity.
2. **Activity is rich** — sessions + web tabs today justify Top Apps / Recent / Web KPIs immediately.
3. **Classification is empty** — Configuration backlog widget is more useful than a blank “Productive/Unproductive” card.
4. **Presence ≠ `employees.is_online`** — devices seen_15m (2) ≠ online flag (8). Use `GET /live-stream/employees` for Online Now.
5. **GPS / holidays / DLP / shadow-IT / AI / executive scores** — empty or scaffolding; keep off the data plane.
6. **Attendance / hours-insights** — live per-employee only; org rollup needs a new aggregate (don’t N+1 53×).

---

## 1. Why redesign (current page)

| Current piece | Problem |
|---|---|
| Full-page `Loader2` for 5 queries | One slow call blanks everything |
| 4 count-only tiles | Misses the real story (45 untracked, 100% unclassified, online now) |
| Giant `DownloadAppSection` hero | Correct for day-0, wrong every day after |
| Productive / Unproductive empty card | No aggregate; with 0 classifications it would stay empty forever |
| No date / department URL filters | Breaks URL-Synced Filters Rule |
| Ignores live-stream + monitoring APIs | Best live signals unused |

---

## 2. Design principles

1. **Live data only** — every number from an API; empty/coming-soon when missing.
2. **Composable widgets** — own query + skeleton + error; one failure never blanks the page.
3. **Skeleton over spinner** — per-widget loading.
4. **URL filters** — `preset` / `from` / `to` / optional `departmentId` via `useUrlQueryState` / `useUrlActivityFilter`.
5. **Actionable** — every widget deep-links to a real live module.
6. **Presence from live-stream list** — not `Employee.isOnline`.
7. **No N+1** — never fan out attendance/hours per employee on the home page.
8. **RBAC-aware** — hide widgets whose module the user can’t access.
9. **Existing design system** — `StatsCard`, shadcn, Tailwind tokens, light motion.

---

## 3. Feature readiness matrix (other modules)

### READY now (put on dashboard as data)

| Info | Source | Notes |
|---|---|---|
| Employees total / tracked / untracked | `employeesApi.list({ status, perPage: 1 })` | Matches DB 53 / 8 / 45 |
| Departments (+ headcounts) | `departmentsApi.list()` | 12 depts; `employeeCount` on rows |
| Sessions in date range | `appSessionsApi.list` **org-wide** (`employeeId` optional) | Indexed `started_at` |
| Browser tabs in range | `appItemsApi.list({ itemType: 'browser_tab' })` org-wide | |
| Open / usage rollup | `appSessionsApi.usage` **org-wide** (`employeeId` optional) | `openSessionCount`, `totalSessionCount`, top rows |
| Online / streaming / consent gaps | `liveStreamApi.employees()` | Prefer over DB `is_online` |
| Unclassified apps / sites | `monitoringApi.apps/websites.list({ unclassified: true, perPage: 1 })` | Live DB: 278 / 197 |
| Client version (weak) | `Employee.clientVersion` on employee list page | No org device list in `api.ts` yet |
| Shifts count | `shiftsApi.listAll()` | Config context only |

### PARTIAL (deep-link only until aggregate exists)

| Feature | Why not a home KPI yet |
|---|---|
| Attendance today present/late/absent | `attendanceApi.today/range` **requires `employeeId`** — page already N+1s up to 100 |
| Hours insights productive split | `GET /hours-insights` **requires `employeeId`** |
| Device `lastSeenAt` / platform distribution | Server `GET /employees/:id/devices` exists; **no web `devicesApi`**, no org endpoint |
| Session events (idle/power) | Sync-only; no admin list/aggregate |
| Location / geofence | Tables exist; `location_samples=0`; UI gated `LOCATION_UI_ENABLED=false` |
| Terms consent | Per-employee; consent-missing already on live-stream list |

### SCAFFOLDING — quick-links only (never data tiles)

`/executive-dashboard` (hardcoded), `/shadow-it`, `/dlp-alerts`, `/dlp-rules`, `/productivity-scoring`, `/ai-summary`, `/reports`, `/screenshots`, `/employees/activity`, `/logs/insights`, `/logs/graphical`, `/configuration/productivity-rules`, KPIs/goals/projects/emails/charts/*, billing mocks.

**Live destinations for Quick Actions:** Employees, Departments, Live Stream, Session Timeline, App Usage, Web Activity, Configuration Apps/Websites, Attendance (per-employee page), Hours Insights (per-employee).

---

## 4. Information the dashboard SHOULD show (ranked)

### Tier A — ship in Phase 0–1 (proven in DB + APIs)

1. **Workforce KPIs** — Total · Tracked · Untracked (emphasize untracked CTA).
2. **Activity KPIs** — App sessions (range) · Web tabs (range) · Open sessions now (`usage.openSessionCount` or ACTIVE count).
3. **Presence KPI** — Online now · Streaming now (from live-stream list).
4. **Departments** — count; optional mini list of top depts by `employeeCount` (Marketing 31, Web-Dev 13…).
5. **Online Now panel** — avatars/names from `liveStreamApi.employees`, link to theater/`?ids=`.
6. **Fleet Health** — tracked/untracked + “X devices on old client versions” (from employee list `clientVersion` sample, honest about incompleteness).
7. **Classification backlog** — unclassified apps/sites totals → Configuration.
8. **Quick Actions** — permission-gated links to live modules only.
9. **Dismissible download strip** — footer, not hero.

### Tier B — Phase 2 (org-wide lists already work)

10. **Recent Sessions** — `appSessionsApi.list` org-wide, capped (e.g. 10), `SessionStatusBadge`, deep-link journey.
11. **Top Apps** — `appSessionsApi.usage` org-wide, `perPage: 5–8`, date range from URL (DB already proves Chrome/VS Code/…).
12. **STALE session callout** — count of `status=STALE` if cheap (usage or dedicated filter); else fold into Fleet/Activity footnote.

### Tier C — Phase 3 server summary (scale + missing joins)

13. **Top Domains** — needs SQL `GROUP BY domain` (list API doesn’t aggregate).
14. **Single `GET /dashboard/summary`** — collapse KPI fan-out; include top apps/domains + unclassified + open/stale.
15. **Org attendance today** — present/late/absent (new aggregate over schedules + session_events).
16. **Org hours-insights rollup** — productive/unproductive once classifications exist (today 0% classified → scores would be meaningless).
17. **Fleet device summary** — last_seen buckets + version histogram from `employee_devices` (DB already has it).

### Explicitly OUT of the dashboard data plane

- Productive % / org productivity score / cost-per-hour (executive mock).
- DLP / Shadow IT / AI summary / custom reports.
- GPS map / geofence alerts (empty + UI gated).
- Holidays strip (table empty; not home-critical).
- Screenshot wall.

---

## 5. Target layout

```
┌──────────────────────────────────────────────────────────────────┐
│ Header: title + date preset (URL) [+ optional department]        │
├────────┬────────┬────────┬────────┬────────┬─────────────────────┤
│ Total  │Tracked │Online  │Sessions│Web tabs│ Open sessions       │
│ emps   │/Untrk  │ now    │(range) │(range) │ now                 │
├────────────────────────────┬─────────────────────────────────────┤
│ Who’s Online (presence)    │ Fleet Health                         │
│ live-stream employees      │ untracked · stale clients · versions │
├────────────────────────────┴─────────────────────────────────────┤
│ Classification backlog     │ Quick Actions (live modules only)    │
│ apps + sites unclassified  │ Employees · Live · Journey · Config  │
├────────────────────────────┬─────────────────────────────────────┤
│ Top Apps (usage, range)    │ Recent Sessions (status badges)      │
├────────────────────────────┴─────────────────────────────────────┤
│ [Dismissible] Download desktop app (compact)                     │
└──────────────────────────────────────────────────────────────────┘
```

Later (Phase 3): Top Domains column beside Top Apps; Attendance Today strip when aggregate lands.

---

## 6. Widget catalog (`web/src/components/dashboard/`)

| Component | Job | Phase A source | Click-through |
|---|---|---|---|
| `DashboardHeader` | Title + URL date/dept filters | URL only | — |
| `DashboardStatGrid` | Tier A KPI tiles | employees ×2, sessions, items, live-stream, usage | matching modules |
| `OnlineNowPanel` | Online / streaming list | `liveStreamApi.employees` (poll 10–15s) | `/live-stream` |
| `FleetHealthCard` | Untracked + version note | employees tracked/untracked + list sample | `/employees` |
| `ClassificationPulse` | Unclassified apps/sites | `monitoringApi` `unclassified=true` totals | `/configuration/apps` · `/websites` |
| `QuickActions` | RBAC-gated shortcuts | none | live routes only |
| `TopAppsWidget` | Top apps in range | `appSessionsApi.usage` org-wide | `/employee-journey/apps` |
| `RecentSessions` | Latest sessions | `appSessionsApi.list` org-wide | `/employee-journey/timeline?employeeId=` |
| `DownloadAppStrip` | Compact dismissible CTA | existing GitHub fetch | dialog |
| `TopDomainsWidget` | Top sites | **Phase 3** summary SQL | `/employee-journey/web` |
| `AttendanceTodayStrip` | Present/late/absent | **Phase 3** aggregate | `/attendance` |

Each widget: own `useQuery`, skeleton, empty/error, optional `module` gate. Page stays a thin composition shell.

---

## 7. Data strategy

### Phase A — compose existing APIs (no server change)

| Need | Call | Verified |
|---|---|---|
| Headcount splits | `employeesApi.list({ perPage:1, status? })` | DB 53/8/45 |
| Depts | `departmentsApi.list()` | 12 rows |
| Sessions / tabs in range | list APIs + `dateFrom`/`dateTo` | org-wide OK |
| Open sessions | `appSessionsApi.usage` → `openSessionCount` | org-wide OK |
| Top apps | `appSessionsApi.usage` + small `perPage` | org-wide OK (was wrongly assumed employee-only) |
| Online / streaming | `liveStreamApi.employees()` | presence contract |
| Unclassified | monitoring list `unclassified=true` | 278 / 197 |
| Attendance / hours org | **skip** | would N+1 |

**Do not** use `Employee.isOnline` for “Online now”.  
**Do not** show productivity % until classifications + hours org rollup exist (today: 0 classified apps).

### Phase B — `GET /api/v1/dashboard/summary` (JWT `protected`)

One payload for scale (94k `app_items` already; fan-out will hurt):

```json
{
  "employees": { "total": 53, "tracked": 8, "untracked": 45 },
  "devices": {
    "active": 11,
    "seen15m": 2,
    "seen24h": 4,
    "stale7d": 5,
    "versions": [{ "version": "1.2.34", "count": 3 }]
  },
  "activity": {
    "sessions": 186,
    "webPages": 827,
    "openSessions": 21,
    "staleSessions": 18,
    "topApps": [{ "name": "Google Chrome", "sessionCount": 79, "openNow": 4 }],
    "topDomains": [{ "domain": "github.com", "visits": 100 }]
  },
  "monitoring": { "unclassifiedApps": 278, "unclassifiedSites": 197 },
  "live": { "online": 0, "streaming": 0, "consentMissing": 0 }
}
```

- Date + optional `departmentId` query params.  
- Top-N capped in SQL (`LIMIT 5–8`).  
- Use indexes already on `app_sessions` / `app_items` (emp+started/opened).  
- `live.*` may still be filled from presence/Redis to match live-stream semantics.  
- Web keeps `liveStreamApi.employees` for the avatar list; summary owns the counts/tops.

### Phase C — later aggregates (separate tickets)

- `GET /attendance/summary?date=` — org present/late/absent.  
- `GET /hours-insights/org` — only valuable after Configuration classification work.  
- Wire `devicesApi` or fold devices into summary (prefer summary).

---

## 8. UX details

### Filters
- `preset=today|7d|30d|all|custom`, `from`, `to`, optional `departmentId`.  
- Default **Today** (local day bounds).  
- `<Suspense>` around the page body.

### Loading / empty
- Per-widget skeletons.  
- Zero tracked employees → strong CTA: download strip + `/employees`.  
- Zero online → calm empty (not an error).  
- 100% unclassified → ClassificationPulse is a warning style, not empty.

### Density
- Desktop 12-col; KPI row; Online + Fleet side-by-side; Classification + Quick Actions; Top Apps + Recent.  
- Mobile single column; Online capped + “View all”.

### Permissions
- Hide Live / Journey / Configuration / Attendance links when `canAccess` is false.

---

## 9. Implementation phases

### Phase 0 — Structure
1. Thin page shell + `components/dashboard/`.  
2. Remove Productive/Unproductive card.  
3. Download → dismissible footer (`compact`).  
4. URL date preset + skeletons.

### Phase 1 — KPIs + presence + fleet + classification
1. `DashboardStatGrid` (independent queries, click-through).  
2. `OnlineNowPanel` (`liveStreamApi`).  
3. `FleetHealthCard` + `ClassificationPulse` + `QuickActions`.  
4. Verify against live DB totals (53 / 8 / 45, 278 / 197 unclassified).  
5. `npx tsc --noEmit`, `next build`.

### Phase 2 — Activity widgets
1. `TopAppsWidget` via org `usage`.  
2. `RecentSessions` via org list + `SessionStatusBadge`.  
3. Deep-links with `employeeId`.

### Phase 3 — Server summary (recommended soon — 94k items)
1. `DashboardService` + handler + `GET /dashboard/summary`.  
2. Top domains + device version histogram + stale session counts.  
3. Web `dashboardApi.summary`; collapse fan-out.  
4. `go build` / `go vet`; smoke SQL against live DB.

### Out of scope
- Executive/shadow-IT/DLP/AI/productivity mock tiles.  
- Org attendance/hours until Phase C.  
- GPS widgets while samples=0 / UI gated.  
- Client/installer changes.

---

## 10. File touch list

**Web (0–2):**  
`web/src/app/(app)/dashboard/page.tsx`, `web/src/components/dashboard/*`, reuse `StatsCard` / `EmptyState` / `SessionStatusBadge` / `useUrlActivityFilter`.

**Web + Server (3):**  
`web/src/lib/api.ts` (`dashboardApi`),  
`server/internal/handlers/dashboard_handler.go`,  
`server/internal/services/dashboard_service.go`,  
repo aggregate queries,  
`server/internal/router/router.go` (`protected` group).

No migrations if Phase 3 only aggregates existing tables.

---

## 11. Acceptance criteria

- [ ] Skeletons, not a full-page spinner.  
- [ ] KPIs match live list totals for the same filters (spot-check vs DB: 53 / 8 / 45).  
- [ ] Untracked and unclassified are visually first-class (the real admin pain today).  
- [ ] Online panel uses live-stream presence, not `is_online`.  
- [ ] Top Apps renders real org usage for Today (Chrome etc.), no mock series.  
- [ ] Productive/Unproductive dead card gone; no executive fake charts.  
- [ ] Download strip dismissed to footer / session-dismissible.  
- [ ] URL date preset re-keys queries; page wrapped in Suspense.  
- [ ] RBAC hides inaccessible module widgets/links.  
- [ ] `npx tsc --noEmit` clean; `next build` ok.  
- [ ] (Phase 3) One summary call feeds KPIs + top apps/domains + device buckets.

---

## 12. Build order

1. **Phase 0 + 1** — structure, KPIs, online, fleet, classification (highest value vs current empty shell).  
2. **Phase 2** — Top Apps + Recent Sessions.  
3. **Phase 3** — `dashboard/summary` once fan-out or Top Domains is needed (recommended given 94k `app_items`).  
4. **Phase C** — attendance/hours org aggregates only after Product wants them (and after classification starts filling).

Say **go** to start Phase 0 + 1.
