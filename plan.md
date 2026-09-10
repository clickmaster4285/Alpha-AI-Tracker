# Bug Fix: App Items Orphan Deadlock — items permanently stuck at `is_synced=0`

## Root Cause

The `app-items` sync is stuck in a **permanent orphan rejection loop**. Here is the exact
deadlock chain:

1. **Client** has `app_items` rows referencing `app_session_id` values (e.g.
   `bebc383d98704486bef3e6f8d818e601`) whose parent sessions were synced to the server in a
   **previous** pass/server-run and marked `is_synced=1` on the client.

2. **Server** wiped the DB (or sessions were lost). Those session IDs no longer exist in
   PostgreSQL. The current DB has 141 sessions for EMP-10002 — but the orphaned session IDs
   (`bebc383d…`, `cd6259b2…`, etc.) are NOT among them.

3. **Each sync pass**:
   - `app-sessions/sync` runs first → sends whatever sessions are `is_synced=0` → 200 OK.
     The orphaned sessions are NOT in this batch (already `is_synced=1`).
   - `app-items/sync` runs next → `filterOrphanAppItems()` queries
     `SELECT id FROM app_sessions WHERE id = ANY($1)` → session IDs not found → items
     rejected → `rejectedIds` returned → client keeps those items `is_synced=0`.

4. **Next pass**: same cycle. Sessions never re-sent (already `is_synced=1`); items never
   accepted (parent sessions don't exist). **Permanent deadlock.**

### Evidence

| Source | Finding |
|--------|---------|
| PostgreSQL `app_sessions` | 141 rows, none match orphan IDs. Sessions from Sep 9–10 have 0 items each. |
| PostgreSQL `app_items` | 1393 rows, latest `browser_tab` is Sep 8. Zero items for Sep 9–10 sessions. |
| Server logs | `app-sessions/sync` → 200, then 4× `app-items/sync` all report same orphan IDs in a loop. |
| Client `SyncService` | `DrainTableAsync<AppSession>` drains before `<AppItem>`, but orphaned sessions are already `is_synced=1`. |

## Fix Plan

Two coordinated changes — server tells the client which sessions are missing, client re-queues
them.

### Change 1: Server — return `missingSessionIds` in `SyncBatchResponse`

**Files:**
- `server/internal/dto/new_schema_dto.go`
- `server/internal/repository/new_schema_repo.go` (`filterOrphanAppItems`)
- `server/internal/services/new_schema_service.go` (`SyncAppItems`)

**What:**
1. Add `MissingSessionIds []string` field to `SyncBatchResponse` (JSON:
   `"missingSessionIds,omitempty"`). This tells the client exactly which parent sessions it
   must re-send.

2. Change `filterOrphanAppItems` to also return the distinct missing session IDs (it already
   computes `orphanIDs` — these ARE the missing session IDs; just return them separately from
   the rejected item IDs).

3. In `SyncAppItems` service, pass the missing session IDs into the response.

**Wire format** (unchanged for non-orphan responses — `omitempty` keeps backward compat):
```json
{
  "synced": 120,
  "message": "Synced 120 of 200 entries",
  "rejectedIds": ["item-id-1", "item-id-2"],
  "missingSessionIds": ["session-id-1", "session-id-2"]
}
```

### Change 2: Client — re-queue sessions reported as missing

**Files:**
- `client/Services/SyncService.cs` (`SyncBatchResponse` class, `DrainTableAsync`, `OnAppItemsRejected`)
- `client/Storage/SqliteLogStore.cs` (new method `MarkAppSessionsUnsyncedByIdsAsync`)
- `client/Core/Abstractions/ILogStore.cs` (interface for the new method)

**What:**
1. Add `MissingSessionIds` property to `SyncBatchResponse` class.

2. In `DrainTableAsync` (the app-items path), after parsing `rejectedIds`, also parse
   `missingSessionIds`. Pass them to a new callback `onMissingSessions`.

3. In `BuildDrainPass()`, wire `onMissingSessions` for the app-items drain to call
   `_store.MarkAppSessionsUnsyncedByIdsAsync(missingSessionIds)`.

4. New store method `MarkAppSessionsUnsyncedByIdsAsync(IReadOnlyList<string> ids)`:
   ```sql
   UPDATE app_sessions SET is_synced = 0 WHERE id IN (...) AND is_synced = 1
   ```
   This resets only the specific sessions the server said are missing. Sessions that
   genuinely exist on the server are not touched (the server didn't report them as missing).

**Effect:** The next sync pass will re-send those sessions → they land in PostgreSQL → items
find their parent → accepted. The deadlock is broken in one additional sync cycle.

### Change 3 (hardening): Client — don't permanently quarantine rows that have a fixable cause

The existing quarantine mechanism (3 consecutive refusals → skip until restart) is too
aggressive for the orphan case. The orphan refusal is **not** a data quality problem — it's a
timing/consistency problem that the missing-session fix resolves.

**File:** `client/Services/SyncService.cs`

**What:** In `OnAppItemsRejected`, only increment the reject counter when the server returned
`missingSessionIds` that include this item's `appSessionId` AND the client successfully
re-queued the session. If the session re-queue succeeded, the item's refusal is expected and
transient — don't count it toward quarantine. This prevents items from being quarantined
while the fix is still in flight.

Alternatively (simpler): just reset the reject counter for any item whose `appSessionId`
appears in the `missingSessionIds` response, since the server acknowledged the problem and
the client is fixing it.

## Verification

1. **Server:** `go build && go vet` — zero errors.
2. **Client:** `dotnet build` — zero errors, zero warnings.
3. **Manual test:**
   - Start server, confirm a client sync produces orphan warnings.
   - Apply fix, restart server + client.
   - Next sync pass: server returns `missingSessionIds` → client re-queues sessions →
     second pass sends sessions → items accepted.
   - Confirm `app_items` for the affected employee now have rows with today's date.
   - Confirm `/employee-journey/web?preset=yesterday&employeeId=…` shows data.
4. **Regression:** Other sync endpoints (`app-sessions/sync`, other tables) unaffected —
   `missingSessionIds` is `omitempty` and only populated by `SyncAppItems`.

## Files Changed (summary)

| Service | File | Change |
|---------|------|--------|
| Server | `dto/new_schema_dto.go` | Add `MissingSessionIds` to `SyncBatchResponse` |
| Server | `repo/new_schema_repo.go` | Return missing session IDs from `filterOrphanAppItems` / `BulkInsertAppItems` |
| Server | `service/new_schema_service.go` | Thread missing session IDs into response |
| Client | `SyncService.cs` | Parse `missingSessionIds`, call `onMissingSessions` callback |
| Client | `ILogStore.cs` | Add `MarkAppSessionsUnsyncedByIdsAsync` interface |
| Client | `SqliteLogStore.cs` | Implement `MarkAppSessionsUnsyncedByIdsAsync` |
