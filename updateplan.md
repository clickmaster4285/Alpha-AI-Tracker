# Alpha AI Tracker — Updated Security, Reliability, and Scalability Plan

> Updated: 2026-08-19
>
> Scope: planning and review only. This document makes no product-code changes.

## Executive priorities

1. **P0 — Linux updates must restart the headless tracker.** A successful update must not leave data collection and sync stopped until a person opens the GUI.
2. **P0 — Bind sync authorization to the employee identity.** A valid credential must never be usable to submit activity for another employee.
3. **P1 — Replace the expiring employee JWT with a secure device credential that supports silent renewal and revocation.**
4. **P1 — Establish server-side data retention, query scalability, and operational observability.**

## Goal 0 — Linux update preserves continuous tracking

### Observed behavior

On Linux, selecting a detected update closes the tracker and installs the newest package, but the collector/sync jobs do not resume. They begin only after the user manually opens the tracker.

This violates headless-at-boot tracking: updating must be a brief process restart, not a tracker shutdown.

### Working diagnosis to verify before implementation

The Linux update path installs with `pkexec dpkg -i`. The likely break is that the post-install flow exits or loses the running client without launching a detached replacement in `--background` mode. Linux autostart normally runs on the next desktop login, not immediately after a package upgrade, so it cannot restart an already-running session.

Validate this in `AppUpdateService.InstallAsync`, `Program.cs`, Linux packaging scripts, and systemd/autostart setup before changing behavior. Capture updater, installer, and replacement-process exit codes so the failed handoff is explicit.

### Required design

- Treat install and restart as one durable handoff owned by a detached Linux updater helper, not the process being replaced.
- The helper waits for the old process to exit, runs `pkexec dpkg -i`, verifies success, and starts the installed executable with `--background` in the same user session.
- Use an explicit restart handoff marker/token so the single-instance mechanism cannot silently discard the intended replacement.
- Preserve the SQLite queue and persisted employee/device credential; the replacement must use normal headless startup, restore identity, start `LogCollectorService`, and start `SyncService` without opening the GUI.
- If install or replacement launch fails, restart the prior executable when possible and persist an actionable error for the next GUI open.
- Make the helper bounded and self-cleaning. Do not rely on shell profiles, a terminal, or a future desktop login.

### Acceptance tests

1. Begin with an installed Linux `.deb`, log in once, and confirm collector and sync jobs are active.
2. Trigger GUI update; do not manually reopen the GUI.
3. Assert old PID exits, `dpkg` succeeds, a new `--background` process appears, and only one tracker instance exists.
4. Confirm a new app-session/app-item reaches the server within the normal sync SLO.
5. Repeat with denied polkit, failed `dpkg`, and replacement-launch failure; confirm the prior tracker resumes or failure is explicit.
6. Repeat with offline backlog; verify unsynced SQLite rows survive and drain after restart.
7. Verify from a clean installed package, never only with `dotnet run`.

### Deliverables

- Linux updater handoff implementation and structured lifecycle logs.
- Installer smoke-test checklist/script.
- Regression tests for updater command construction and headless startup arguments.
- Documentation of the Linux update state machine and recovery paths.

## Goal 1 — Fix sync authorization before credential longevity

### Verified issue

Sync handlers validate that a JWT is valid but do not prove that its user claim belongs to the `employeeId` in the JSON request. Correct this before introducing long-lived device credentials.

### Required design

- Add one `DeviceAuth` middleware to all desktop sync routes.
- Send an opaque device credential in `Authorization: Device <credential>`, never JSON.
- Resolve employee and device once in middleware and attach them to request context.
- Remove `token` and preferably request-level `employeeId` from sync DTOs; establish ownership server-side and reject contradictory entry data.
- Keep web-admin JWT authentication separate from device authentication.
- Add audit events and rate limits for employee-login and credential failures.

### Tests

- A valid credential succeeds only for its own employee.
- A credential for employee A cannot submit activity as employee B.
- Revoked, expired, malformed, and unknown credentials fail without retry storms.
- Every sync endpoint applies the same middleware.

## Goal 2 — One-time employee login with revocable devices

Replace the expiring employee sync JWT with a random 256-bit opaque device credential. Keep one human login, but use long-lived credentials with silent rotation/renewal rather than an irrevocable permanent secret.

Create `employee_devices` with:

- `id` UUID primary key and `employee_id` UUID foreign key
- `machine_id`, platform, client version, optional device name
- keyed credential fingerprint/hash, credential version, `created_at`, `last_seen_at`, `expires_at`, and `revoked_at`
- documented uniqueness policy for active `(employee_id, machine_id)` records

Store only a keyed server-side hash/fingerprint, never raw credentials. Keep the pepper outside the database. Use OS credential storage where available (Windows Credential Manager, macOS Keychain, Linux Secret Service). Do not reuse plaintext SQLite `employee_info.token`; add a clearly named credential migration.

### Delivery sequence

1. Device migration and repository/service support.
2. Device middleware and login enrollment response.
3. Secure desktop persistence and migration from existing JWTs.
4. Silent rotation/re-enrollment and revoked-device behavior.
5. Admin device list/revoke/rotate API and web UI.
6. End-to-end revocation and recovery tests.

## Goal 3 — Immediate database scalability

### Employee IDs

The employee sequence currently cycles at 99,999. Replace it with a non-cycling `BIGINT` sequence. Retain five-digit padding only as a minimum display width; `VARCHAR(20)` supports a much higher ceiling.

### Server retention

Define policy before implementation for raw app items/URLs, sessions, inventory history, audit events, aggregates, and legal-hold records.

Build a server retention worker that:

- deletes only closed eligible session trees and child items;
- operates in small observable batches;
- records deleted rows, duration, lock waits, and errors;
- respects legal hold; and
- is rehearsed on a production-sized database copy.

Client-side 24-hour cleanup already exists; it does not prevent server storage growth.

### Query path

The UI uses infinite scroll, but server lists use `COUNT(*)` plus `LIMIT/OFFSET`. At scale:

- add partial composite indexes for real query patterns, such as employee plus activity timestamp where `deleted_at IS NULL`;
- verify with `EXPLAIN ANALYZE` on realistic data;
- adopt cursor/keyset pagination using `(started_at, id)` or `(opened_at, id)` and `hasMore`;
- add trigram indexes for broad substring search only when benchmarks justify their cost.

## Goal 4 — Scale architecture when measured demand justifies it

### Activity lifecycle

- Partition high-volume activity tables monthly once raw activity reaches millions of rows or retention deletes become material.
- Retain a hot PostgreSQL window, archive historical raw data only when forensic retrieval is required, and maintain daily aggregates for dashboards.
- Prefer partition expiration/drop to bulk deletes after partitioning.
- Plan this migration separately because the current migration runner wraps each migration in one transaction, while low-lock production operations may require non-transactional execution.

### Ingestion reliability

- Preserve client-generated idempotency IDs and bounded batches.
- Add metrics for ingestion latency, accepted/rejected rows, client backlog age, sync/auth failures, retention lag, DB pool saturation, and dashboard query latency.
- Establish SLOs, e.g. 99% of online-client activity visible within two minutes and no unsent client data older than one hour while online.
- Add a durable queue only after measured PostgreSQL limits require it; do not introduce a broker prematurely.

### Multi-organization readiness

Only if the product becomes multi-company SaaS:

- add `organization_id` to all employee-owned data and authorization checks;
- enforce tenant isolation in repositories and, where appropriate, PostgreSQL row-level security;
- separate analytics/reporting from ingestion; and
- add replicas or an analytics store only after observed read/write contention.

## Delivery order

1. Linux update handoff investigation, fix, and installed-build regression test.
2. Centralized sync authorization and integration-test baseline.
3. Device credentials, secure storage, revocation, and device management.
4. Sequence repair, server retention, indexes, and cursor pagination.
5. Partitions, aggregates, archival, and scale-out decisions based on metrics.

## Definition of done

- Unit and integration tests cover success, failure, and restart/recovery paths.
- Server builds and migrations pass against a representative database copy.
- Client builds and the installed Linux package passes end-to-end testing.
- Web typecheck/build succeeds for web changes.
- Metrics, logs, rollback/recovery notes, and operator documentation are updated.
