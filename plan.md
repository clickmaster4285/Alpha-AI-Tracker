# Timeline status and date-filter correction plan

## Scope and guardrail

This plan addresses the anomaly on `/employee-journey/timeline` for employee UUID
`76dcfd24-2241-45a3-a438-a1f86e24dcf2`.  The forward fixes and safe historical
status repair described below were implemented on 2026-09-10. The work retains
the server as the lifecycle authority and does not rewrite historical activity
timestamps.

## Confirmed causes

### 1. `preset=today` is not applied by the Timeline page

The supplied URL contains `preset=today`, but
`web/src/app/(app)/employee-journey/timeline/page.tsx` does not read the shared
`EmployeePage` filter and calls `appSessionsApi.list` with only `employeeId`,
`page`, and `perPage`.  Therefore the request has no `dateFrom` or `dateTo` and
the server correctly returns the employee's full history, including September 9.

This explains why September 9 data is visible on a page whose URL says Today;
it does not mean that September 9 rows were newly created on September 10.

### 2. A new closed session can be inserted as `ACTIVE`

`server/internal/repository/new_schema_repo.go` inserts `ended_at` but omits
`status` from its `INSERT INTO app_sessions` column list.  Migration 031 gives
that omitted column a default of `ACTIVE`.  The `ON CONFLICT` path correctly
changes an existing ACTIVE/OFFLINE/STALE session to CLOSED when a client sends
`ended_at`, but that logic does not run for a first-time insert.

Consequently, when a short session starts and ends before its first successful
sync (or an unsent local row first reaches the server only after close), the
persisted row can be:

| `ended_at` | `status` | Timeline rendering |
| --- | --- | --- |
| non-NULL | `ACTIVE` | Running + a value in Closed |

The UI is exposing a contradictory server record; this is not a browser-format
or timezone issue.

## Implementation plan

1. Establish a small reproducible fixture/query before changing behavior.
   - Call `GET /api/v1/app-sessions` for the affected employee with explicit
     September 9 and September 10 bounds, and capture `id`, `machineId`,
     `startedAt`, `endedAt`, `status`, `lastActivityAt`, and `lastSyncAt`.
   - In the database, inspect the same columns plus `synced_at`.  Categorize
     rows as (a) closed-before-first-sync, (b) open rows that stopped syncing,
     and (c) normal closed rows.  This verifies the diagnosis without changing
     production data.

2. Wire the Timeline to the existing URL activity filter.
   - Change the `EmployeePage` render callback in the timeline to receive
     `filter`.
   - Derive the same API-safe `dateFrom`/`dateTo` values used by the other
     Employee Journey pages and include them in both the React Query key and
     `appSessionsApi.list` parameters.
   - Keep `employeeId` and paging behavior intact.  Reset/re-key the infinite
     query when the employee or date range changes so pages from an earlier
     filter cannot remain in the table.
   - Verify `preset=today` shows only local-calendar-today sessions, `all`
     shows history, and custom ranges include their selected boundaries.

3. Make server lifecycle values internally consistent on first insert.
   - In `BulkInsertAppSessions`, explicitly derive the inserted status from
     the incoming `ended_at`: `CLOSED` when it is non-NULL; otherwise `ACTIVE`.
   - Include that derived status in the INSERT values.  Retain the existing
     conflict/update behavior for retried and reconnecting rows.
   - Do not make the UI silently override `ACTIVE` with `CLOSED`; that would
     hide server-data corruption and leave API consumers contradictory.

4. Decide and implement the historical-data policy separately and visibly.
   - Run a one-time, reviewable SQL report for invalid combinations such as
     `status = 'ACTIVE' AND ended_at IS NOT NULL` and
     `status IN ('OFFLINE','STALE') AND ended_at IS NOT NULL`.
   - If the report confirms legacy contradictions, add an idempotent migration
     that changes only rows with non-NULL `ended_at` to `CLOSED`.  It must not
     invent or alter `ended_at` values.
   - Keep this migration separate from the forward-path fix so it can be
     reviewed, counted, and deployed safely.

5. Re-evaluate the per-machine sweep contract using the captured rows.
   - The current server uses four states (`ACTIVE -> OFFLINE -> STALE ->
     CLOSED`), although migration comments and some older documentation still
     describe three states.  Reconcile those documents and all user-visible
     labels in the same change.
   - Confirm whether a fresh September 10 sync from a machine should revive
     only the row that the client re-uploads (current behavior) or all still
     open rows for that machine.  This is a product/lifecycle decision; do not
     broaden the update to all rows without approving that policy.

## Tests and acceptance criteria

1. Add a repository/service test for a first-ever sync entry with `endedAt`.
   It must persist and return `status=CLOSED` with the original `endedAt`.
2. Preserve tests for the existing paths:
   - open first sync -> `ACTIVE`;
   - later closed re-sync -> `CLOSED`;
   - OFFLINE/STALE row re-uploaded open -> `ACTIVE`;
   - terminal CLOSED row re-uploaded open remains consistent with the approved
     lifecycle policy.
3. Add a web test (or focused component/query test) showing that `preset=today`
   includes `dateFrom` and `dateTo` in the API request and that changing the
   preset creates a new query key.
4. Manually verify the supplied URL after deployment:
   - it contains no September 9 sessions under `preset=today` on September 10;
   - no returned row displays Running when its Closed column has a timestamp;
   - a deliberately short, closed-before-first-sync session displays Closed.
5. Run the normal gates: `go test ./...`, `go vet ./...`, `dotnet build` in
   `client`, `npx tsc --noEmit`, and `npm run build` in `web`.

## Deployment order

1. Deploy the server fix and any approved historical-data migration first.
2. Deploy the web filter fix.
3. Ship the client only if testing identifies a separate local requeue/close
   defect; neither confirmed cause currently requires a client change.
