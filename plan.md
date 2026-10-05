# Dashboard — Final Production Architecture & Implementation Plan

> **Status:** Plan only — **do not implement until this document is approved.**  
> **Scope:** Admin home `/dashboard` (web) + required server contracts. **Client/installer: N/A.**  
> **Execution:** Follow [`prompt.md`](./prompt.md) + [`AGENTS.md`](./AGENTS.md) mandatory rules.  
> **Replaces:** Prior plans that composed many list APIs first and deferred `GET /dashboard/summary`.  
> **Audited:** 2026-10-05 against live `alpha_ai_tracker` DB, `server/internal/{handlers,repository,router}`, `web/src/lib/api.ts`.

---

## 0. Execution contract (`prompt.md` alignment)

### 0.1 Task classification

| Mode | This work |
|---|---|
| **Now** | Plan / architecture lock (read-only vs product code). |
| **After approval** | **Implement/build** — complete the change, verify with project commands, hand off. Do not stop at a second proposal. |

Instruction priority (from `prompt.md`):

1. Current user requirements (this plan + any follow-up).  
2. `AGENTS.md` mandatory workspace rules.  
3. `server/ARCHITECTURE.md`, `web/ARCHITECTURE.md`, `FILE_HIERARCHY.md`, `WORKFLOW.md` as relevant.  
4. `prompt.md` operating contract.  
5. Existing code patterns (handler → service → repo → DTO; `api.ts` → React Query).

`AGENTS.md` is the source of truth for architecture and known risks. Do not invent unsupported interfaces or copy assumptions when repository evidence exists.

### 0.2 AGENTS.md rules that bind this feature

| Rule | How it applies to dashboard |
|---|---|
| **Client-vs-Web API Auth Separation** | `GET /dashboard/summary` and presence list for admins mount under **`JWTAuth` / `protected`** only. Never `DeviceAuth` / `syncGroup`. Do not read `employee_id` from Echo context in the summary handler. |
| **URL-Synced Filters** | Filters live in the URL (`preset` / `from` / `to` / `departmentId`). React Query keys derive from URL. Debounced local mirrors for inputs (~400 ms). **No Clear (X)** on date/search — clear = default preset or empty input. `<Suspense>` around `useSearchParams`. **Never** `router.push`/`replace` inside a `setState` updater. |
| **Web Infinite-Scroll** | Applies to full list/table pages. Dashboard **Recent Sessions** is a **bounded Top-N slice inside summary** (≤10–25), not a paginated list page — **no Next/Previous**, no infinite scroll widget. Deep-link to `/employee-journey/timeline` for full lists (those pages already infinite-scroll). |
| **Server-Projected Flags / fields** | `recentSessions[].employeeName` (and any cross-table boolean later) must be projected in the **same SQL** as the row — never a second frontend fetch per session. Same spirit as `Employee.hasUserLogin`. |
| **Live stream / presence** | Online semantics reuse existing `employeeLiveOnline` (presence WS authoritative when enabled; else heartbeat window). Do not invent a parallel online model. Extend `GET /live-stream/employees` params; keep JWT admin route. |
| **Installer-Parity** | **N/A** — server + web only. No `config.enc`, no client publish. |
| **No hardcoded software names** | Top Apps/Domains are data-driven from DB aggregates — never a product-name allowlist. |
| **Branding single source** | N/A unless UI copy hardcodes product name; reuse existing `APP_SHORT_NAME` / config helpers if needed. |
| **Cross-service contract sync** | Migration (if any) → repo SQL → DTO/model → handler → `web/src/lib/api.ts` types → UI — **same PR / same delivery**. |
| **Docs handoff** | After landing: changelog entries in `AGENTS.md`, `server/ARCHITECTURE.md`, `web/ARCHITECTURE.md` (per `prompt.md` live-stream/server/web contract note). |

### 0.3 Safety and scope (`prompt.md`)

- Preserve unrelated local changes.  
- Never expose or commit secrets (`.env`, tokens).  
- **No commit / push / PR / amend / branch** unless the user explicitly asks.  
- No adjacent refactors (do not “fix” executive-dashboard, attendance N+1 page, etc. in this ticket).  
- Do not disable analyzers, validation, hooks, or tests to hide failures.  
- Smallest complete solution consistent with existing Echo layering and Next patterns.  
- When blocked (DB auth, live-stream flag off, missing index evidence), report the exact blocker once.

### 0.4 Verification commands (must run before claiming done)

| Service | Commands |
|---|---|
| Server | `go build` (or project `make` equivalent), `go vet`, relevant package tests |
| Web | `npx tsc --noEmit`, `next build` (and lint if already used in repo workflow) |
| Cross-service | Confirm JSON field names match `api.ts` ↔ Go DTOs (camelCase) |
| Client | **Skip** (unchanged) |

Do not claim a check passed unless it was run successfully (`prompt.md` Verification).

### 0.5 Definition of done (`prompt.md` + this feature)

A delivery is done only when:

1. Requested dashboard behavior is implemented (summary-first topology).  
2. Relevant checks above pass (or failures clearly reported).  
3. Cross-service contracts synchronized; Installer-Parity N/A stated.  
4. No unrelated user work overwritten.  
5. Docs changelogs updated (`AGENTS.md`, `server/ARCHITECTURE.md`, `web/ARCHITECTURE.md`).  
6. Final response states result, verification evidence, and any required user action.

---

## Changelog vs previous plan

| Decision | Previous plan | This plan |
|---|---|---|
| Data path | Phase A: 6–10 frontend list calls; Phase B: summary later | **Summary-first** — implement `GET /dashboard/summary` before UI widgets |
| Top Apps | Reuse `appSessionsApi.usage` org-wide | **Do not** — usage runs heavy island/window SQL; summary uses bounded Top-N |
| Top Domains | Deferred | In **summary from day one** (no list API aggregate) |
| Device health | Employee-list sampling | **`summary.devices`** SQL (org devices API not in `api.ts`) |
| Online Now | Full `liveStreamApi.employees()` poll | Live **counts** in summary; **capped** presence list |
| Live vs historical | Mixed | **Strict split** |
| Temporary architecture | Planned then replaced | **Forbidden** |
| Execution | Ad-hoc | Bound to **`prompt.md` + AGENTS.md`** (auth group, URL filters, docs, verify) |

**Kept:** no fake productivity/executive/DLP/AI/GPS; presence ≠ `employees.is_online`; classification backlog; skeletons; URL filters; RBAC-aware widgets; Quick Actions; dismissible download strip; “who needs attention.”

---

## 1. Product goal

The dashboard answers four questions in one screen:

| Question | Examples |
|---|---|
| **A. Who needs attention?** | Untracked employees · unclassified apps/sites · stale devices · stale sessions |
| **B. Who is online now?** | Presence-backed online / streaming (not DB `is_online`) |
| **C. What is happening?** | Sessions · web tabs · open sessions · Top Apps · Top Domains · Recent Sessions |
| **D. What should I do?** | Quick Actions → **live** modules only |

Target scale: hundreds → **thousands** of employees; large `app_sessions`; **millions** of `app_items`; concurrent admins; fast first paint; **minimal HTTP round-trips** for historical KPIs.

---

## 2. Hard rules (non-negotiable)

1. **Frontend-consumable contracts only.** Postgres alone is never enough. If `api.ts` + a JWT route cannot deliver filters/aggregates/bounded payload, classify **`NOT AVAILABLE FOR DASHBOARD`** until an endpoint is in this plan.  
2. **No frontend N+1.**  
3. **No temporary fan-out architecture.**  
4. **No fake metrics.**  
5. **Live ≠ historical.**  
6. **Bounded payloads** (Top-N / recent LIMIT; SQL `COUNT`/`FILTER`).  
7. **Server-side aggregation** only.  
8. **JWT `protected` only** for new admin dashboard APIs (`AGENTS.md` Client-vs-Web rule).  
9. **Contracts + docs land with code** (`prompt.md` Definition of done).

---

## 3. Dashboard data contract audit

Legend: **Frontend usable?** = in `web/src/lib/api.ts` with needed fields. **Scalable for home?** = safe as *primary* dashboard path at 1k+ employees / large activity tables.

| Dashboard data | Backend source | Existing API | Frontend usable? | Scalable for home? | Final solution |
|---|---|---|---|---|---|
| Employee total | `employees` | `GET /employees` → `employeesApi.list` | **Yes** | **No** (extra trips) | **`dashboard/summary.employees.total`** |
| Tracked / untracked | `tracking_status` | `list({ status })` | **Yes** | **No** | **`summary.employees.tracked/untracked`** |
| Online count | presence + heartbeat | `GET /live-stream/employees` | **Yes** if enabled | **No** (full org every poll; 503 if off) | **`summary.live.online`** |
| Online names | same | same full list | **Yes** | **No** at thousands | **`?onlineOnly&limit`** on same JWT route (§5.2) |
| Streaming / consent missing | SFU + terms | same | **Yes** | Counts in summary | **`summary.live.*`** |
| `employees.is_online` | DB login flag | on `Employee` | Yes | **Wrong signal** | **Do not use** |
| Open sessions | ACTIVE rows | `usage.openSessionCount` | **Yes** | **No** (heavy usage SQL) | **`summary.activity.openSessions`** |
| Stale sessions | `STALE` | No status filter | **No** | n/a | **`summary.activity.staleSessions`** |
| Sessions in range | `started_at` | `appSessionsApi.list` → `total` | **Yes** | **Weak**; no `departmentId` | **`summary.activity.sessions`** |
| Web tabs in range | `browser_tab` | `appItemsApi.list` → `total` | **Yes** | **Weak** at millions | **`summary.activity.webPages`** + index |
| Top Apps | GROUP BY sessions | `appSessionsApi.usage` | **Yes** | **No for home** | **`summary.topApps[]`** light Top-N |
| Top Domains | `app_items.domain` | **None** | **No** | n/a | **`summary.topDomains[]`** (new) |
| Unclassified apps/sites | monitoring tables | `monitoringApi.*.list({ unclassified })` | **Yes** | OK alone, bad in fan-out | **`summary.monitoring.*`** |
| Device health / versions | `employee_devices` | Per-emp `GET /employees/:id/devices` only; **no `devicesApi`** | **No (org)** | n/a | **`summary.devices`** (new) |
| Recent sessions | `app_sessions` | `list({ perPage:10 })` | **Yes** | Prefer embed | **`summary.recentSessions[]`** + projected `employeeName` |
| Department options | `departments` | `departmentsApi.list()` | **Yes** | **Yes** | Separate cached call |
| Org attendance | schedules + events | `attendanceApi` requires `employeeId` | **No (org)** | N+1 | **`NOT AVAILABLE FOR DASHBOARD`** |
| Org hours / productivity % | hours-insights | requires `employeeId` | **No (org)** | N+1 | **`NOT AVAILABLE FOR DASHBOARD`** |
| GPS / DLP / Shadow IT / AI / Exec | — | gated / scaffolding | No | — | **Out** |

### Audit conclusions

1. List APIs are not the production home backend.  
2. Top Domains + org Device Health are **NOT AVAILABLE** without summary SQL.  
3. Full live-stream employee list is not a scale-safe Online Now feed.  
4. **Final topology is mandatory from Phase 1** (summary + capped presence + departments).

---

## 4. Final request topology (production)

```
Browser /dashboard
 │
 ├─① GET /api/v1/dashboard/summary?from&to&departmentId     [JWT protected]
 │     • employees, activity, monitoring, devices, live counts
 │     • topApps, topDomains, recentSessions (bounded)
 │     • React Query key = URL filters; staleTime 30–60s
 │     • refetch on filter change — NOT on presence timer
 │
 ├─② GET /api/v1/live-stream/employees?onlineOnly=true&limit=24  [JWT protected]
 │     • Online Now names only; refetchInterval 10–15s (tab visible)
 │     • 503 if live-stream disabled → panel empty/disabled
 │
 └─③ GET /api/v1/departments   [JWT protected]
       • filter dropdown; staleTime 5–10 min
```

**Target ≤ 3 HTTP calls.** Forbidden: multi-count fan-out + org-wide `usage` + full live list as the home data plane.

---

## 5. Final API contracts

### 5.1 `GET /api/v1/dashboard/summary` (new)

**Auth:** `JWTAuth` / `protected` group in `router.go` — **web admin only** (`AGENTS.md` Client-vs-Web).  
**Consumer:** `web` via Next rewrite + `dashboardApi` in `api.ts`.  
**Not for:** desktop client.

**Query**

| Param | Required | Notes |
|---|---|---|
| `from` | yes (client-normalized) | Inclusive start; date-only or RFC3339 — match journey `parseTimeParam` behavior |
| `to` | yes | **Exclusive end** (document once; align with journey pages) |
| `departmentId` | no | Scopes employee KPIs + activity/top/recent via `employees.department_id` |
| `topN` | no | Default **8**, max **20** (server clamp) |
| `recentLimit` | no | Default **10**, max **25** |

**Response (canonical camelCase — lock Go DTO tags + `api.ts` together)**

```json
{
  "range": { "from": "…", "to": "…" },
  "employees": { "total": 0, "tracked": 0, "untracked": 0 },
  "activity": {
    "sessions": 0,
    "webPages": 0,
    "openSessions": 0,
    "staleSessions": 0
  },
  "monitoring": { "unclassifiedApps": 0, "unclassifiedSites": 0 },
  "devices": {
    "active": 0,
    "seen15m": 0,
    "seen24h": 0,
    "stale7d": 0,
    "versions": [{ "version": "1.2.34", "count": 3 }]
  },
  "live": {
    "online": 0,
    "streaming": 0,
    "consentMissing": 0,
    "presenceAvailable": true
  },
  "topApps": [
    {
      "appDisplayName": "Google Chrome",
      "processName": "chrome",
      "sessionCount": 79,
      "openNow": 4
    }
  ],
  "topDomains": [{ "domain": "github.com", "visits": 100 }],
  "recentSessions": [
    {
      "id": "…",
      "employeeId": "MU-17",
      "employeeName": "…",
      "appDisplayName": "…",
      "processName": "…",
      "status": "ACTIVE",
      "startedAt": "…",
      "endedAt": null,
      "lastSyncAt": "…"
    }
  ]
}
```

**Rules**

- Top Apps = light `COUNT` Top-N (+ `openNow`); **not** `AggregateAppSessionsUsage` islands.  
- Top Domains = `browser_tab` + non-empty `domain`, range, `GROUP BY`, `LIMIT`.  
- `openSessions` / `staleSessions` = **now** (no date clip); label UI accordingly.  
- `live.*` counts reuse `employeeLiveOnline` / hub / consent — integers only.  
- `employeeName` on recent = SQL join projection (Server-Projected Fields).  
- Errors: `dto.APIError` 400/401/500; prefer fail whole summary over fake section zeros.  
- Server role middleware still product-wide absent — frontend `canAccess` gates widgets; do not invent dashboard-only ACL.

**Wiring (existing patterns — do not invent new stacks)**

- `cmd/server/main.go`: construct handler with needed repos/presence/hub deps.  
- `router.go`: `protected.GET("/dashboard/summary", dashboardHandler.GetSummary)`.  
- Layering: `handlers` → `services` → `repository` + `dto`.

**Web**

```ts
export const dashboardApi = {
  summary: (params: {
    from: string; to: string; departmentId?: number; topN?: number; recentLimit?: number;
  }) => request<DashboardSummaryResponse>('/dashboard/summary', { params }),
};
```

### 5.2 Presence list (real-time — separate JWT route)

Extend existing admin route (same handler, additive query params — backward compatible when omitted):

```http
GET /api/v1/live-stream/employees?onlineOnly=true&limit=24
```

| Param | Behavior |
|---|---|
| `onlineOnly=true` | Only `online === true` (existing online rule) |
| `limit` | Default 24, max 50 |
| `departmentId` | Optional if cheap |

Response: `{ data: LiveStreamEmployee[]; total: number }` where `total` = **full online count** (not page length).  
Omitted params → today’s full-list behavior (live-stream console unchanged).  
Feature off → 503 (existing); panel disabled; summary `live.presenceAvailable=false`.

**Web:** `liveStreamApi.employees(params?)` — extend, don’t fork.

### 5.3 Departments (unchanged)

`departmentsApi.list()` for filter options.

### 5.4 Not in v1

| Metric | Status |
|---|---|
| Org attendance | NOT AVAILABLE — future `GET /attendance/summary` if product asks |
| Org hours / productive % | NOT AVAILABLE |
| Executive / DLP / Shadow IT / AI / GPS tiles | Out |

---

## 6. SQL & index strategy

One handler → service → few SQL statements (errgroup for independent blocks). Aggregate in Postgres; never pull millions of rows into Go for grouping.

### 6.1 Query plan

| Block | Tables | Strategy | Result size |
|---|---|---|---|
| Employees | `employees` | `COUNT(*) FILTER` + optional `department_id` | 1 row |
| Sessions in range | `app_sessions` (+ emp join if dept) | `COUNT` on `started_at` window | scalar |
| Open / stale | `app_sessions` | `COUNT FILTER` by status — **no date** | 1 row |
| Web pages | `app_items` | `item_type='browser_tab'` + `opened_at` | scalar |
| Top apps | `app_sessions` | `GROUP BY` name/process `ORDER BY COUNT DESC LIMIT` | ≤20 |
| Top domains | `app_items` | `GROUP BY domain LIMIT` | ≤20 |
| Recent | `app_sessions` ⋈ `employees` | `ORDER BY started_at DESC LIMIT` + name | ≤25 |
| Monitoring | catalog + sites | unclassified `COUNT` (same predicates as monitoring repo) | 2 scalars |
| Devices | `employee_devices` | last_seen buckets + version histogram `LIMIT 10` | small |
| Live counts | presence / heartbeat / consent | in-process counts (reuse stream code paths) | 3 ints |

### 6.2 Indexes

| Need | Existing | Action |
|---|---|---|
| Org sessions by time | `idx_app_sessions_timestamp` etc. | **EXPLAIN** org range; add `(started_at DESC) WHERE deleted_at IS NULL` if seq-scan |
| ACTIVE/STALE | partial active + status_sync | Add STALE partial only if EXPLAIN requires |
| Org web tabs | `idx_app_items_emp_opened` weak org-wide | Prefer **`idx_app_items_type_opened`** `(item_type, opened_at DESC) WHERE deleted_at IS NULL` |
| Top domains | `idx_app_items_domain` | Prefer type+time index first; widen only if measured |
| Devices last_seen | emp_id index | Consider `(last_seen_at) WHERE revoked_at IS NULL` |
| Employees dept | `idx_employees_department_id` | OK |

**Migration:** only proven indexes → next sequential file (e.g. `042_dashboard_indexes.sql`). No speculative wide indexes.

### 6.3 Caching

| Data | Strategy |
|---|---|
| Summary | v1: indexed SQL only; optional Redis TTL 30–60s later under admin concurrency |
| Presence | no DB cache |
| Departments | client `staleTime` 5–10 min |
| Summary client | React Query `staleTime` 30–60s; **not** tied to 15s presence poll |

---

## 7. Frontend architecture

### 7.1 Filters (URL-Synced Filters Rule — full)

```
?preset=today|7d|30d|all|custom&from=YYYY-MM-DD&to=YYYY-MM-DD&departmentId=
```

- Default **Today** (local day → ISO bounds for API).  
- Single filter model → all summary widgets.  
- Presence ignores date range; may honor `departmentId`.  
- Debounced local input mirrors; no Clear-X.  
- `<Suspense>` boundary.  
- Router updates **outside** `setState` updaters (`useUrlQueryState` pattern already fixed for live-stream).

### 7.2 Components (`web/src/components/dashboard/`)

| Component | Data | States |
|---|---|---|
| `DashboardHeader` | URL filters | — |
| `DashboardStatGrid` | summary KPIs | skeleton / error |
| `AttentionStrip` | untracked / unclassified / stale | summary |
| `OnlineNowPanel` | presence list | skeleton / empty / error / disabled |
| `FleetHealthCard` | `summary.devices` | skeleton |
| `ClassificationPulse` | `summary.monitoring` | skeleton |
| `QuickActions` | `usePermissions` | — |
| `TopAppsWidget` / `TopDomainsWidget` / `RecentSessions` | summary arrays | empty OK |
| `DownloadAppStrip` | existing GitHub helper; session dismiss | footer |

Page = composition only. Ownership: place under `web/src/components/dashboard/` per `FILE_HIERARCHY.md` / web ownership.

### 7.3 Loading / error

- No full-page `<Loader2 />`.  
- Skeletons until summary resolves (consistent KPI/Top/Recent).  
- Presence updates independently.  
- Summary error ≠ blank Online Now.

### 7.4 Refresh

| Query | staleTime | refetchInterval |
|---|---|---|
| summary | 30–60s | off (filter / retry) |
| presence online list | 0–5s | 10–15s, tab visible only |
| departments | 5–10 min | off |

### 7.5 RBAC

Frontend `canAccess` hides Live / Journey / Configuration / Attendance actions. Dashboard module remains landing.

### 7.6 Remove / demote

- Delete Productive/Unproductive empty card.  
- Download → dismissible footer (`compact`).  
- No executive/shadow-IT/DLP/AI mock numbers.

---

## 8. Implementation phases (final architecture; `prompt.md` implement workflow)

Phases = delivery safety. **No throwaway fan-out.** Each phase leaves shippable contracts.

### Phase 0 — Contract lock + EXPLAIN (no product UI)

1. [x] API audit (§3).  
2. [x] Response contract (§5).  
3. [ ] `EXPLAIN (ANALYZE, BUFFERS)` candidate SQL on live DB → final index list.  
4. [ ] Confirm live-stream param extension stays backward compatible for `/live-stream` console.

**Exit:** Approved plan + index list.  
**prompt.md step:** establish current behavior (today’s dashboard fan-out) before changing it.

### Phase 1 — Backend (complete server slice)

Per `prompt.md` implement workflow: inspect → smallest complete server solution → verify.

1. Index migration if EXPLAIN requires.  
2. `dto` + `repository` + `service` + `handler` + `main.go` DI + `router` JWT route.  
3. Extend `StreamHandler.ListEmployees` with `onlineOnly` / `limit` / accurate `total`.  
4. Tests: empty org, dept filter, range validation, Top-N clamp, live-stream disabled.  
5. `go build`, `go vet`, relevant tests.  
6. Manual curl against running API with admin cookie.

**Exit:** Final summary + presence contracts callable. Frontend may still be old.

### Phase 2 — Frontend shell (summary + presence)

1. `dashboardApi` + types; extend `liveStreamApi.employees`.  
2. Rewrite `dashboard/page.tsx` + URL filters + Suspense.  
3. StatGrid, Attention, OnlineNow, Fleet, Classification, QuickActions, Download strip.  
4. Remove dead productivity card.  
5. `npx tsc --noEmit`, `next build`.

**Exit:** Home uses production topology (§4). No KPI list fan-out.

### Phase 3 — Activity widgets (same summary payload)

1. TopApps, TopDomains, RecentSessions from summary.  
2. Deep-links to journey routes (`employeeId` where useful).  
3. Empty states.

### Phase 4 — Performance validation

| Scenario | Scale | Checks |
|---|---|---|
| Small | ~100 emp | p95 summary budget (e.g. &lt;200ms DB on indexed path) |
| Medium | ~500 | payload typically &lt;50–100KB |
| Large | 1k–5k + large `app_items` | no hot seq scans; presence ≤ `limit` rows |

Record: HTTP count on load (**≤3**), DB queries/summary, payload, concurrency.

### Phase C (optional later — not v1)

Org attendance / hours aggregates — only if product prioritizes; separate ticket.

### Docs handoff (with Phase 1–2 land)

Update changelogs (date + short bullet):

- `AGENTS.md` — dashboard summary + presence list params; web home topology.  
- `server/ARCHITECTURE.md` — API surface `GET /dashboard/summary`; indexes migration.  
- `web/ARCHITECTURE.md` — `/dashboard` data source = summary + presence; retire “employees+sessions+items fan-out” row in route table.

---

## 9. File touch list

**Server**  
- `server/migrations/042_dashboard_indexes.sql` (conditional)  
- `server/internal/dto/dashboard_dto.go`  
- `server/internal/repository/dashboard_repo.go`  
- `server/internal/services/dashboard_service.go`  
- `server/internal/handlers/dashboard_handler.go`  
- `server/internal/handlers/stream_handler.go`  
- `server/internal/router/router.go`  
- `server/cmd/server/main.go` (DI)  
- tests  

**Web**  
- `web/src/lib/api.ts`  
- `web/src/app/(app)/dashboard/page.tsx`  
- `web/src/components/dashboard/*`  
- reuse `StatsCard`, `EmptyState`, `SessionStatusBadge`, `useUrlQueryState` / activity filter helpers  

**Docs**  
- `AGENTS.md`, `server/ARCHITECTURE.md`, `web/ARCHITECTURE.md`  

**Out of scope:** `client/**`, installer, executive-dashboard, attendance page N+1 rewrite, GPS UI flag flip.

---

## 10. Acceptance criteria

### Correctness
- [ ] Every on-screen number from a real, frontend-wired JWT API.  
- [ ] No “DB has it” without contract.  
- [ ] Online ≠ `Employee.isOnline`.  
- [ ] One summary → consistent KPI/Top/Recent for a filter set.  
- [ ] No mock productivity / executive / DLP / AI / GPS tiles.  
- [ ] `employeeName` (and similar) server-projected.

### Scalability
- [ ] No frontend N+1.  
- [ ] Home historical path = summary only.  
- [ ] Bounded Top-N / recent.  
- [ ] Presence poll capped.  
- [ ] Dept filter in SQL.

### Speed
- [ ] No full-page spinner.  
- [ ] Summary not on presence interval.  
- [ ] EXPLAIN-validated indexes where added.  
- [ ] Phase 4 numbers recorded.

### `prompt.md` / AGENTS compliance
- [ ] Summary + presence under **JWT `protected`**, not DeviceAuth.  
- [ ] URL-synced filters; Suspense; no router-inside-setState.  
- [ ] Recent = capped summary slice (infinite-scroll rule N/A to this widget).  
- [ ] Cross-service types synced.  
- [ ] Docs changelogs updated.  
- [ ] `go build` / `go vet` / `tsc` / `next build` run and reported honestly.  
- [ ] No secrets committed; no unsolicited git commit/push/PR.  
- [ ] Installer-Parity N/A documented in handoff.

---

## 11. Live DB context (evidence only — not a UI data source)

Snapshot 2026-10-05 — motivates widgets; **UI still uses §5 APIs only**:

- 53 employees / 8 tracked / 45 untracked  
- 186 sessions · 827 browser tabs today  
- 278 apps + 197 sites unclassified  
- 11 devices; version mix; 2 seen in 15m  
- 21 ACTIVE · 18 STALE  
- location/holidays empty  

---

## 12. Build order

1. Approve this plan.  
2. Phase 0 — EXPLAIN + index list.  
3. Phase 1 — backend summary + presence params + tests + verify.  
4. Phase 2 — frontend shell on summary + Online Now + verify.  
5. Phase 3 — Top Apps / Domains / Recent.  
6. Phase 4 — scale validation.  
7. Docs handoff + final report (result, evidence, user actions).

**Do not implement code until approved. Do not ship list-API fan-out as an interim home.**
