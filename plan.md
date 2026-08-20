# Plan — Catalog double-app merge (024) + Flatpak xdg-dbus-proxy label fix

## Part 1 — Cross-OS catalog doubles (server-only, approved method)

**Method:** merge catalog rows by **normalized display name** (`app_name` → lowercase,
strip non-alphanumerics). Display names are OS-independent, so this covers every tool
category and every OS flavor (Linux/.desktop incl. Parrot/Kali, Windows Start-Menu,
macOS bundles). Rejected alternative: merging by `binary_name` (evidence: same binary
→ many products: `libreoffice`→6 apps, `flatpak`→Floorp+Warpinator, `gapplication`→
Maps+Weather, `OfficeClickToRun`→M365+Office2019, `OUTLOOK`→Outlook+Outlook classic,
`software-properties-gtk`→Additional Drivers+Software & Updates; also misses Google
Chrome `chrome` vs `google-chrome-stable`).

Caveats (honest): (1) two unrelated products sharing the exact display name would
wrongly merge (rare); (2) same product with genuinely different display names across
OSes won't merge (rare — client already normalizes e.g. "Microsoft Visual Studio Code
(User)").

### Steps
1. **Migration `024_catalog_merge.sql`** (one transaction, idempotent):
   - key = `regexp_replace(lower(app_name), '[^a-z0-9]', '', 'g')`
   - group non-deleted rows having `count(*) > 1`
   - winner = most `employee_installed_applications` links, tie → earliest `created_at`
   - re-point `employee_installed_applications.installed_application_id` loser→winner
     (junction dedup via `ON CONFLICT (employee_id, installed_application_id) DO NOTHING`
     then delete the colliding duplicate link)
   - re-point `app_sessions.installed_app_id` loser→winner
   - carry loser's `type_id` / `category_id` onto the winner if the winner has none
   - soft-delete losers (`deleted_at = now()`) — keeps the UNIQUE `app_fingerprint`
     constraint intact
   - expected result on dev DB: 178 → ~171 rows, 7 doubles gone
2. **Sync hardening** — `server/internal/repository/new_schema_repo.go`
   `UpsertApplicationCatalog`: before INSERT, look up an existing non-deleted row by
   normalized `app_name`; if found reuse it (link employee to it) instead of creating a
   new fingerprint row. Stops future Linux/Windows syncs re-creating doubles.
3. Verify: `go build` / `go vet` / `go test` clean; apply migration to dev DB; smoke
   test a live sync + the Apps page.

## Part 2 — `xdg-dbus-proxy` label on Floorp (client)

**Root cause (verified live):** catalog row is correct (`Floorp` / binary `flatpak` /
`one.ablaze.floorp`). Bad label is the browser tracker's runtime `ProcessName` (shown as
the browser/source badge on Web Activity, used for app-session lookups). Flatpak
registers the window with AT-SPI under the **`xdg-dbus-proxy` PID**, which has **no
`FLATPAK_ID`** in its environ (live: PID 53874 has only `DBUS_SESSION_BUS_ADDRESS`), so
`resolve_app_name(pid)` — which reads only that PID's own environ — falls back to
`/proc/comm` = `xdg-dbus-proxy`. Real app (PID 53878, `FLATPAK_ID=one.ablaze.floorp`) is a
sibling under the same bwrap root (`bwrap --args 41 -- floorp`).

Process tree (live): `gnome-shell(2712) → bwrap(53844) "bwrap --args 41 -- floorp"`
→ `bwrap(53873) → xdg-dbus-proxy(53874)`, and `→ bwrap(53877) → floorp(53878)`.

### Steps
1. **Extend `resolve_app_name(pid)`** in the embedded Python probe of
   `client/Core/BrowserAccessibility/LinuxAtSpiBrowserReader.cs` (~line 454):
   - if own environ has no `FLATPAK_ID`, walk **up the PPID chain through `bwrap`** to
     the flatpak root (top-most `bwrap` whose parent is not `bwrap`)
   - DFS that root's descendants for a process with `FLATPAK_ID` → use its short name
   - fallback: parse the root's cmdline `bwrap --args N -- <name>` → `<name>`
   - `bwrap`/`xdg-dbus-proxy` are flatpak's OS-level sandbox binaries (structural, no
     product names) → No-Hardcoded-Names Rule compliant
   - C# `ReadComm`/`_pidNameCache` is fed from probe results → fixed automatically
2. **Optional nicety:** `InstalledAppDetector` stores the flatpak short id (`floorp`) as
   `binary_name` instead of `flatpak` (from `Exec=flatpak run <id>`), so binary lookups
   match too. (Fuzzy lookup already matches Floorp via `app_name`, so not required.)
3. **Installer-Parity:** `dotnet build` clean + verify in an installed build; ships in
   the next installer build.

## Verification
- Server: `go build`, `go vet`, `go test ./...`, migration applied + smoke tested on
  dev DB (apps count, live sync, classification intact).
- Client: `dotnet build` 0/0.
- Web (untouched, sanity): `tsc --noEmit`, `next build`.