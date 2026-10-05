# Dashboard Redesign Plan

> **Scope:** `web/src/app/(app)/dashboard/page.tsx` + new `web/src/components/dashboard/*`
> **Goal:** Turn the dashboard into a dynamic, user-friendly ops overview that scales with real data — not another mock page.
> **Constraint:** Prefer live APIs already in the product. No fake productivity scores. Scaffolding endpoints stay honest empty/coming-soon, never hardcoded charts like `/executive-dashboard`.

---

## 1. Why redesign

Today’s dashboard is a thin shell:

| Current piece | Problem |
|---|---|
| Full-page `Loader2` while 5 queries load | Feels slow; one slow call blocks everything |
| 4 count-only `StatsCard`s | No trends, no actions, no “what do I do next?” |
| Giant `DownloadAppSection` at top | Dominates the first viewport after first install |
| “Productive / Unproductive” empty card | Dead weight; no aggregate endpoint exists |
| 5 parallel `perPage: 1` list calls | Works for counts but won’t scale to richer widgets |
| No date / department filter | Breaks the URL-Synced Filters Rule used elsewhere |
| No online / live-stream signal | Presence + live-stream APIs already exist, unused here |

Admins need a **command center**: who’s online, what’s happening today, where to dig in next — not four numbers and a download banner.

---

## 2. Design principles

1. **Live data only** — every number comes from an API. If data isn’t available, show a clear empty/coming-soon state (never invent scores).
2. **Composable widgets** — each dashboard block is its own component + query. One widget failing never blanks the whole page.
3. **Skeleton over spinner** — per-widget loading skeletons; no full-page gate.
4. **URL is the filter source of truth** — date preset (+ optional department) via `useUrlQueryState` / `useUrlActivityFilter` (same rules as journey pages).
5. **Actionable** — every widget links into a real module (Employees, Journey, Live Stream, Attendance, Configuration).
6. **Scalable data path** — Phase A composes existing endpoints; Phase B adds one server aggregate so the page stays fast as employee count grows.
7. **RBAC-aware** — hide widgets whose target module the user can’t access (`usePermissions` / `canAccess`).
8. **Stay in the existing design system** — `StatsCard`, shadcn, Tailwind tokens, framer-motion delays already used on the page. Don’t invent a second visual language.

---

## 3. What to remove / demote / keep

### Remove
- The full-width **Productive / Unproductive** empty card (and its journey-link footer). Productivity belongs behind a real aggregate later, or a dedicated page — not a permanent dead zone on home.

### Demote
- **`DownloadAppSection`** — move from hero position to a collapsible / dismissible footer strip (or Settings → Tracking). First-time admins still need it; daily users don’t need it above the fold. Prefer `compact` variant + local dismiss (`sessionStorage` key is OK for UI chrome only; no mock business data).

### Keep (enhanced)
- Employee / department / activity **count tiles** — but make them richer (tracked split, click-through, optional sparkline later).
- Link-outs to Employee Journey routes (as secondary CTAs inside relevant widgets, not a standalone empty card).

---

## 4. Target information architecture

```
┌─────────────────────────────────────────────────────────────────┐
│ Header: greeting + date-range filter (URL-synced)               │
├──────────┬──────────┬──────────┬──────────┬──────────┬──────────┤
│ Employees│ Online   │ Sessions │ Web tabs │ Depts    │ Live     │
│ total    │ now      │ (range)  │ (range)  │ count    │ streaming│
├─────────────────────────────┬───────────────────────────────────┤
│ Who’s Online (presence)     │ Quick Actions                     │
│ liveStreamApi.employees     │ Employees / Live / Journey / …    │
├─────────────────────────────┴───────────────────────────────────┤
│ Recent Activity (latest app sessions, infinite or capped list)  │
├─────────────────────────────┬───────────────────────────────────┤
│ Top Apps (usage aggregate)  │ Top Sites (app-items by domain)*  │
├─────────────────────────────┴───────────────────────────────────┤
│ Fleet health: tracked vs untracked, stale client versions**     │
├─────────────────────────────────────────────────────────────────┤
│ [Dismissible] Download desktop app (compact)                    │
└─────────────────────────────────────────────────────────────────┘
```

\* Top Sites may start as “most recent browser_tab domains” from existing `appItemsApi.list` until a domain-aggregate endpoint exists.  
\*\* Client version already lands on `Employee.clientVersion` / live-stream device rows — surface “unknown / outdated” lightly, don’t build a version-policy engine in Phase A.

---

## 5. Widget catalog (components)

Put all new UI under `web/src/components/dashboard/`:

| Component | Job | Data source (Phase A) | Click-through |
|---|---|---|---|
| `DashboardHeader` | Title, short subtitle, date preset control | URL state only | — |
| `DashboardStatGrid` | 5–6 KPI tiles | employees + departments + sessions + items + live-stream | Matching module |
| `OnlineNowPanel` | Online / streaming employees (avatar list) | `liveStreamApi.employees()` | `/live-stream?ids=` / journey |
| `QuickActions` | 4–6 permission-gated shortcuts | none | fixed routes |
| `RecentSessions` | Latest sessions across org (or empty if no `employeeId` filter) | `appSessionsApi.list` | `/employee-journey/timeline?employeeId=` |
| `TopAppsWidget` | Top apps by duration in range | `appSessionsApi.usage` (needs `employeeId` today — see §6) | `/employee-journey/apps` |
| `ClassificationPulse` | Unclassified apps/sites counts | `monitoringApi` list totals / filters | `/configuration/apps` · `/websites` |
| `FleetHealthCard` | Tracked vs untracked + optional version note | `employeesApi.list` status filters | `/employees` |
| `DownloadAppStrip` | Compact dismissible installer CTA | GitHub release (existing) | dialog |

Each widget owns:
- its `useQuery` (or receives data from a thin `useDashboardData` facade),
- loading skeleton,
- error + empty states,
- optional `module` gate.

Page file stays thin: layout + filter state + composition only.

---

## 6. Data strategy

### Phase A — Web-only, existing APIs (ship first)

Use what already works:

| Need | Endpoint | Notes |
|---|---|---|
| Totals | `employeesApi.list({ perPage: 1, status? })` | Keep count pattern |
| Departments | `departmentsApi.list()` | Already unpaged |
| Sessions / web in range | `appSessionsApi.list` / `appItemsApi.list` with `dateFrom`/`dateTo` | Drive from URL preset |
| Online / streaming | `liveStreamApi.employees()` | Poll ~10–15s (`staleTime`/`refetchInterval`) like live-stream page, lighter cadence |
| Classification backlog | `monitoringApi` apps/websites with unclassified filter | Count from `total` |
| Attendance today | **Skip org-wide** in Phase A | `attendanceApi` is per-`employeeId` only — don’t N+1 |

**Gaps to handle honestly in Phase A:**

1. **`appSessionsApi.usage` is employee-scoped today** — Top Apps either (a) requires a department/employee filter, or (b) is deferred until Phase B. Prefer (b) over hammering every employee.
2. **Recent Sessions org-wide** — `GET /app-sessions` already supports listing without employee filter (confirm in handler); if not, show the widget only when an employee is selected, or defer.
3. **No productivity %** — do not revive the empty Productive/Unproductive card or copy executive-dashboard mock bars.

### Phase B — Server aggregate (scalability)

Add **one** JWT-protected endpoint, e.g.:

```
GET /api/v1/dashboard/summary?from=&to=&departmentId=
```

Returns a single payload shaped for the home page:

```json
{
  "employees": { "total": 0, "tracked": 0, "untracked": 0, "online": 0 },
  "activity": {
    "sessions": 0,
    "webPages": 0,
    "topApps": [{ "name": "", "processName": "", "durationSeconds": 0, "sessionCount": 0 }],
    "topDomains": [{ "domain": "", "visits": 0 }]
  },
  "monitoring": { "unclassifiedApps": 0, "unclassifiedSites": 0 },
  "live": { "streaming": 0, "wsConnected": 0 }
}
```

Rules:
- One round-trip; indexes already on `app_sessions` / `app_items` cover date windows.
- Top-N capped (e.g. 5) in SQL — never return raw row floods to the dashboard.
- Online counts may still come from the live-stream/presence path (or Redis) so the summary doesn’t fight the presence contract.
- Web switches widgets to `dashboardApi.summary` while keeping `liveStreamApi.employees` for the avatar list (needs names).

Phase B is the **scalability** story; Phase A must still feel useful without it.

---

## 7. UX details

### Filters (URL-Synced Filters Rule)
- Keys: `preset=today|7d|30d|all|custom`, `from`, `to`, optional `departmentId`.
- Default: **Today** (local day bounds), matching journey pages.
- No Clear-X on search; department clear = “All departments”.
- Wrap page in `<Suspense>` because of `useSearchParams`.

### Loading
- Grid of skeletons matching tile/widget geometry.
- Widgets appear independently as queries resolve (`isLoading` per card).

### Empty states
- Zero employees → CTA to `/employees` + compact download strip.
- Zero activity in range → “No activity in this period” + link to widen preset.
- Zero online → calm empty, not an error.

### Density
- Desktop: 12-col grid; KPI row full width; Online + Quick Actions side-by-side; Recent Activity full width; Top/Classification secondary row.
- Mobile: single column; Online list capped + “View all”.

### Motion
- Keep light entrance delays on KPI tiles (existing `StatsCard` pattern).
- No decorative chart animation without real data.

### Permissions
- Hide Live / Journey / Configuration widgets when `canAccess` is false.
- Dashboard module itself stays the landing page for any authenticated admin who can see it.

---

## 8. Implementation phases

### Phase 0 — Structure (half day)
1. Extract page into composition shell.
2. Create `components/dashboard/` folder + barrel.
3. Move Download strip to bottom; add dismiss.
4. Delete Productive/Unproductive empty card.
5. Add URL date preset + skeletons.

### Phase 1 — KPI + presence (1 day)
1. `DashboardStatGrid` with independent queries + click-through.
2. `OnlineNowPanel` from `liveStreamApi.employees`.
3. `QuickActions` + `FleetHealthCard`.
4. Verify: `npx tsc --noEmit`, `next build`, manual load with 0 and N employees.

### Phase 2 — Activity widgets (1–2 days)
1. `RecentSessions` (org or filtered).
2. `ClassificationPulse` from monitoring APIs.
3. Top Apps/Sites only if data path is honest; otherwise stub with “Needs aggregate endpoint” empty (no fake chart).
4. Wire deep-links with `employeeId` where relevant.

### Phase 3 — Server summary (1–2 days, optional but recommended before large fleets)
1. `DashboardService` + handler + route under JWT group.
2. SQL aggregates + top-N.
3. Web `dashboardApi.summary`; collapse Phase A fan-out.
4. `go build` / `go vet`; smoke against live DB.

### Out of scope (explicit)
- Rebuilding `/executive-dashboard` mock charts.
- Org-wide attendance heatmaps (needs new attendance aggregate).
- AI summary / productivity scoring integration until those backends exist.
- Client/installer changes (web-only feature).

---

## 9. File touch list (expected)

**Web (Phase 0–2)**
- `web/src/app/(app)/dashboard/page.tsx` — thin shell
- `web/src/components/dashboard/*` — new widgets
- `web/src/lib/api.ts` — only if Phase 3 adds `dashboardApi`
- Possibly reuse `StatsCard`, `EmptyState`, `SessionStatusBadge`, `useUrlActivityFilter`

**Server (Phase 3 only)**
- `server/internal/handlers/dashboard_handler.go` (new)
- `server/internal/services/dashboard_service.go` (new)
- `server/internal/repository/…` aggregate queries
- `server/internal/router/router.go` — `GET /dashboard/summary` on `protected`

No migrations expected if aggregates use existing tables/indexes.

---

## 10. Acceptance criteria

- [ ] First paint shows skeletons, not a centered spinner.
- [ ] KPI numbers match existing list totals for the same filters.
- [ ] Online panel reflects `liveStreamApi` presence without opening WebRTC.
- [ ] Date preset changes URL and re-keys queries.
- [ ] Download banner is not the visual hero; can be dismissed for the session.
- [ ] Productive/Unproductive dead card is gone.
- [ ] No hardcoded mock series (unlike current executive dashboard).
- [ ] Widgets respect RBAC (hidden when module inaccessible).
- [ ] `npx tsc --noEmit` clean; `next build` registers `/dashboard`.
- [ ] (Phase 3) Single summary request replaces the multi-count fan-out for KPIs/top lists.

---

## 11. Suggested default build order

1. Approve this plan (adjust widget set if you want attendance/GPS later).
2. Implement Phase 0 + 1 (structure, KPIs, online, quick actions).
3. Implement Phase 2 activity/classification.
4. Decide Phase 3 based on real employee volume / page load feel.

When you say go, start with Phase 0 + 1 unless you want Phase 3 (server summary) first.
