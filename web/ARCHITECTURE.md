# Web Architecture — Alpha AI Tracker Dashboard

> **Last audited:** 2026-08-22
> **Changelog:**
> 2026-08-22: **Sidebar parent menus stay open when navigating to child pages.**
>   `AppSidebar` now auto-expands any parent section whose `children` array contains the current
>   `pathname`, so the menu stays open when the user opens a child page.
> 2026-08-22: **Configuration pages UI/UX redesign + manual website creation.**
>   `ClassifiedItemsTable` component redesigned with improved filter bar (status toggle chips, clearable filters,
>   active count badge), better table spacing/hover states, per-row classification status badges
>   (Classified/Unclassified/Partial), and richer empty/loading states. `websites/page.tsx` gains an
>   **Add Website** button + modal dialog that posts to new `POST /api/v1/monitoring/websites` (server creates
>   the row with optional type/category and normalizes the domain). Server: new `CreateWebsite` endpoint in
>   `monitoring_handler.go` + `MonitoringService` + `MonitoringRepo`. Verified: `go build`/`go vet` clean,
>   `tsc --noEmit` clean, `next build` passes.
> 2026-08-22: **Dynamic browser badge names on Web Activity — removed hardcoded `BROWSER_NAMES` map.**
>   `employee-journey/web/page.tsx` no longer translates `metadataJson.processName` through a hardcoded
>   10-entry map. The server resolves the friendly browser name dynamically: `ListAppItems` extracts
>   `processName` from `metadata_json` and looks it up in `installed_applications` (`binary_name` /
>   `app_name` match, `is_browser = true`), returning `browserName` in the API response. The web page
>   uses `item.browserName` directly; for embedded webviews (`source: "webview"`) the host app's
>   process name is preserved as before. Verified: `go build`/`go vet` clean, `tsc --noEmit` clean.
> 2026-08-18: **Server-side date/search filters on App Usage + Web Activity + structural browser detection + UX fixes.**
> - **Server-side filters**: `GET /app-sessions` and `GET /app-items` now accept `dateFrom`/`dateTo` (RFC3339 or
>   date-only) — sessions filtered on `started_at`, items on `opened_at`; combined with existing `search`/`platform`.
>   App-items search also matches `url`/`domain` (so searching "youtube" finds the exact page).
> - **Shared `ActivityFilters` component** (`components/journey/ActivityFilters.tsx`): debounced search (300ms),
>   date presets (Today/Yesterday/7d/30d/All Time) defaulting to **Today** with real local-day bounds
>   (`createDefaultFilter()` sends `dateFrom`/`dateTo` on initial load — no stale data leaking), plus custom
>   range via Calendar popover (`react-day-picker` + `date-fns`).
> - **App Usage page** (`employee-journey/apps`): Duration column replaces Foreground/Background — computed from
>   `endedAt - startedAt` (closed) or `now - startedAt` (running). Active Time tile = sum of durations.
>   Apps with >1 session show a chevron → expandable nested table (Opened/Closed/Duration/Details per session).
>   Uses `keepPreviousData` so filter changes never swap the whole content to a spinner.
> - **Web Activity page** (`employee-journey/web`): Pages grouped by domain ("Visited Sites") with expandable
>   groups (Visits/Duration/Last Visited). Sites with >1 visit get chevron → nested page list (Page/URL/Visited/
>   Duration). Browser badge (Chrome/Floorp/etc.) from `metadata_json.processName` shown on every row — site
>   rows show distinct browsers present. Search switches to flat "Matching Pages" view (exact URL visible).
>   `keepPreviousData` for smooth filter transitions.
> - **Filter UX fixes**: `ActivityFilters` always mounted (no unmount on empty/error/loading) — search field
>   never loses focus, filters never disappear on no-match. `next.config.ts` gains `allowedDevOrigins`
>   (suppresses cross-origin dev warning for LAN IP).
> 2026-08-18: **Employee Journey + Device Specs modules** — the old `/users/[id]` detail page was replaced by nine
> pages behind a shared `EmployeePage` shell + `EmployeeSelector` picker (deep-linkable via `?employeeId=`):
> Session Timeline, App Usage, Web Activity (real APIs, infinite scroll) + Screenshots/Location Trail placeholders;
> Device Specs: Hardware Overview, Installed Software, Peripherals, Permissions (all over `GET /employees/:id/detail`).
> The employees table action menu now deep-links to both modules; `AppItem` gained the journey fields; new
> `employee-journey` + `device-specs` permission modules; shared helpers `lib/format.ts`, `hooks/use-employee-detail.ts`,
> components `EmptyState`/`InventoryTable`/`FocusTime`/`DeviceClassIcon`.
> 2026-08-17: Sidebar restructure — Device Specs became a collapsible section with 4 children (Hardware Overview,
> Installed Software, Peripherals, Permissions); Employee Journey stays collapsible with 5 children.
> 2026-08-13: Employees rename — `/users`, `/users/[id]`, `/users/activity` moved to `/employees`, `/employees/[id]`,
> `/employees/activity`; employee `role` removed (see [AGENTS.md](./AGENTS.md) changelog).
> 2026-08-11: `/users/[id]` detail page + `GET /employees/:id/detail` aggregate endpoint; users list action menu became a
> portal-rendered Radix `DropdownMenu`.
> 2026-07-25: Updated comprehensive/logs page to use new app_sessions API (replaced old activityLogsApi). Added appSessionsApi to api.ts with AppSession types. Tabs removed (system log and productive/unproductive tabs not yet implemented for new schema).  
> **Service completion (honest):** ~20%

---

## 1. Responsibility & Scope

**Owns:**
- Admin-facing web dashboard for employee monitoring and productivity analytics
- Authentication UI (login page) and session management via httpOnly cookies
- Employee and department management (CRUD via real API calls)
- Activity log viewing with filtering and pagination (via real API calls)
- Permission/role management UI (client-side only, stored in localStorage)
- Desktop app download dialog (fetches latest GitHub release)
- Mock data seeding for ~25+ feature pages that are not yet API-connected

**Does NOT own:**
- Any direct communication with desktop clients (all data goes through the server)
- Real-time data delivery (no WebSocket, SSE, or polling)
- Server-side rendering of protected data (all pages are `"use client"` — CSR only)
- Background job processing or data aggregation

---

## 2. Tech Stack Detail

| Component | Library | Version |
|---|---|---|
| **Framework** | Next.js (App Router) | ^15.3.4 |
| **React** | react / react-dom | ^18.3.1 |
| **State (Auth)** | @reduxjs/toolkit | ^2.12.0 |
| **Data Fetching** | @tanstack/react-query | ^5.83.0 |
| **UI Components** | shadcn/ui (Radix primitives) | — |
| **Styling** | Tailwind CSS | ^4.3.3 |
| **Animation** | framer-motion | ^12.35.2 |
| **Charts** | recharts | ^2.15.4 |
| **Forms** | react-hook-form + zod | ^7.53.2 / ^3.25.76 |
| **Toast/Sonner** | sonner | ^1.7.4 |
| **Icons** | lucide-react | ^0.462.0 |
| **Carousel** | embla-carousel-react | ^8.6.0 |
| **Drawer** | vaul | ^0.9.9 |
| **OTP Input** | input-otp | ^1.4.2 |
| **Date Picker** | react-day-picker | ^8.10.1 |
| **Date Utils** | date-fns | ^3.6.0 |
| **Theming** | next-themes | ^0.3.0 |
| **Class Merge** | tailwind-merge + clsx | ^2.6.0 / ^2.1.1 |
| **TypeScript** | typescript | ^5.8.3 |

### Build Tooling

- `@tailwindcss/postcss` v4.3.3 (PostCSS plugin for Tailwind v4)
- `@tailwindcss/typography` v0.5.16
- ESLint v9.32.0 with `eslint-config-next`
- `tw-animate-css` v1.4.0 (CSS animation utilities)

---

## 3. Project Structure

```
web/
├── next.config.ts               # Rewrites: /api/* → backend, allowedDevOrigins
├── tsconfig.json                # Path alias @/* → src/*
├── package.json                 # Dependencies, scripts
├── postcss.config.js            # @tailwindcss/postcss
├── components.json              # shadcn/ui config
├── public/
│   ├── robots.txt
│   ├── app-logo.png
│   └── favicon.ico
│
└── src/
    ├── config.ts                # APP_NAME, STORAGE_PREFIX, GITHUB_REPO constants
    ├── globals.css              # Tailwind v4 CSS variables, dark/light themes
    │
    ├── lib/
    │   ├── api.ts               # API client: fetch wrapper with credentials:'include'
    │   │                        #   authApi, employeesApi, departmentsApi, appSessionsApi,
    │   │                        #   appItemsApi, healthApi (AppItem carries the journey fields)
    │   ├── format.ts            # Shared formatters: formatMb / formatSeconds / formatDuration
    │   │                        #   / formatDateTime / formatDate / formatDateShort
    │   ├── auth.tsx             # AuthContext, AuthProvider, useAuth, fallback users
    │   ├── permissions.tsx      # PermissionsContext, role-based permission matrix
    │   │                        #   (client-side only, stored in localStorage)
    │   ├── store.ts             # localStorage-based mock data store
    │   │                        #   (seeded with 12 sample employees, activity logs, etc.)
    │   ├── utils.ts             # cn() helper (clsx + tailwind-merge)
    │   └── store/
    │       ├── redux.ts         # Redux store with authSlice (checkAuth, loginUser, logoutUser)
    │       └── hooks.ts         # useAppDispatch, useAppSelector typed hooks
    │
    ├── hooks/
    │   ├── use-mobile.tsx       # Responsive detection hook
    │   ├── use-toast.ts         # Toast notification hook (shadcn/ui)
    │   └── use-employee-detail.ts  # Query for GET /employees/:id/detail (enabled once an employee is picked)
    │
    ├── components/
    │   ├── providers.tsx        # Root provider: Redux + QueryClient + Tooltip + Toasts + Auth + Permissions
    │   ├── NavLink.tsx          # Active nav link component
    │   ├── EmployeeSelector.tsx # Searchable employee picker (shared by Journey/Device-Specs pages)
    │   ├── employees/           # Shared employee-page building blocks
    │   │   ├── EmployeePage.tsx #   Page shell: header + picker + loading/error/no-selection
    │   │   ├── InventoryTable.tsx  EmptyState.tsx  DeviceClassIcon.tsx
    │   ├── journey/
    │   │   ├── ActivityFilters.tsx  Shared search + date presets + custom range filter bar
    │   │   └── FocusTime.tsx    #   Foreground/background stacked bar
    │   ├── ui/                  # ~45 shadcn/ui component files (button, card, dialog, table, chart, etc.)
    │   │   ├── button.tsx       #  (all are standard shadcn/ui, no customization)
    │   │   ├── card.tsx
    │   │   ├── chart.tsx        # Recharts wrapper
    │   │   ├── sidebar.tsx      # shadcn sidebar (not used — custom sidebar in layout/)
    │   │   ├── StatsCard.tsx    # Custom: animated stats card with Framer Motion
    │   │   └── ...              # 40+ other shadcn components
    │   └── layout/
    │       ├── AppSidebar.tsx   # Custom sidebar: collapsible, accordion menus, permission-filtered
    │       ├── AppLayout.tsx    # Layout wrapper: sidebar + topbar + main content
    │       ├── ProtectedRoute.tsx  # Auth guard: redirects to /login if unauthenticated
    │       └── TopBar.tsx       # Header bar: page title, search, user avatar, logout
    │
    └── app/                     # Next.js App Router
        ├── layout.tsx           # Root layout: Providers wrapper, metadata
        ├── page.tsx             # Home page: redirects to /dashboard or /login
        └── not-found.tsx        # 404 page
        │
        ├── login/               # Login page (unauthenticated)
        │   └── page.tsx         # Animated hero + email/password form
        ├── forgot-password/     # Placeholder page
        ├── reset-password/      # Placeholder page
        └── mfa/                 # Placeholder page
        │
        └── (app)/               # Authenticated route group
            ├── layout.tsx       # Wraps children with ProtectedRoute + AppLayout
            │
            ├── dashboard/       # Dashboard: stats cards, best performer, chart (mock data)
            ├── employees/       # Employees list: CRUD via real API, generate secret dialog,
            │   └── activity/    #   action menu deep-links to Journey/Device Specs
            │                    #   (activity status: mock data; the /employees/[id] detail
            │                    #   page was removed 2026-08-18 — see Employee Journey/Device Specs)
            ├── departments/     # Department CRUD via real API
            │
            ├── employee-journey/# Per-employee journey, shared EmployeePage shell + picker
            │   ├── timeline/    #   Session timeline — real API, infinite scroll
            │   ├── apps/        #   App usage — real API, duration aggregation, expandable session groups
            │   ├── web/         #   Web activity — real API, domain-grouped, browser badges, search flat view
            │   ├── screenshots/ #   Placeholder — client collects none
            │   └── location/    #   Placeholder — client collects none
            ├── device-specs/    # Per-employee machine picture (detail API)
            │   ├── page.tsx     #   Hardware overview
            │   ├── software/    #   Installed software (apps/packages tabs + search)
            │   ├── peripherals/ #   Peripherals (plugged/unplugged cards)
            │   └── permissions/ #   Permission checks
            │
            ├── logs/
            │   ├── comprehensive/  # Activity logs with filtering (real API)
            │   ├── insights/       # User insights (mock data)
            │   └── graphical/      # Graphical logs (mock data)
            │
            ├── charts/
            │   ├── productivity/   # Productivity chart (mock data)
            │   └── activity/       # Activity chart (mock data)
            │
            ├── apps/               # Apps & Websites (mock data)
            ├── screenshots/        # Screenshots (mock data)
            ├── live-stream/        # Live stream (mock/placeholder)
            ├── emails/             # Emails & Alerts (mock data)
            ├── kpis/               # KPIs & KRAs (mock data)
            ├── roles/              # Roles (mock data)
            ├── shifts/             # Shift management (mock data)
            ├── timesheets/         # Timesheets (mock data)
            ├── attendance/         # Attendance log (mock data)
            ├── gps-location/       # GPS & Location (mock data)
            ├── hours-insights/     # Hours insights (mock data)
            ├── productivity-scoring/  # Score card (mock data)
            ├── goals/              # Goals & OKRs (mock data)
            ├── reports/            # Reports & Analytics (mock data)
            ├── audit-log/          # Audit log (mock data)
            ├── executive-dashboard/  # Executive dashboard (mock data)
            ├── dlp-alerts/         # DLP Alerts (mock data)
            ├── dlp-rules/          # DLP Rules (mock data)
            ├── shadow-it/          # Shadow IT (mock data)
            ├── ai-summary/         # AI Summary (mock data)
            ├── onboarding/         # Onboarding (mock data)
            ├── employee-portal/    # Employee portal (mock data)
            ├── projects/           # Projects (mock data)
            │
            └── settings/
                ├── page.tsx             # General settings (mock data)
                ├── permissions/         # Permission management (client-side only)
                ├── tracking/            # Tracking settings (mock data)
                ├── user-management/     # User management (mock data)
                ├── notifications/       # Notification config (mock data)
                ├── billing/             # Billing (placeholder)
                ├── compliance/          # GDPR & Compliance (placeholder)
                └── security/            # Security settings (placeholder)
```

---

## 4. Data Flow from Server

### Auth Flow

```
┌──────────┐     GET /api/v1/auth/check        ┌──────────┐
│  Web App  │──────────────────────────────────▶│  Server   │
│  (on      │  (cookie auto-sent)               │          │
│   mount)  │◀──────────────────────────────────│          │
└──────────┘     {authenticated: true, user}    └──────────┘
       │
       │ authenticated → render ProtectedRoute children
       │ not authenticated → redirect to /login?redirect=<path>
       ▼
┌──────────┐     POST /api/v1/auth/login        ┌──────────┐
│  Login    │──────────────────────────────────▶│  Server   │
│  Page     │  {email, password}                │          │
│           │◀──────────────────────────────────│          │
└──────────┘     Sets httpOnly cookie           └──────────┘
       │
       │ success → router.replace('/dashboard')
       │ fail → show error
```

### Data Fetching Strategy

| Strategy | Usage |
|---|---|
| **CSR (Client-Side Rendering)** | All pages use `"use client"` — no SSR, no ISR |
| **TanStack Query** | Employees, Departments, Logs, Dashboard tiles, User Management, Roles, Employee Journey, Device Specs pages (real API data) |
| **Infinite scroll** | `useInfiniteQuery` + IntersectionObserver sentinel — **the only allowed list pagination** on every list page (Session Timeline, Web Activity, Employees, User Management, …), server-side pagination via `page`/`perPage` (see *Web Infinite-Scroll Rule* below) |
| **Honest empty states** | Pages whose backend doesn't exist yet render a header + `EmptyState` explaining the gap (2026-08-25 removed all localStorage mock data — none remains) |
| **Next.js Rewrites** | `/api/:path*` → `http://localhost:8080/api/:path*` (or `NEXT_PUBLIC_API_URL`) |
| **Redux** | Auth state only (user, loading, auth status) |

### Real-Time Updates

**Not implemented.** No polling, no WebSocket, no Server-Sent Events. The web dashboard only shows data at the moment of page load — it never updates automatically.

### Web Infinite-Scroll Rule (mandatory)

**Every list/table page MUST paginate with server-side infinite scrolling — Next/Previous buttons are forbidden.**

- Use `useInfiniteQuery` (TanStack Query v5) with `initialPageParam: 1` and
  `getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined)`.
- Flatten pages with `data?.pages.flatMap(p => p.data)`.
- Trigger the next fetch with an **IntersectionObserver sentinel** (`rootMargin: '300px'`, gated on
  `hasNextPage && !isFetchingNextPage`) rendered below the list.
- Show an inline "Loading more…" row while `isFetchingNextPage`; show a "Showing all N" footer when
  `hasNextPage` is false.
- Filters/search change the query key (a fresh infinite query starts at page 1) — never a `page` state.
- Reference implementation: `src/app/(app)/employees/page.tsx` and the Session Timeline journey page.

---

## 5. Pages/Routes Inventory & Data Dependencies

> The old `/employees/[id]` single-page detail view was removed 2026-08-18 — it is replaced by the
> Employee Journey + Device Specs modules below (both deep-linkable via `?employeeId=`).

| Route | Page | Data Source | Real API? |
|---|---|---|---|
| `/login` | Login | Server (auth) | ✅ |
| `/unauthorized` | Access denied (RouteGuard target) | — | ✅ |
| `/dashboard` | Dashboard | Server (`GET /employees`, `/departments`, `/app-sessions?dateFrom=`, `/app-items?itemType=browser_tab&dateFrom=`) | ✅ |
| `/employees` | Employee list | Server | ✅ |
| `/employees/activity` | Activity status | Honest empty state (no endpoint) | ❌ |
| `/employee-journey/timeline` | Session timeline | Server (`GET /app-sessions`, infinite scroll) | ✅ |
| `/employee-journey/apps` | App usage | Server (aggregated `GET /app-sessions`) | ✅ |
| `/employee-journey/web` | Web activity | Server (`GET /app-items?itemType=browser_tab`) | ✅ |
| `/employee-journey/screenshots` | Screenshots | Placeholder — client collects none | ❌ |
| `/employee-journey/location` | Location trail | Placeholder — client collects none | ❌ |
| `/device-specs` | Hardware overview | Server (`GET /employees/:id/detail`) | ✅ |
| `/device-specs/software` | Installed software | Server (detail payload) | ✅ |
| `/device-specs/peripherals` | Peripherals | Server (detail payload) | ✅ |
| `/device-specs/permissions` | Permissions | Server (detail payload) | ✅ |
| `/departments` | Departments | Server | ✅ |
| `/roles` | Roles + per-submodule permission toggles | Server (`GET /modules`, `/roles` CRUD) | ✅ |
| `/logs/comprehensive` | Activity logs | Server | ✅ |
| `/logs/insights` | Log insights | Honest empty state (no endpoint) | ❌ |
| `/logs/graphical` | Graphical logs | Honest empty state (no endpoint) | ❌ |
| `/charts/productivity` | Productivity chart | Hardcoded Recharts demo data | ❌ |
| `/charts/activity` | Activity chart | Hardcoded Recharts demo data | ❌ |
| `/configuration/apps` | Applications classification | Server (`GET/PATCH /monitoring/apps`) | ✅ |
| `/configuration/websites` | Websites classification | Server (`GET/POST/PATCH /monitoring/websites`) | ✅ |
| `/configuration/categories` | Categories & Types CRUD | Server (`/monitoring/types`, `/monitoring/categories`) | ✅ |
| `/screenshots` | Screenshots | Honest empty state (no endpoint) | ❌ |
| `/live-stream` | Live stream | Honest empty state (no endpoint) | ❌ |
| `/kpis` | KPIs & KRAs | Hardcoded demo data (scaffolding) | ❌ |
| `/shifts` | Shift management | Hardcoded demo data (scaffolding) | ❌ |
| `/timesheets` | Timesheets | Hardcoded demo data (scaffolding) | ❌ |
| `/attendance` | Attendance | Hardcoded demo data (scaffolding) | ❌ |
| `/gps-location` | GPS & Location | Hardcoded demo data (scaffolding) | ❌ |
| `/hours-insights` | Hours insights | Honest empty state (no endpoint) | ❌ |
| `/productivity-scoring` | Score card | Hardcoded demo data (scaffolding) | ❌ |
| `/goals` | Goals & OKRs | Hardcoded demo data (scaffolding) | ❌ |
| `/reports` | Reports | Hardcoded demo data (scaffolding) | ❌ |
| `/audit-log` | Audit log | Hardcoded demo data (scaffolding) | ❌ |
| `/executive-dashboard` | Exec dashboard | Hardcoded demo data (scaffolding) | ❌ |
| `/dlp-alerts` | DLP Alerts | Hardcoded demo data (scaffolding) | ❌ |
| `/dlp-rules` | DLP Rules | Hardcoded demo data (scaffolding) | ❌ |
| `/shadow-it` | Shadow IT | Hardcoded demo data (scaffolding) | ❌ |
| `/emails` | Emails & Alerts | Hardcoded demo data (scaffolding) | ❌ |
| `/projects` | Projects | Hardcoded demo data (scaffolding) | ❌ |
| `/ai-summary` | AI Summary | Honest empty state (no endpoint) | ❌ |
| `/onboarding` | Onboarding | Hardcoded demo data (scaffolding) | ❌ |
| `/employee-portal` | Employee portal | Hardcoded demo data (scaffolding) | ❌ |
| `/settings` | General settings | Static hub page | ❌ |
| `/settings/tracking` | Tracking settings | In-memory session state only (explicit non-persistence notice) | ❌ |
| `/settings/user-management` | User management (CRUD + role assignment, infinite scroll) | Server (`/users`, `/roles`) | ✅ |
| `/settings/notifications` | Notifications | Hardcoded demo data (scaffolding) | ❌ |
| `/settings/billing` | Billing | Placeholder | ❌ |
| `/settings/compliance` | Compliance | Placeholder | ❌ |
| `/settings/security` | Security | Placeholder | ❌ |

> The former `src/lib/store.ts` localStorage mock-data factory was **deleted 2026-08-25**. Pages that
> had no real backend were converted to honest empty states rather than keeping fake data; pages
> marked "Hardcoded demo data (scaffolding)" above keep their in-file demo constants and await endpoints.
> `app/page.tsx` purges orphaned `alpha_ai_tracker_*` keys from visitors' localStorage on first hit.
> The legacy client-side permission matrix (`/settings/permissions` page + its localStorage store)
> was deleted in the same pass — permissions are now server-driven only (§6).

---

## 6. Auth / Session Handling

### Auth Provider (`auth.tsx`)

- Wraps the entire app (inside `Providers.tsx`)
- On mount: dispatches `checkAuth()` thunk → calls `GET /api/v1/auth/check`; if it reports
  `authenticated:false` (which an EXPIRED access token also produces — `/auth/check` is optional-auth
  and never returns 401), the thunk first tries `POST /api/v1/auth/refresh` and re-checks once before
  declaring the session dead
- Redux `auth` slice tracks: `user`, `isLoading`, `isAuthenticated`, `error`
- `ProtectedRoute` component reads `isAuthenticated` and either renders children or redirects to `/login?redirect=<path>`
- `Login` page calls `loginUser()` thunk → `POST /api/v1/auth/login` → server sets BOTH cookies (`auth_token` access ~15 min + `refresh_token` 30 days, both httpOnly)
- `Logout` calls `logoutUser()` → `POST /api/v1/auth/logout` → server REVOKES the refresh row + clears both cookies; Redux resets

### Access-Token Refresh (`api.ts` request wrapper)

- Every API call goes through `request()`; on a **401** (except `/auth/login` and
  `/auth/refresh` themselves) it runs ONE single-flight `performRefresh()`
  (module-level promise so concurrent 401s share a rotation) and replays the original request once
- Refresh success → transparent retry, user never notices the 15-minute expiry
- Refresh failure → session is unrecoverable → `window.location.replace('/login')`
  forces the full reload that also resets Redux state (the "force to login" rule)
- A `_retried` flag guarantees at most one refresh-replay cycle per call (no loops)

### Fallback Users

`auth.tsx` still exports `FALLBACK_USERS` (8 sample identities) on the context for legacy consumers,
but nothing in the permission system uses them anymore.

### Permission System (`permissions.tsx` — SERVER-driven since 2026-08-25)

- The server embeds the user's granted **submodule keys** (RBAC `role_submodule_permissions`,
  migration 025) into every auth response as `user.permissions: string[]`
- `PermissionsProvider` turns that array into an allow-list `Set`; `canAccess(role, module)` returns
  `'full'` for `company_admin`, `'full'` when the set is absent/null (**fail-open** — sessions from
  before the RBAC rollout keep working), otherwise `'full' | 'none'` by set membership
- **Entirely removed**: the old localStorage matrix (`ALL_MODULES` / `DEFAULT_PERMISSIONS` /
  `alpha_ai_tracker_dynamic_permissions`) and the `/settings/permissions` editor page — the server's
  `/roles` CRUD page (Settings ▸ Roles) is now the only place permissions are edited
- `AppSidebar` filters nav entries through `canAccess`; `RouteGuard` resolves the current pathname to
  a sidebar module (`findModuleForPath`) and redirects denied routes to `/unauthorized`
- ⚠️ Enforcement is CLIENT-side only — API middleware performs no role checks yet

---

## 7. Production-Readiness Gaps

| Gap | Severity | Details |
|---|---|---|
| **Scaffolding pages dominate** | 🟠 Medium | ~35 of ~50 pages have no backend endpoint; most render hardcoded demo constants or honest empty states. Real-API: login, dashboard tiles, employees, user-management, roles, departments, logs/comprehensive, configuration/*, Employee Journey + Device Specs. |
| **No tests** | 🔴 High | 0 test files. No unit, integration, or e2e tests. |
| **Client-side only permission enforcement** | 🟠 Medium | RBAC grants are checked only in the browser (sidebar + RouteGuard). Any crafted request with a valid cookie reaches every protected endpoint. Server middleware has no role checks. |
| **No error boundaries** | 🟠 Medium | Uncaught React errors crash the entire page. No per-page error boundaries. |
| **No SEO** | 🟢 Low | All pages are `"use client"` — no SSR, no metadata beyond the root layout. Fine for an admin dashboard. |
| **No accessibility** | 🟠 Medium | Many interactive elements lack `aria-label`, `role`, or keyboard navigation. The sidebar menus are `<button>` elements with no aria-expanded. |
| **No API error boundary** | 🟢 Low | If the server is down, the API client throws `ApiError` but only login and users pages handle it gracefully. Other pages would show blank/error states. |
| **Hardcoded GitHub repo** | 🟢 Low | Default repo is `clickmaster4285/Alpha-AI-Tracker` — not the org repo. Overridable via env but easy to miss. |
| **No build-time validation** | 🟢 Low | `strict: false` in tsconfig.json. TypeScript errors that would be caught in strict mode are silently ignored. |
| **Large bundle** | 🟢 Low | ~45 Radix UI components, Framer Motion, Recharts, Redux, TanStack Query — likely a heavy initial JS payload. No code splitting per page. |

---

## 8. Immediate Next Steps

1. ~~Connect real API to dashboard~~ ✅ done 2026-08-25 — dashboard tiles are live-API; charts still demo data pending productivity endpoints
2. **Add tests** — start with the `api.ts` client and auth flows using MSW or similar
3. **Add error boundaries** — wrap each page section in `React.ErrorBoundary`
4. **Server-side permission enforcement** — validate `user.permissions`/role per route in Echo middleware; client checks are UX only
5. **Enable `strict: true` in tsconfig** — fix the TypeScript errors it reveals
6. **Wire the empty-state pages to real endpoints** as they ship (screenshots, insights, graphical logs, activity status)
7. **Add code splitting** — dynamic imports for chart libraries, heavy pages
