# Plan — Configuration pages UI/UX redesign + manual website creation

## Status

**Part 1 — Cross-OS catalog doubles (migration 024): COMPLETED** (2026-08-22)
**Part 2 — Flatpak bwrap PPID chain fix: COMPLETED** (2026-08-22)
**Part 3 — Web dynamic browser badge names: COMPLETED** (2026-08-22)
**Part 4 — Configuration pages UI/UX redesign + manual website creation: COMPLETED** (2026-08-22)

All planned changes have been applied and verified.

## Part 4 — Configuration pages UI/UX redesign + manual website creation

### Changes

1. **Server: `POST /api/v1/monitoring/websites`**
   - New `CreateWebsite` endpoint allows admins to manually add a website domain
   - Accepts `domain`, optional `typeId`, optional `categoryId`
   - Normalizes domain (strips protocol/path)
   - Server: `monitoring_handler.go`, `monitoring_service.go`, `monitoring_repo.go`, `router.go`

2. **Web: `ClassifiedItemsTable` component redesign**
   - Improved filter bar with status toggle chips (All / Classified / Unclassified)
   - Clearable search input with active filter count badge
   - Better table spacing, hover states, and visual hierarchy
   - Per-row classification status badges (Classified / Unclassified / Partial)
   - Richer empty state with contextual messaging
   - Better loading states

3. **Web: `websites/page.tsx` — Add Website button + dialog**
   - "Add Website" button in page header
   - Modal dialog with domain input, type select, category select
   - Domain auto-normalization (strips `https://`, `http://`, paths)
   - Success/error toasts
   - Cache invalidation after creation

4. **Web: `apps/page.tsx` — updated to use new `ClassifiedItemsTable` props**

### Verification
- Server: `go build`, `go vet` clean
- Web: `tsc --noEmit` clean, `next build` passes

## Part 4b — Fix 400 on manual website creation + UX improvements (2026-08-22)

**Root cause:** the Add Website dialog hardcoded type/category option values (1/2/3) that
did not match the live DB, and the server had no domain normalization. The dialog also
lacked duplicate detection.

**Fixes:**
1. Server: `normalizeDomain` in `monitoring_handler.go` strips protocol, path, query,
   fragment, and lowercases before any validation.
2. Frontend: `AddWebsiteDialog` fetches types + categories dynamically from the API
   (no hardcoded IDs).
3. Frontend: domain input auto-normalizes on every keystroke.
4. Frontend: blur/change checks the registry for duplicates and disables submit with
   an "Already exists" warning.
