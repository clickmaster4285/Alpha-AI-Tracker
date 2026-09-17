# Plan: Client Terms & Conditions Acceptance Flow

Status: plan-only; no code changes yet. Awaiting review before implementation.

## Objective

Gate the desktop client on the server's Terms & Conditions: fetch active terms, persist them in
SQLite, show a locked fullscreen acceptance flow, and continue only after every pending term is
accepted — with consent reported back to the server's audit trail.

## Evidence — what already exists (verified in repo)

The contract side of this feature is **already shipped**; only the client is missing:

- **Server** (per-feature T&C consent framework, 2026-09-14):
  - Migration `036_terms_consent.sql` — append-only `terms_consent` (`employee_id`, `feature_id`,
    `terms_version`, `action`, `created_at`; UNIQUE on `(employee_id, feature_id, terms_version, action)`).
  - Migrations `037`/`038` — `terms_content` (`id` UUID-text, `heading`, `body` **HTML**,
    `terms_version`, `is_system`, `feature_id`, `term_type`, `is_active`, `sort_order`, soft delete).
    Four featured terms seeded idempotently at boot (`terms_content_seeder.go`): `app_usage`,
    `browser_journey`, `file_journey`, `live_view` — all `terms_version` `1.0`.
  - Routes in `router.go`, all under the **JWTAuth-protected** group:
    `POST /terms-consent/sync`, `GET /terms-consent?employeeId=`,
    `GET /terms-consent/check?employeeId=&featureId=`, and CRUD on `/terms-content`
    (incl. `PATCH /terms-content/:id/active` for the admin `is_active` toggle).
- **Web**: `termsContentApi` + `termsConsentApi` in `web/src/lib/api.ts`; admin pages under
  `/settings/terms-and-conditions`. No web work needed.
- **Client**: zero terms code today. This plan is client-only unless a gap is found.

## Contract facts that resolve prior open questions

| Prior open question | Resolved by repo evidence |
|---|---|
| Server contract shape? | Exists — endpoints above; no new server endpoint required for the base flow |
| Version/upgrade detection? | Per-term `terms_version` + content hash of (`heading`,`body`) — **not** app version |
| Accepted status per user/machine? | Per **employee** (`terms_consent.employee_id`); local rows store the employee id too |
| Acceptance order? | Server orders by `sort_order ASC, created_at ASC` — client presents sequentially |
| Active filter? | `GET /terms-content` returns all non-deleted terms; client filters `is_active == 1` |

Additional contract notes:

- Auth: client calls use the employee JWT as `Authorization: Bearer …` (`ScheduleCacheService`
  pattern — the terms routes are NOT under `DeviceAuth`).
- `POST /terms-consent/sync` entries: `{ featureId, termsVersion, action, acceptedAt, revokedAt }`;
  the UNIQUE constraint makes re-sending an accepted entry idempotent.
- A term that becomes `is_active = 0` or is deleted server-side must drop out of the local pending
  queue (deactivation/deletion is an admin "un-require" signal).

## Planned workstreams

### 1) Client storage (`client_terms` in SQLite)

- New table `client_terms`: `id TEXT PK` (= server `terms_content.id`), `feature_id`, `heading`,
  `body`, `terms_version`, `content_hash`, `is_accepted INTEGER`, `accepted_at`, `employee_id`,
  `synced_at`, `created_at`, `updated_at`.
- Equality rule: a server term is "seen" when `(id, terms_version, content_hash)` matches a local
  row. Only new or changed terms insert a pending row (`is_accepted = 0`); accepted rows are never
  duplicated. Content hash = SHA-256 of normalized (`heading` + `body`).
- Migration via the existing idempotent `MigrateSql` strategy (`CREATE TABLE IF NOT EXISTS` +
  caught `ALTER`s) in `Storage/DatabaseSchema.cs`; CRUD in `Storage/SqliteLogStore.cs` behind the
  existing connection gate.
- Purge rule: pending rows whose feature is no longer active/deleted server-side are removed;
  accepted rows are retained (audit mirror) and re-verified against the server on each fetch.

### 2) Fetch + diff service (`Services/TermsService.cs`, new)

- Follow the `ScheduleCacheService` pattern: pull on login/session-restore, on a periodic timer,
  and after resume; single `HttpClient` call `GET /terms-content` with the employee Bearer token.
- Diff → insert pending rows → expose `HasPendingTerms` + ordered pending list.
- Offline behavior: if the fetch fails, use the locally stored pending queue (never block on the
  network); if there is no cached state and the server is unreachable, the client cannot be logged
  in anyway — log and defer to the next cycle.
- Acceptance path: on accept, POST the consent entry to `/terms-consent/sync` immediately (Bearer
  auth) and only mark the local row `is_accepted = 1` after a 2xx — an unacknowledged acceptance
  must stay pending locally so it retries (same principle as the 2026-09-09 sync-fix rule: a row
  the server refused must never masquerade as accepted). On persistent failure, queue the consent
  for the next fetch cycle.

### 3) Startup gating (router + shell)

- New router state following the existing guard-property pattern (`IsProfile` /
  `RequiresPermissionAction` in `MainViewModel`): `RequiresTermsAcceptance` short-circuits the
  shell before Dashboard, reusing the permission-wizard gating idiom rather than inventing a new
  navigation mechanism.
- Trigger points: after login, after session restore, and whenever `TermsService` detects new
  pending terms mid-session.
- Headless/background mode decision needed (see decisions): UI gating cannot apply to
  `--background` boots; recommended default is UI-gate only for v1, with collection-pause as a
  follow-up flag if legal requires it.

### 4) Locked fullscreen acceptance UI

- Preferred shape: a gated page in the existing shell (7th-page pattern from
  `client/UI_ARCHITECTURE.md` §7: `Views/Pages/TermsPage.axaml` + `ViewModels/TermsViewModel.cs`)
  plus shell-level enforcement while pending:
  - `MainWindow.Closing` cancelled while `RequiresTermsAcceptance` is true (non-closeable);
  - `Topmost = true`, `WindowState = Maximized`, `SystemDecorations` minimized/hidden nav buttons
    while gated (no minimize/close affordances);
  - no navigation to other pages while pending (rail disabled/bound away).
- Sequential step flow: one term at a time in `sort_order`, progress indicator, per-term
  **I agree** action, final "All terms accepted" → release the gate.
- Loading state while the server check runs; honest offline notice when serving from cache.
- HTML bodies: no HTML renderer is referenced in the project today. Default = strip to plain text
  for display (headings/paragraphs preserved structurally) with the raw HTML kept in SQLite;
  adding a third-party HTML label package is a separate decision (see decisions).

### 5) Platform / window-manager reality check

- Windows: topmost + close-suppression are fully supported by the WM.
- Linux/X11: `_NET_WM_STATE_ABOVE` honored by Mutter/KWin; close suppression via the Closing
  handler works, but a WM "force quit" can never be prevented — document, don't fight it.
- Linux/Wayland: the protocol has **no always-on-top**; topmost is best-effort. This is an
  environment limitation, not a bug — record it in the verification evidence.
- Cross-Platform Analyzer Safety Rule: any platform-specific window-flag code is guarded with
  `OperatingSystem.IsWindows/Linux/MacOS()` inside the method; no `[SupportedOSPlatform]`
  propagation.

### 6) Config knobs (Installer-Parity §5)

- New `ALPHA_TERMS_ENABLED` (default `true`) + check-interval knob, added to `.env`,
  `.env.example`, `AppConfig`, and `--print-config` — **before** `encrypt-config.sh` runs, so
  installed builds receive them via `config.enc`.

## File touchpoints (concrete)

- `client/Core/Models/ClientTerm.cs` (new), `client/Core/AppConfig.cs` (knobs)
- `client/Storage/DatabaseSchema.cs`, `client/Storage/SqliteLogStore.cs` (table + CRUD)
- `client/Services/TermsService.cs` (new), `client/Services/SyncService.cs` (only if consent
  drain is delegated there instead of inline POST)
- `client/ViewModels/MainViewModel.cs` (guard state + router), `client/ViewModels/TermsViewModel.cs` (new)
- `client/Views/Pages/TermsPage.axaml(.cs)` (new), `client/MainWindow.axaml(.cs)` (close guard/flags)
- `client/Program.cs` (DI registration)
- Server/web: no changes expected; any discovered contract gap is reported before code.

## Mandatory-rules checklist (per AGENTS.md / prompt.md)

- **Installer parity:** not done until the modal gate is verified from an installed build
  (`build-installer.sh` → install → boot → accept). `dotnet run` alone is not a release test.
- **Branding single source:** modal title/copy uses `AppInfo` accessors; no literal product strings.
- **Cross-platform analyzer safety:** platform guards inside methods; non-incremental build check
  after window-code changes.
- **No hardcoded names:** N/A (no software detection involved).
- **Installed paths:** all state in the existing SQLite store under the user data dir.
- **No unrelated refactoring:** client-only diff; server/web untouched unless a gap emerges.

## Verification plan (tiered, per prompt.md)

1. `dotnet build` — clean, 0 errors (non-incremental after window-flag changes; investigate
   duration >2× baseline or ~1 GB compiler memory).
2. Scenario matrix against a local server (seeded featured terms):
   first run (4 pending terms appear) → accept all sequentially (consent rows visible via
   `GET /terms-consent?employeeId=`) → reboot (no re-prompt) → admin bumps `terms_version`/edits
   body (only that feature re-prompts) → admin deactivates a term (pending row drops) →
   kill network after terms cached (offline boot uses cache) → already-accepted term never
   re-prompts.
3. Ship-test from the installed artifact (Installer-Parity): build installer, install, verify the
   gate appears on first login, window cannot be closed/minimized while pending, acceptance
   survives reboot. Wayland topmost caveat recorded as environment-limited if applicable.
4. Report explicitly which tier passed: source build / installer built / installed verified.

## Decisions to confirm at review (with recommended defaults)

1. **Headless enforcement** — UI gate only (recommended for v1) vs. also pausing app-session
   collection until consent. Legal posture question; default keeps collection running.
2. **HTML rendering** — plain-text strip (recommended, zero new dependencies) vs. adding an HTML
   label package. Note the seeds use `<h3>`/`<p>`; stripping loses inline formatting only.
3. **Gate shape** — gated page inside the existing shell (recommended; reuses router/guard
   patterns) vs. a separate always-on-top `Window`. The shell window still gets the Closing guard
   either way.
4. **Retry cadence for unacknowledged acceptances** — reuse the terms fetch interval (recommended)
   vs. pushing through `SyncService`.

## Execution order after approval

1. SQLite table + CRUD + content-hash diff logic.
2. `TermsService` (fetch/diff/accept, knobs, offline fallback).
3. Router guard + `TermsPage` UI + shell close/topmost enforcement.
4. Scenario matrix from a dev server; fix findings.
5. Installer build + installed-build verification per the parity rule.
6. Final report: evidence per verification tier, Wayland/WM caveats, remaining blockers.
