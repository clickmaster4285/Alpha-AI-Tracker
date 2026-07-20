# AGENTS.md

## Project overview

Alpha AI Tracker — an employee monitoring & productivity dashboard. Two sub-projects:

- **`web/`** — Next.js 15 + React 18 frontend (the only tracked code)
- **`client/`** — Avalonia UI desktop app (.NET 10/C#), git-ignored, do not modify

## Commands (run from `web/`)

```bash
cd web
npm run dev      # dev server
npm run build    # production build
npm run lint     # ESLint via next lint
```

No test suite, no formatter, no typecheck script. TypeScript strict mode is **off**.

## Architecture

- **App Router** with a `(app)` route group — all authenticated pages live under `src/app/(app)/`
- **Fully client-side** — every page/component uses `"use client"`. No server components, no API routes.
- **No backend** — all data is localStorage-based with hardcoded sample data (initialized on first visit)
- **shadcn/ui** components in `src/components/ui/` — add new UI primitives here via `npx shadcn@latest add <component>`
- **Path alias**: `@/*` → `src/*`
- **Fonts**: Plus Jakarta Sans (display), Inter (body) — loaded via Google Fonts in `globals.css`

## Auth & permissions

- Auth is localStorage-based with 8 fixed user accounts (password: `alphai123`)
- Login via `useAuth()` from `src/lib/auth.tsx`
- Role-based access via `usePermissions()` from `src/lib/permissions.tsx`
- Protected routes wrap pages via `<ProtectedRoute module="...">` in `(app)/layout.tsx`
- When adding a new page: add the route under `(app)/`, register the module key in `ALL_MODULES` in `permissions.tsx`, add nav entry in `AppSidebar.tsx`, and add the page title in `AppLayout.tsx`

## Style conventions

- Tailwind CSS with CSS variables for theming (light/dark via `class` strategy)
- Use `cn()` from `src/lib/utils.ts` to merge class names
- Custom animations defined in `tailwind.config.ts`: `fade-in`, `slide-in-left`, `pulse-soft`
- Sidebar uses custom `sidebar-*` CSS variable tokens
- Data store keys are prefixed with `alpha_ai_tracker_` (see `STORAGE_PREFIX` in `src/config.ts`)
