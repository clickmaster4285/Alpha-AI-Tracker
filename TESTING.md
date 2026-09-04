# Live testing — `/app-sessions/usage/sessions` (per-app expand endpoint)

This is the test plan for the new endpoint added on 2026-09-04. The endpoint
serves the chevron-expanded session list under each app row on
`/employee-journey/apps`. It is **paginated server-side** so a heavy user
(200+ sessions/week) doesn't ship every row up front.

## 0. Prerequisites

PostgreSQL + Redis + a freshly built server binary. AGENTS.md §8 has the
canonical dev setup; on a machine with docker, this is one command:

```bash
docker run -d --name pg -e POSTGRES_USER=alpha_ai -e POSTGRES_PASSWORD=yourpassword -e POSTGRES_DB=alpha_ai_tracker -p 5432:5432 postgres:16
docker run -d --name redis -p 6379:6379 redis:7
```

then:

```bash
cd server
make run   # picks up .env, runs migrations 001-032 on boot
```

Confirm the new endpoint is registered:

```bash
curl -s http://localhost:8080/health
# {"status":"ok"}
```

(That endpoint doesn't require auth. The protected ones need a cookie.)

## 1. Login as the seeded admin

```bash
curl -s -c /tmp/cookies.txt -X POST http://localhost:8080/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@alphai.com","password":"AlphaAI@2024!"}' -i
```

You should get `Set-Cookie: auth_token=...` and a 200. The cookie file
(`/tmp/cookies.txt`) is what the next calls use.

## 2. Confirm the new route is registered

```bash
curl -s -b /tmp/cookies.txt -o /dev/null -w "%{http_code}\n" \
  "http://localhost:8080/api/v1/app-sessions/usage/sessions?appDisplayName=x&processName=x"
```

Expected: **400** with `{"code":400,"message":"appDisplayName and processName are required ..."}` — the handler's "both empty → 400" guard fires when the route is hit but with no employees matchable. If you get **404**, the new route isn't registered and the server wasn't rebuilt — `go build` and restart.

## 3. Pick an employee that has app_sessions rows

```bash
curl -s -b /tmp/cookies.txt "http://localhost:8080/api/v1/employees?perPage=5" | python -c "import json,sys; d=json.load(sys.stdin); print(d['data'][0]['employeeId'])"
```

This is your `$EMP`. If the first employee has no `app_sessions` rows yet,
try the next one (`.data[1]`, `.data[2]`, …).

## 4. Find an app in the per-app aggregate

```bash
curl -s -b /tmp/cookies.txt "http://localhost:8080/api/v1/app-sessions/usage?employeeId=$EMP&perPage=10" | python -m json.tool
```

Expected: an array under `.data`, each row has `appDisplayName`, `processName`, `sessionCount`, `firstOpenedAt`, `lastClosedAt`, `totalDurationSeconds`. Pick one with `sessionCount > 0` — that's your `$APP_NAME` and `$PROCESS_NAME`.

If `.data` is empty, no clients have synced for this employee yet — start
a client, open chrome for 10 minutes, and re-run.

## 5. The actual test — fetch the per-app session list

```bash
curl -s -b /tmp/cookies.txt \
  "http://localhost:8080/api/v1/app-sessions/usage/sessions?appDisplayName=$APP_NAME&processName=$PROCESS_NAME&employeeId=$EMP&page=1&perPage=20" \
  | python -m json.tool
```

Expected shape (200 OK):

```json
{
  "data": [
    {
      "id": "...",
      "appDisplayName": "Google Chrome",
      "processName": "chrome",
      "startedAt": "2026-09-04T09:00:00Z",
      "endedAt": "2026-09-04T09:10:00Z",
      "contextLabel": "Wikipedia — Google Chrome",
      "foregroundSeconds": 540.0,
      "backgroundSeconds": 60.0,
      "status": "CLOSED",
      "lastActivityAt": "...",
      "lastSyncAt": "..."
    }
  ],
  "total": 3,
  "page": 1,
  "perPage": 20,
  "totalPages": 1
}
```

Verify the count matches the per-app aggregate's `sessionCount` (the
group key on the aggregate is the same `(appDisplayName, processName)`
pair, so the totals must agree).

## 6. Pagination test

If the app has more than 20 sessions in your date range, `perPage=20` will
return the first 20 with `totalPages > 1`. Walk the pages:

```bash
for p in 1 2 3; do
  curl -s -b /tmp/cookies.txt \
    "http://localhost:8080/api/v1/app-sessions/usage/sessions?appDisplayName=$APP_NAME&processName=$PROCESS_NAME&employeeId=$EMP&page=$p&perPage=20" \
    | python -c "import json,sys; d=json.load(sys.stdin); print(f'page={d[\"page\"]} count={len(d[\"data\"])} total={d[\"total\"]} totalPages={d[\"totalPages\"]}')"
done
```

Expected: each page is at most 20 rows, `total` is the same across all
pages, `page` increments, `totalPages` is the same.

## 7. Date filter test

```bash
curl -s -b /tmp/cookies.txt \
  "http://localhost:8080/api/v1/app-sessions/usage/sessions?appDisplayName=$APP_NAME&processName=$PROCESS_NAME&employeeId=$EMP&dateFrom=2026-09-01&dateTo=2026-09-04&page=1&perPage=20" \
  | python -c "import json,sys; d=json.load(sys.stdin); print(f'total={d[\"total\"]}')"
```

Expected: `total` ≤ the unfiltered count, and every `startedAt` in `.data`
is between the two bounds.

## 8. Bad-request test

```bash
curl -s -b /tmp/cookies.txt -w "\n%{http_code}\n" \
  "http://localhost:8080/api/v1/app-sessions/usage/sessions?employeeId=$EMP"
```

Expected: `400` with the message `"appDisplayName and processName are required ..."`.

## 9. Web page render test

Start the web dev server (or use the production build already verified):

```bash
cd web
npm run dev
```

Navigate to:
- `http://localhost:3000/dashboard` → login → `/employee-journey/apps`
- pick an employee from the `EmployeeSelector` (or use the URL `?employeeId=…`)
- click the **chevron** on any app row that has `Sessions > 0`

Expected:
- a spinner appears briefly under the row
- an inline table expands below the row with one row per session
- columns: **Opened, Closed, Duration, Process, Title, Foreground, Background**
- a "Load more sessions" button at the bottom (if total > 20) or a "Showing all N" footer

Repeat the date filter — the inner table refetches from page 1 with the
new bounds.

## 10. Expected outcome

If steps 1-9 all pass, the implementation is verified end-to-end. Report
the curl outputs you got, any deviation from expected, and the page
screenshot for the expanded chrome row.

If anything fails, copy the exact request + response + Postgres log
output and we'll root-cause from there.

---

# Live testing — orphan `app-items` FK 23503 fix (2026-09-04)

## The bug

Client sends `app-sessions` and `app-items` in separate HTTP calls. When
items arrive before their parent session (concurrent client retries,
network blip, second client process), Postgres returns
`SQLSTATE 23503` and the whole batch 500s, silently dropping every row.

## The fix

`new_schema_repo.BulkInsertAppItems` now preflights each 500-row batch:

1. `SELECT id FROM app_sessions WHERE id = ANY($1)` over the distinct
   `app_session_id`s in the batch (one indexed round-trip, O(1) per id).
2. Drop rows whose `app_session_id` is empty or missing.
3. Insert only the survivors.
4. Log a single WARN line per batch with the count + first 20 orphan ids.
5. Return 200 with `accepted: <survivor count>`. The dropped rows stay
   `is_synced=0` in the client's local SQLite and are re-sent on the
   next sync — by then the parent session has landed.

No schema change. No client change. No migration.

## Test plan

### Step A — repro the old 500

1. Login (step 1 above).
2. Pick an employee `$EMP` (step 3 above).
3. Insert a bogus `app_items` row referencing a non-existent session:

```bash
curl -s -b /tmp/cookies.txt -X POST http://localhost:8080/api/v1/app-items/sync \
  -H "Content-Type: application/json" \
  -d '{
    "employeeId": "'$EMP'",
    "token": "<the JWT, not the cookie — see SyncAppItemsRequest>",
    "entries": [{
      "id": "test-orphan-1",
      "appSessionId": "00000000-0000-0000-0000-000000000000",
      "itemType": "browser_tab",
      "title": "Orphan test",
      "identifier": "test",
      "url": "",
      "domain": "",
      "openedAt": "2026-09-04T15:00:00Z",
      "objectType": "Tab",
      "action": "open",
      "journeyId": "test-orphan-1",
      "sequence": 1,
      "metadataJson": "{}"
    }]
  }' -i
```

Expected with the fix: **HTTP 200** with body
`{"synced":0,"message":"Synced 0 of 1 entries"}` (or similar — depends on
the `SyncBatchResponse` shape, but the request must NOT 500).

### Step B — confirm the WARN log fires

Tail the server log:

```bash
tail -f /path/to/server.log
```

Re-run the Step A curl. Expected log line:

```
[new_schema] WARN: app-items batch contained 1 orphan app_session_id(s) for employee=EMP-XXXXX (dropped from this batch, will re-sync next pass). ids=[<empty>]  (or ids=[00000000-0000-0000-0000-000000000000])
```

If the `ids` slice shows the bogus session id, the preflight is working.
If it shows `<empty>`, the client sent an empty `appSessionId` instead —
different bug, but the same fix path.

### Step C — confirm normal inserts still work

Same curl as Step A but with a **real** session id (use the `id` from
the aggregate response in step 4 above, or a session from
`GET /api/v1/app-sessions?employeeId=$EMP`). Expected: HTTP 200, the row
is in `app_items`, no WARN log.

### Step D — confirm the client re-syncs the dropped rows

Trigger a normal client sync (open/close a tab, or wait for the
60s push cadence). The previously-dropped `test-orphan-1` row stays
`is_synced=0` in the client's local SQLite UNTIL the next sync — and
then ONLY re-sends if the parent session id is now in the DB. For
`00000000-...` (which we never created), the row will be re-dropped
forever (the WARN log will re-fire on every sync). That's the right
behavior — the row is genuinely orphaned, not transiently so.

If the orphan id is a real-but-not-yet-synced session (the common
race case), the next sync's `app-sessions` call lands the parent,
then the items re-sync succeeds, and the WARN log stops firing.

## Expected outcome

- Step A: 200, not 500
- Step B: WARN log with the orphan id
- Step C: 200, row in DB
- Step D: orphan re-dropped for bogus ids, recovered for real-but-late ids

Report any deviation (404 on the route, 500 still, WARN missing,
unexpected log line) with the full log output and request/response
so we can root-cause from there.
