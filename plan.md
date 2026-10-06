# Employee Portal — Final Production Architecture & Implementation Plan

> **Status:** Plan only — **do not implement until this document is approved.**  
> **Scope:** Web `/employee-portal` (mock → live) + required server contracts. **Client/installer: N/A.**  
> **Execution:** Follow [`prompt.md`](./prompt.md) + [`AGENTS.md`](./AGENTS.md) mandatory rules.  
> **Product north star:** [`newrequirment.md`](./newrequirment.md) §§43–48 (employee dashboard + Activity Productivity terminology).  
> **Replaces:** Prior `plan.md` (admin `/dashboard` summary-first — already implemented).  
> **Audited:** 2026-10-06 against `web/src/app/(app)/employee-portal/page.tsx`, `hours-insights` + attendance handlers/repos, `auth/profile`, RBAC catalog, migrations `020`/`023`/`028`/`031`/`032`, `web/src/lib/api.ts`.

---

## 0. Execution contract (`prompt.md` alignment)

### 0.1 Task classification

| Mode | This work |
|---|---|
| **Now** | Plan / architecture lock (read-only vs product code). |
| **After approval** | **Implement/build** — complete the change, verify with project commands, hand off. Do not stop at a second proposal. |

Instruction priority:

1. Current user requirements (this plan + any follow-up).  
2. `AGENTS.md` mandatory workspace rules.  
3. `server/ARCHITECTURE.md`, `web/ARCHITECTURE.md`, `FILE_HIERARCHY.md`, `WORKFLOW.md` as relevant.  
4. `prompt.md` operating contract.  
5. Existing code patterns (handler → service → repo → DTO; `api.ts` → React Query).

### 0.2 AGENTS.md rules that bind this feature

| Rule | How it applies to Employee Portal |
|---|---|
| **Client-vs-Web API Auth Separation** | All portal reads mount under **`JWTAuth` / `protected`**. Never `DeviceAuth`. Do not read `employee_id` from Echo context (that key is DeviceAuth-only). Resolve employee from JWT `user_id` → `users.employee_id` in the **service**, or accept explicit `?employeeId=` for admin override (see §2). |
| **URL-Synced Filters** | Date preset / range in URL (`preset` / `from` / `to`). Optional admin override `employeeId` in URL. React Query keys derive from URL. Debounced local mirrors. **No Clear (X)** on date/search. `<Suspense>` around `useSearchParams`. **Never** `router.push`/`replace` inside a `setState` updater. |
| **Web Infinite-Scroll** | Portal is a **summary dashboard**, not a list page. Top apps = bounded Top-N inside summary (≤10). No Next/Previous. Deep-link to `/employee-journey/*` / `/hours-insights` for full detail. |
| **Server-Projected Flags / fields** | Cross-table labels (employee name, app type/color, attendance status) projected in the **same SQL/response** as the aggregate — never N+1 from the frontend. |
| **Installer-Parity** | **N/A** — server + web only. |
| **No hardcoded software names** | Top apps come from DB + `monitoring_types` classification — never a product-name allowlist. |
| **Cross-service contract sync** | Migration (if any) → repo → DTO → handler → `api.ts` → UI — same delivery. |
| **Docs handoff** | After landing: changelog in `AGENTS.md`, `server/ARCHITECTURE.md`, `web/ARCHITECTURE.md`. |

### 0.3 Safety and scope

- Preserve unrelated local changes.  
- Never expose or commit secrets.  
- **No commit / push / PR** unless the user explicitly asks.  
- Do **not** implement full Productivity Rules engine (`productivity_rule_sets`), Goals CRUD, or PDF export in this ticket (see §5 Out of scope).  
- Do **not** refactor `/hours-insights`, `/productivity-scoring`, or `/goals` beyond what portal needs to reuse.  
- Smallest complete solution; report blockers once (e.g. user has no linked employee).

### 0.4 Verification commands (before claiming done)

| Service | Commands |
|---|---|
| Server | `go build`, `go vet`, relevant package tests (new portal summary if added) |
| Web | `npx tsc --noEmit`, `next build` |
| Cross-service | JSON camelCase match `api.ts` ↔ Go DTOs |
| Client | **Skip** |

### 0.5 Definition of done

1. `/employee-portal` renders **live** data for a resolvable employee (no hardcoded weekly scores / fake goals / fake apps).  
2. Honest empty / unlinked-employee states (no mock fallback numbers).  
3. Checks in §0.4 pass.  
4. Contracts + docs updated.  
5. Terminology uses **Activity Productivity / Focus Score** — never “Employee Performance” (`newrequirment.md` §48).

---

## 1. Current state (evidence)

### 1.1 Web page today

`web/src/app/(app)/employee-portal/page.tsx` is **100% scaffolding**:

| Widget | Mock value | Backend today |
|---|---|---|
| My Productivity Score | `84` / “Good” | **No** score API; hours-insights has productive/unproductive/neutral seconds |
| My Focus Time | `5h 42m` | `app_sessions.foreground_seconds` exists; hours-insights only exposes **ratio** `focusScore` (fg/(fg+bg)×100), not absolute hours |
| Apps Used Today | `8` / “6 productive” | hours-insights `appCount` + type breakdown |
| My Peak Hour | `10:00 AM` | **No** peak-hour aggregate |
| Attendance | `Present` / “On time” | `GET /attendance/today?employeeId=` |
| Weekly Productivity chart | 0–100 score series + “+8%” | hours-insights `chart[]` is **seconds by type**, not score |
| My Goals | 3 progress bars | **No** table/API (`/goals` also mock) |
| Top Apps Today | names + Productive badges | hours-insights `topApps[]` with `type` |
| Weekly Summary | active/idle/avg score + Export PDF | attendance `/range` can sum active/idle; score + PDF **missing** |

Nav: sidebar General → Employee Portal (`module: "employee-portal"`). Docs already mark it mock (`web/ARCHITECTURE.md`).

### 1.2 Identity / RBAC

| Piece | Status |
|---|---|
| `users.employee_id` | EXISTS (UNIQUE) |
| `GET /auth/profile` → `employee` | EXISTS — loads linked employee when `employeeId` non-empty |
| RBAC key `employee-portal` | Seeded in `RBACService.rbacCatalog` (General) |
| RouteGuard | Client-side grant only; **no** server role middleware yet |
| Self-scoped portal API | **MISSING** — every aggregate takes explicit `?employeeId=` |
| “May only read own employee” enforcement | **MISSING** (any JWT caller can pass any `employeeId` today) |

### 1.3 Best existing APIs (reuse, do not reinvent)

| API | Why it matters |
|---|---|
| **`GET /api/v1/hours-insights`** | Employee + range; productive/unproductive/neutral seconds; `focusScore` ratio; `appCount`; `topApps` with classification; daily/hourly `chart` buckets. Classification joins `installed_applications` ↔ `monitoring_types`. |
| **`GET /api/v1/attendance/today`** | Status (`present`/`late`/`absent`/`off_shift`/`unknown`), `lateMinutes`, `timezone`, active/idle for today. |
| **`GET /api/v1/attendance/range`** | Per-day `activeSeconds` / `idleSeconds` for weekly summary totals. |
| **`GET /api/v1/auth/profile`** | Resolve “me” → linked `employee.employeeId`. |
| **`GET /api/v1/dashboard/summary`** | **Wrong shape** — org-wide, no classification badges, no attendance, no focus hours. Do **not** power the portal from it. |

### 1.4 DB surfaces (relevant)

| Table | Use |
|---|---|
| `users.employee_id` | Self-resolve |
| `app_sessions` | Duration + `foreground_seconds` / `background_seconds` (mig **020**), status lifecycle (**031**), indexes **022/032** |
| `installed_applications.type_id` + `monitoring_types` | Productive / Unproductive / Neutral (mig **023**) |
| `app_items` + `monitoring_sites` | Website classification (hours-insights already mixes sites into top items) |
| `session_events` + `shifts` + `company_holidays` | Attendance computed on read (mig **028**) — **no** server `daily_attendance` table |

**Absent:** `goals`, `productivity_rule_sets`, `productivity_scores`, PDF artifacts.

### 1.5 Product formula gap (critical)

[`newrequirment.md`](./newrequirment.md) V1 score:

```text
Activity Productivity = Productive ÷ Classified × 100
Focus Score           = Foreground ÷ (Foreground + Background) × 100   (separate metric)
Thresholds            = Excellent 80–100 / Good 60–79 / Average 40–59 / Poor 0–39
```

Today hours-insights:

- Treats **unclassified as Neutral** (`type_name IS NULL → neutral_sec`).  
- Exposes `focusScore` as fg/(fg+bg) ratio only.  
- Does **not** expose absolute `foregroundSeconds` / `backgroundSeconds` on the summary DTO (computed internally then discarded except as ratio).  
- Does **not** expose a named Activity Productivity percentage.

Full configurable Productivity Rules (`productivity_rule_sets`, browser overlap, unclassified include/exclude) is a **separate epic** — out of portal V1 scope. Portal V1 uses a **documented interim formula** locked in §3.

---

## 2. Product decisions (locked for this plan)

### 2.1 Audience: self-first portal

| Mode | Behavior |
|---|---|
| **Default** | Resolve employee from logged-in user (`auth/profile` → `employee.employeeId`). Page title/subtitle show that employee’s name. |
| **Unlinked user** | Honest empty state: “Your account is not linked to an employee record.” CTA → Profile / ask admin. **No fake KPIs.** |
| **Admin override (optional, V1.1)** | URL `?employeeId=EMP-…` allowed when caller has `employee-portal` grant — same pattern as Hours Insights picker. V1 may ship **self-only** and add picker later without API break if summary accepts optional `employeeId` with server fallback to “me”. |

**Recommendation for implementation:** ship **self + optional `?employeeId=`** in one summary endpoint so managers can deep-link without a second API. Server resolves:

1. If `employeeId` query present → use it (JWT admin).  
2. Else → load `users.employee_id` for `user_id` from JWT; 404/empty if blank.

> Hard “own-data-only” ACL for non-admins is **not** in V1 (RBAC middleware still absent). Document as known gap; do not pretend the API is private.

### 2.2 Page topology (target UI)

Align mock layout with `newrequirment.md` §45 — replace fake widgets with honest activity metrics:

```text
┌─ Header: “My Activity” + date preset (Today | 7d | custom) ─────────────┐
│ KPIs: Activity Productivity % + band | Focus Time (hours) | Focus Score % │
│       Apps used (+ productive count) | Attendance (today always)          │
├─ Weekly Activity Productivity (line: daily % for range) ──┬─ Top Apps ───┤
│  (derived score per day; vs prior period delta optional)   │ classified   │
├─ Breakdown strip: Productive / Neutral / Unproductive ────┴─ Weekly T&A ─┤
│  (seconds + %)                                              active/idle  │
└─ Deep links: Hours Insights · App Usage · Attendance ────────────────────┘
```

**Removed from V1 UI (were mock-only):**

- My Goals (no backend) → hide section; do not show empty progress theater.  
- Export PDF → hide button (no pipeline).  
- Peak Hour → hide until an hourly peak query exists (Phase 3 optional).

### 2.3 Interim Activity Productivity formula (V1)

Until Productivity Rules ship:

```text
classifiedSeconds = productiveSeconds + unproductiveSeconds + neutralSeconds
activityProductivity =
  classifiedSeconds > 0
    ? round(productiveSeconds / classifiedSeconds * 1000) / 10
    : null   // UI: “—” + “No classified activity”
```

Notes:

- Matches “Productive / Classified” with **today’s** hours-insights buckets (unclassified currently folded into Neutral).  
- Label UI **Activity Productivity**, subtitle band from thresholds in §2.4.  
- Tooltip must show Productive / Neutral / Unproductive seconds + formula (`newrequirment.md` §47).  
- Do **not** call it Employee Performance.

### 2.4 Threshold bands (display-only constants)

| Band | Range |
|---|---|
| Excellent | ≥ 80 |
| Good | ≥ 60 and &lt; 80 |
| Average | ≥ 40 and &lt; 60 |
| Poor | &lt; 40 |

Ship as shared web helper (and mirror on server response as `productivityBand` string) — **not** DB-configured until Productivity Rules.

### 2.5 Focus metrics

| Metric | Definition | Source |
|---|---|---|
| **Focus Score** | `fg / (fg+bg) * 100` (1 decimal) | Already computed in hours-insights |
| **Focus Time** | `SUM(foreground_seconds)` over range (display as hours) | Same SQL path; **must be added to API** |

---

## 3. Target architecture

### 3.1 Preferred: one summary endpoint (summary-first)

Mirror the admin dashboard lesson: **one JWT request** for the portal home, not a fan-out of list APIs.

```text
GET /api/v1/employee-portal/summary
  Auth: JWTAuth
  Query:
    employeeId?   // optional; default = linked employee for JWT user
    from? to?     // date-only or RFC3339; exclusive end for date-only (match dashboard)
    preset?       // today | 7d | custom (server may ignore if from/to set)
  Response: EmployeePortalSummaryResponse
```

**Why not only call hours-insights + attendance from the browser?**

- Three round-trips + client-side score math duplicates product rules.  
- Absolute focus seconds are not on the hours-insights DTO today.  
- Attendance “today” is independent of the selected chart range.  
- A dedicated summary keeps portal UX stable when hours-insights chart shape evolves.

**Internal composition (service layer):** reuse `NewSchemaRepo.GetHoursInsights` pieces and/or extract shared SQL helpers + `TimeAttendanceService` for today + range — **do not** duplicate classification joins. Prefer thin orchestration over copy-paste SQL.

### 3.2 Response contract (proposed)

```ts
// web/src/lib/api.ts — employeePortalApi.summary()
interface EmployeePortalSummary {
  employee: {
    employeeId: string;
    name: string;
    department: string;
  };
  range: { from: string; to: string; label: string };

  // Activity Productivity (interim formula §2.3)
  activityProductivity: number | null;      // 0–100 or null
  productivityBand: string | null;          // Excellent|Good|Average|Poor|null
  productiveSeconds: number;
  unproductiveSeconds: number;
  neutralSeconds: number;
  classifiedSeconds: number;

  // Focus
  focusSeconds: number;                     // SUM(foreground_seconds)
  backgroundSeconds: number;
  focusScore: number | null;                // fg/(fg+bg)*100

  // Apps
  appCount: number;
  productiveAppCount: number;               // distinct apps with type Productive in range
  topApps: Array<{
    name: string;
    totalSeconds: number;
    type: string;                           // Productive|Unproductive|Neutral
    color: string;
    sessionCount: number;
  }>;                                       // capped ≤ 8–10

  // Trend (one point per local day in range)
  dailyProductivity: Array<{
    date: string;                           // YYYY-MM-DD
    activityProductivity: number | null;
    productiveSeconds: number;
    unproductiveSeconds: number;
    neutralSeconds: number;
  }>;

  // Optional vs prior equal-length window (V1 nice-to-have)
  priorPeriodDelta: number | null;          // percentage points; null if either side null

  // Attendance — always "today" in employee shift TZ (independent of chart range)
  attendanceToday: {
    status: string;
    lateMinutes: number;
    timezone: string;
    firstActiveAt: string | null;
    lastActiveAt: string | null;
  } | null;

  // Weekly T&A rollup for the selected range (sum of attendance days)
  attendanceRange: {
    activeSeconds: number;
    idleSeconds: number;
    presentDays: number;
    lateDays: number;
    absentDays: number;
  };
}
```

### 3.3 Alternate (fallback if summary delayed)

Wire the page to:

1. `authApi.profile()`  
2. `hoursInsightsApi.get({ employeeId, preset/from/to })`  
3. `attendanceApi.today` + `attendanceApi.range`  

…and compute Activity Productivity + Focus Time **on the client**. Acceptable only as a short intermediate; **target remains §3.1** so formula lives server-side once.

### 3.4 Web structure

| File | Role |
|---|---|
| `web/src/app/(app)/employee-portal/page.tsx` | Suspense + URL filters + query → layout |
| `web/src/components/employee-portal/*` | KPI row, trend chart, top apps, breakdown, attendance card, empty/unlinked states |
| `web/src/lib/api.ts` | `employeePortalApi.summary` types |
| `web/src/lib/productivity.ts` (new, small) | Band labels + format helpers (shared later with Score Card) |

Reuse existing: `StatsCard`, `ActivityFilters` / `useUrlActivityFilter`, `formatSeconds`, chart tokens from Hours Insights colors (`#10b981` / `#ef4444` / `#64748b`).

### 3.5 Server structure

| Piece | Path |
|---|---|
| Handler | `server/internal/handlers/employee_portal_handler.go` |
| Service | `server/internal/services/employee_portal_service.go` |
| Repo helpers | Prefer extending `new_schema_repo` / attendance service; add `employee_portal_repo.go` only if SQL diverges cleanly |
| DTO | `server/internal/dto/employee_portal_dto.go` |
| Route | `protected.GET("/employee-portal/summary", …)` in `router.go` |
| Tests | Service-level formula + empty employee cases |

### 3.6 Indexes

Existing `idx_app_sessions_emp_started` / `idx_app_sessions_employee_started_name` + session_events indexes cover “my day / my week”. **No new migration required for V1** unless EXPLAIN on live DB shows a missing path for daily productivity buckets — then add a focused index in a numbered migration (next after latest).

Phase 0 before coding: run EXPLAIN on the daily bucket query for one active `employee_id` (same discipline as dashboard plan).

---

## 4. Implementation phases

### Phase 0 — Evidence (½ day)

1. Confirm a web user with non-empty `users.employee_id` on the target DB.  
2. EXPLAIN hours-insights-style aggregates for that employee (today + 7d).  
3. Smoke `GET /attendance/today` + `/range` for the same id.  
4. Lock formula §2.3 with product owner if Neutral-includes-unclassified is unacceptable — if so, **split unclassified** in SQL before UI work (small hours-insights/repo change in Phase 1).

**Exit:** written notes in PR/handoff; no product code required until Phase 1.

### Phase 1 — Server summary contract

1. Add `EmployeePortalSummary` DTO + handler + service.  
2. Resolve employee (query override vs JWT user link).  
3. Compose activity + focus + top apps + dailyProductivity + attendanceToday + attendanceRange.  
4. Expose `focusSeconds` / `backgroundSeconds` (fix the hours-insights gap for portal consumers).  
5. Unit-test: null productivity when classified=0; band thresholds; unlinked user error shape.  
6. `go build` / `go vet` / tests green.

### Phase 2 — Web live page

1. Replace mock `page.tsx` with URL-synced filters + `useQuery(['employee-portal-summary', …])`.  
2. KPI row + breakdown + trend + top apps + attendance + weekly T&A.  
3. Unlinked / loading / error empty states.  
4. Remove Goals + PDF + Peak Hour from UI.  
5. Deep links to Hours Insights / journey / attendance with `employeeId` preserved.  
6. `tsc --noEmit` + `next build`.

### Phase 3 — Polish (same ticket if cheap; else follow-up)

| Item | Notes |
|---|---|
| Prior-period delta | “+8% vs prior week” only if both windows have classified activity |
| Peak hour | `date_trunc('hour', …)` mode over last 30 days — **optional**; hide if not shipped |
| Self-only ACL | When server RBAC lands, restrict non-admins to linked employee |
| Split Unclassified | Align Neutral vs Unclassified with `newrequirment.md` Method 1 |

### Phase 4 — Docs handoff

- `AGENTS.md` changelog entry (web + server).  
- `server/ARCHITECTURE.md` API table row for `GET /employee-portal/summary`.  
- `web/ARCHITECTURE.md` — mark `/employee-portal` live-API (drop “Hardcoded demo data”).

---

## 5. Explicitly out of scope (do not expand)

| Item | Why |
|---|---|
| Full **Productivity Rules** engine (`productivity_rule_sets`, weights, browser overlap, rule versioning) | Separate epic (`newrequirment.md` §§49–50); portal uses interim formula |
| **Goals** CRUD / `/goals` page | No schema; leave mock or Coming Soon elsewhere |
| **PDF export** | No generation stack |
| Org-wide **Score Card** (`/productivity-scoring`) | Still mock; do not block portal on it |
| Admin **Dashboard** changes | Already shipped; leave alone |
| Client / installer / DeviceAuth routes | N/A |
| Server RBAC middleware for all routes | Pre-existing gap; portal documents the risk |

---

## 6. Mapping: mock → live

| Mock widget | V1 live behavior |
|---|---|
| My Productivity Score | **Activity Productivity** % + band from summary |
| My Focus Time | `focusSeconds` formatted |
| Apps Used Today | `appCount` + `productiveAppCount` |
| My Peak Hour | **Removed** (or Phase 3) |
| Attendance | `attendanceToday.status` + late copy |
| Weekly Productivity chart | `dailyProductivity[].activityProductivity` line (0–100 domain; gaps as null) |
| My Goals | **Removed** |
| Top Apps Today | `topApps` with type badges |
| Weekly Summary active/idle | `attendanceRange` sums |
| Avg Productivity Score | Mean of non-null daily points **or** whole-range `activityProductivity` (prefer whole-range) |
| Export PDF | **Removed** |

---

## 7. Risks & mitigations

| Risk | Mitigation |
|---|---|
| User has no `employee_id` | Empty state; never invent scores |
| Unclassified apps inflate Neutral | Document interim formula; Phase 3 split; tooltip shows raw seconds |
| Fan-out / slow page | Single summary endpoint; bounded topApps; reuse indexed session queries |
| Confusion with Focus Score vs Activity Productivity | Separate KPI tiles + copy from §48 |
| Admin viewing another employee without ACL | Optional `employeeId`; document until RBAC middleware exists |
| Duplicating hours-insights SQL bugs | Service orchestrates shared repo helpers; one classification join definition |

---

## 8. Approval checklist

Before implementation:

- [ ] Approve self-first + optional `?employeeId=` resolution (§2.1)  
- [ ] Approve interim Activity Productivity formula (§2.3) and threshold bands (§2.4)  
- [ ] Approve removal of Goals / PDF / Peak Hour from V1 UI (§2.2)  
- [ ] Approve new `GET /employee-portal/summary` over browser-only fan-out (§3.1)  
- [ ] Confirm Phase 0 EXPLAIN access to the target DB  

**After approval:** implement Phases 0→4 in one delivery pass unless a blocker is reported.

---

## 9. File touch list (expected)

**Server (new/updated):**

- `internal/dto/employee_portal_dto.go`  
- `internal/handlers/employee_portal_handler.go`  
- `internal/services/employee_portal_service.go`  
- `internal/repository/…` (helpers or thin portal repo)  
- `internal/router/router.go`  
- tests under `internal/services/`  

**Web:**

- `src/app/(app)/employee-portal/page.tsx` (rewrite)  
- `src/components/employee-portal/*` (new)  
- `src/lib/api.ts` (`employeePortalApi`)  
- `src/lib/productivity.ts` (bands/helpers)  

**Docs:**

- `AGENTS.md`, `server/ARCHITECTURE.md`, `web/ARCHITECTURE.md`  

**Not touched:** client/, installers, `dashboard/*`, Goals/PDF pipelines.
