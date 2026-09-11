# Release Notes — 2026-09-11

## App-Session Accuracy, Windows Shutdown Hardening + Apps-Page UX

**What changed**

- **Server** — the App Usage aggregate no longer mislabels OFFLINE/STALE sessions as "Running":
  - `has_open_session = BOOL_OR(status='ACTIVE' AND ended_at IS NULL)` (a session that was never given an `ended_at` by a dead client is not "Running").
  - New `last_active_at` projection powers the **Last Active** column.
  - A per-row stagnation sweep closes any OFFLINE/STALE row sitting untouched for the close window, even on a machine that is still reporting other sessions.
- **Migrations** — `034` (historical `ended_at`-bearing rows → CLOSED) and `035` (freeze stranded OFFLINE/STALE rows older than 24h). **035 is already applied to the live DB**; verification `SELECT COUNT(*) FROM app_sessions WHERE status='OFFLINE' AND ended_at IS NULL` returns **0**.
- **Web `/employee-journey/apps`** — columns now **Application | Sessions | Duration | Last Active | Status**; Status shows green only for truly running sessions; Edge webviews are folded into one "Microsoft Edge" row (`msedgewebview2` → `msedge`).
- **Web `/employee-journey/timeline`** — Closed column shows the last sync for OFFLINE/STALE sessions instead of an em-dash.
- **Web `/employee-journey/web`** — an open tab now contributes `now − openedAt` to its group's duration instead of `0`.
- **Client (Windows)** — on shutdown/restart the tracker stamps `ended_at` on every open session (powered by `CloseSessionsAndAppItemsAsync`) so the next sync close is pushed to the server. Ships in the **next installer build**.

**Verification**

`go build`/`go vet` clean · `npx tsc --noEmit` clean · `next build` passes · `dotnet build` 0 warnings / 0 errors.

---

## For Ops — Monitoring & Follow-ups (Phase 6)

- **Alert (recommended):** fire when stranded open sessions appear:
  ```sql
  SELECT COUNT(*) FROM app_sessions WHERE status='OFFLINE' AND ended_at IS NULL;
  ```
  Deploy the alert in Grafana (or your SQL alerting) at > 0 with a 5-min refresh.
- **Nightly/weekly safety sweep** as a cron fallback (48h window, runs harmlessly alongside the built-in per-row sweep):
  ```sql
  UPDATE app_sessions
     SET status='CLOSED', ended_at=COALESCE(last_activity_at, last_sync_at, started_at)
   WHERE ended_at IS NULL
     AND status IN ('OFFLINE','STALE')
     AND last_sync_at < NOW() - interval '48 hours';
  ```
- **Deploy sequence:** server first (applies 034/035 on startup), then the client ships in the next installer build (the Windows shutdown path is inert until then).
- **Not yet done infra:** Grafana dashboard/alert provisioning is out-of-repo; the alerts above are the runbook target. A Windows power-off smoke test (real shutdown) is the remaining client verification.