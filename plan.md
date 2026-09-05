# Hours Insights — Build Plan

## Goal
Build `/hours-insights` — a page where an admin selects an employee and sees **total usage of apps and websites** broken down by time period (Today, Yesterday, Last 7 days, Last 30 days, Custom range) rendered as a **graph/chart view**.

---

## 1. Current State

### Database (PostgreSQL)
| Table | Key columns | Notes |
|-------|-------------|-------|
| `employees` | `id` (uuid), `employee_id` (varchar), `name`, `department_id`, `deleted_at` | Source of truth for the user picker |
| `departments` | `id`, `name` | For department label on the picker |
| `app_sessions` | `employee_id`, `app_display_name`, `process_name`, `started_at`, `ended_at`, `foreground_seconds`, `background_seconds`, `status`, `last_sync_at`, `deleted_at` | App-level usage windows |
| `app_items` | `employee_id`, `app_session_id`, `item_type` (`tab`, `browser_tab`, `browser_navigation`, …), `url`, `domain`, `opened_at`, `closed_at`, `title`, `deleted_at` | Web page / tab-level visits |
| `installed_applications` | `id`, `employee_id`, `app_name`, `binary_name`, `is_browser`, `type_id`, `category_id`, `deleted_at` | App metadata (for classification) |
| `monitoring_types` | `id`, `name`, `color` | Productive / Unproductive / Neutral colors |
| `monitoring_sites` | `id`, `domain`, `type_id`, `category_id` | Website registry |

### Existing web patterns
- **Charts**: `recharts` (v2.15.4) is already in `package.json`. Custom `ChartContainer`, `ChartTooltipContent`, `ChartLegendContent` live in `web/src/components/ui/chart.tsx`.
- **Employee selection**: `EmployeePage` shell (`web/src/components/employees/EmployeePage.tsx`) + `EmployeeSelector` picker deep-linkable via `?employeeId=`. Used by all Employee Journey pages.
- **Filters**: `ActivityFilters` + `useUrlActivityFilter` hook — presets encoded as `today`, `7d`, `30d`, `custom` + `from`/`to` dates in the URL query string.
- **API client**: `web/src/lib/api.ts` — `request<T>()` wrapper with cookie auth + 401 refresh. New endpoints added here.
- **Server routes**: All under `/api/v1`, protected by `JWTAuth` middleware.
- **Sidebar entry**: Already exists at `/hours-insights`, module key `hours-insights`.

---

## 2. What the Page Should Show

### Layout (top → bottom)

```
┌─────────────────────────────────────────────────────────────────┐
│ Hours Insights                                    [icon/help]   │
│ Track how employees spend time across apps and websites         │
├─────────────────────────────────────────────────────────────────┤
│ Employee: [Amir ▾]          Department: Engineering             │
├─────────────────────────────────────────────────────────────────┤
│ Filters: [Today ▾]  [Custom range ▾]                           │
│ (ActivityFilters component — same as journey pages)              │
├─────────────────────────────────────────────────────────────────┤
│ ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐          │
│ │ Total    │ │ Apps     │ │ Websites │ │ Focus    │          │
│ │ Time     │ │ Used     │ │ Visited  │ │ Score % │          │
│ │ 4h 22m   │ │ 12       │ │ 28       │ │ 68%     │          │
│ └──────────┘ └──────────┘ └──────────┘ └──────────┘          │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│   ████████████████████████████████████                          │
│   ██████████  ████████████████  ████████                        │
│   ███████  ████████████  ████████  ██████                       │
│   █████  ██████████  ████████████  ██████                       │
│   ███  █████████  ██████████████  ███████                       │
│   ██  ████████  ████████████████  ████████                      │
│   ██  ███████  █████████████████  █████████                     │
│   █  ████████  ██████████████████  ██████████                   │
│     ████████  ██████████████████  ███████████                   │
│      ███████  ██████████████████  ████████████                  │
│       ██████  █████████████████  ████████████                   │
│        █████  █████████████████  ███████████                    │
│         ████  ████████████████  ██████████                      │
│          ███  ███████████████  █████████                        │
│           ██  ██████████████  ████████                          │
│             █  █████████████  ███████                           │
│              █  ███████████  ██████                             │
│                ██████████  █████                                │
│                 █████████  ████                                 │
│                  ████████  ███                                  │
│                   ███████  ██                                   │
│                    ██████                                       │
│                     ████                                        │
│                                                                 │
│   ┌── Stacked bar chart: productive / unproductive / neutral  ┐│
│   │  X-axis = time buckets (hourly or daily)                  ││
│   │  Stack = foreground seconds (productive) + background     ││
│   │   Tooltip shows app/site name + seconds                   ││
│   └───────────────────────────────────────────────────────────┘│
├─────────────────────────────────────────────────────────────────┤
│ Top Apps & Sites                                               │
│ ┌────────────────────┬──────┬────────┬───────────┬──────────┐ │
│ │ App / Site         │ Type │ Cat.   │ Duration  │ Focus %  │ │
│ ├────────────────────┼──────┼────────┼───────────┼──────────┤ │
│ │ Google Chrome      │ App  │ Web    │ 1h 45m    │ 72%      │ │
│ │ Visual Studio Code │ App  │ Dev    │ 1h 12m    │ 89%      │ │
│ │ chatgpt.com        │ Site │ AI     │ 45m       │ 91%      │ │
│ │ ssavr.com          │ Site │ Utils  │ 12m       │ 60%      │ │
│ └────────────────────┴──────┴────────┴───────────┴──────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

### Key UI decisions

| Element | Decision |
|---------|----------|
| **Chart type** | Stacked area (Recharts `<AreaChart>`, stacked) or stacked bar per time bucket. Stacked area shows "how time was spent over the period". |
| **Time bucket** | For Today/Yesterday: hourly buckets (00:00–23:00). For 7d/30d: daily buckets. |
| **Stack layers** | Three categories: **Productive** (green), **Unproductive** (red), **Neutral** (gray). Derived from `installed_applications.type_id` / `monitoring_sites.type_id` joined to `monitoring_types`. |
| **Focus Score** | `SUM(foreground_seconds) / NULLIF(SUM(foreground_seconds + background_seconds), 0)` expressed as a percentage. |
| **Top list** | Sorted by total duration desc. Shows app/site name, type (App vs Site), category, formatted duration, and focus %. |
| **Empty state** | Honest `EmptyState` if no data for the selected employee + range. |

---

## 3. Server Changes

### New endpoint: `GET /api/v1/hours-insights`

**Query params:**
- `employeeId` (required)
- `dateFrom`, `dateTo` (optional, RFC3339 or date-only)
- `preset` (optional: `today`, `yesterday`, `7d`, `30d`, `custom`) — convenience, same pattern as `GET /app-sessions`

**Response shape:**
```json
{
  "employee": { "employeeId": "EMP-10002", "name": "Amir", "department": "Engineering" },
  "range": { "from": "2026-09-05T00:00:00+05:00", "to": "2026-09-05T11:00:00+05:00", "label": "Today" },
  "summary": {
    "totalSeconds": 15680,
    "productiveSeconds": 10240,
    "unproductiveSeconds": 3200,
    "neutralSeconds": 2240,
    "focusScore": 65.2,
    "appCount": 12,
    "siteCount": 28
  },
  "chart": [
    { "bucket": "09:00", "productive": 2400, "unproductive": 600, "neutral": 300 },
    { "bucket": "10:00", "productive": 1800, "unproductive": 1200, "neutral": 200 }
  ],
  "topItems": [
    {
      "name": "Google Chrome",
      "kind": "app",
      "category": "Web Browsing",
      "type": "Productive",
      "color": "#10b981",
      "totalSeconds": 6300,
      "focusScore": 72.1,
      "isBrowser": true
    }
  ]
}
```

### Implementation notes
1. **Chart aggregation is done in SQL** — one query, not one query per bucket. Use `generate_series` for the time axis and `LEFT JOIN` against filtered `app_sessions` + `app_items`.
2. **Apps vs Sites**: `app_sessions` rows give us app usage. For websites, sum `app_items` where `item_type IN ('tab', 'browser_tab', 'browser_navigation')` and `domain IS NOT NULL`.
3. **Category resolution**:
   - Apps: `installed_applications.type_id → monitoring_types.name + color`
   - Sites: `monitoring_sites.type_id → monitoring_types.name + color`
   - Fallback: rows with no type → `neutral` / gray
4. **Focus score per top item**: `SUM(foreground_seconds) / NULLIF(SUM(foreground_seconds + background_seconds), 0)` for the app/site's sessions in range.
5. **Reuse existing patterns**: Same `JWTAuth` guard, same `dto.APIError` error envelope, same `ActivityFilter` preset/date resolution used by `GET /app-sessions` and `GET /app-items`.

### Files to create / modify (server)
- `server/internal/handler/hours_insights_handler.go` — new handler
- `server/internal/repository/new_schema_repo.go` — new `GetHoursInsights(ctx, employeeID, from, to)` method
- `server/internal/router/router.go` — register `GET /api/v1/hours-insights`

---

## 4. Client / Web Changes

### New API method
Add to `web/src/lib/api.ts`:
```ts
export const hoursInsightsApi = {
  get: (params: {
    employeeId: string;
    dateFrom?: string;
    dateTo?: string;
    preset?: string;
  }) =>
    request<HoursInsightsResponse>('/hours-insights', {
      params: params as Record<string, string | number | undefined>,
    }),
};
```

Add types:
```ts
export interface HoursInsightsResponse { ... }
export interface HoursInsightsChartBucket { bucket: string; productive: number; unproductive: number; neutral: number; }
export interface HoursInsightsTopItem { name: string; kind: 'app' | 'site'; category: string; type: string; color: string; totalSeconds: number; focusScore: number; isBrowser: boolean; }
```

### New page
Create `web/src/app/(app)/hours-insights/page.tsx`:

1. Wrap in `<Suspense>` (required for `useSearchParams`).
2. Use `EmployeePage` shell for employee picker + loading/error/no-selection states.
3. Inside the render-prop, use `useUrlActivityFilter` for the filter state (Today/Yesterday/7d/30d/Custom).
4. Call `hoursInsightsApi.get({ employeeId, ...filter })` via `useQuery`.
5. Render:
   - **Stat tiles** (Total Time, Apps Used, Sites Visited, Focus Score)
   - **Stacked area chart** using Recharts `<AreaChart>` with `<Area type="monotone" stackId="1" />` for productive/unproductive/neutral
   - **Top Apps & Sites table** (or list) below the chart

### Chart component details
- Use the existing `ChartContainer` / `ChartTooltipContent` / `ChartLegendContent` from `web/src/components/ui/chart.tsx`.
- X-axis labels: for daily buckets → day name (Mon, Tue…); for hourly → `HH:00`.
- Tooltip: show bucket label + stacked seconds formatted via `formatSeconds`.
- Colors: `productive = #10b981` (green), `unproductive = #ef4444` (red), `neutral = #6b7280` (gray) — match `monitoring_types` seed colors.

### Files to create / modify (web)
- `web/src/app/(app)/hours-insights/page.tsx` — **new**
- `web/src/lib/api.ts` — add `hoursInsightsApi` + types

---

## 5. GUI Look & Feel

### Visual style
- Matches existing dashboard/journey pages: `bg-card rounded-xl border border-border shadow-card` cards.
- Stat tiles: same `UsageTile` pattern from `/employee-journey/apps` (icon + large number + label).
- Chart: responsive, `aspect-video` container, matches the existing `ChartContainer` sizing.
- Top-items table: striped rows, hover highlight, truncated names with tooltip.

### Interaction
- Changing employee or date range triggers a fresh query.
- `keepPreviousData` prevents chart flicker on filter changes.
- Loading: spinner in chart area + shimmer on table.
- Error: inline error message (no toast, per existing pattern).
- Empty: `EmptyState` component if no data.

### Responsive
- Stat tiles: `grid-cols-2 sm:grid-cols-4`.
- Chart: `ResponsiveContainer` width="100%".
- Table: `overflow-x-auto` on mobile.

---

## 6. Step-by-Step Build Order

1. **Server endpoint** (`hours_insights_handler.go` + repo method + route)
2. **Smoke test** with `psql` / curl to verify the SQL and JSON shape
3. **Web API client** additions in `api.ts`
4. **Page component** `hours-insights/page.tsx`
5. **Verify** with `npx tsc --noEmit` + `next build`

---

## 7. Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| Chart SQL is heavy on large datasets | Add `LIMIT` on buckets (max 168 for hourly, 60 for daily). Use `COALESCE(..., 0)` so missing buckets render as 0. |
| `app_items` may have browser_navigation + browser_tab duplicates for the same visit | Sum both — they represent the same real time. |
| Employees without any app data | Return empty chart + empty top-items list; web shows `EmptyState`. |
| Category missing for apps/sites | Fallback to `neutral` / "Uncategorized". |

---

## 8. Out of Scope (Future)
- Per-app breakdown inside the chart (drill-down on click)
- Export to CSV / PDF
- Comparison across multiple employees
- Real-time polling / WebSocket updates
