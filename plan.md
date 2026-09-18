# Live Stream — Implementation Plan

> **Status:** PLANNED (not started) · **Branch:** `feature/live_stream` · **Created:** 2026-09-18
> **Driver requirement:** Admin selects an employee → sees that employee's screen live.
> Preview only. **No recording, no persistence, no disk, no DB storage — frames are ephemeral.**
> The employee has already accepted the T&C for this feature (`featureId = "live_stream"`), and the
> server re-verifies consent server-side before any frame flows.

---

## 0. Summary

| # | Requirement                                                                                | Where it lands                                                                                                                                                                                                        |
| - | ------------------------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1 | Left sidebar: all employees with **online \| offline** status via a client health API | Server:`GET /api/v1/live-stream/employees` (health derived from `last_heartbeat_at` in `app_status`, stale after 60 s — same threshold the client's own crash-recovery uses). Web: `/live-stream` left rail. |
| 2 | Click employee → their screen appears on the right                                        | Web right pane: live`<canvas>` / `<img>` panel fed by a WebSocket from the server relay.                                                                                                                          |
| 3 | **Live preview only — nothing recorded or saved**                                   | Server keeps at most the latest frame**in memory**; no disk writes, no DB table. Client pushes JPEG frames to the relay; only while an admin is watching. Stream stops when the last watcher leaves.            |

**Transport decision (final):**

| Protocol | Usage | Why |
|----------|-------|-----|
| **HTTP** | All existing sync (app_sessions, app_items, etc.) | Already works, battle-tested, no changes |
| **WebSocket** | Live stream — client push + admin watch | New, only for live streaming |
| **WebRTC** | Not used | Overkill for this use case |

**Capture scope (user-selected): Windows-first.** Windows ships capture now (DXGI
Desktop Duplication API); Linux/macOS report "stream unavailable" honestly in the sidebar and in the
watch panel. PipeWire/portal capture is a follow-up phase.

---

## 1. Project analysis (why the design looks like this)

Findings from the repo that constrain the design:

1. **No real-time infra exists.** ARCHITECTURE.md explicitly lists "no WebSocket, SSE, or polling
   endpoints" as a current gap. This feature introduces the first one — it is greenfield on the
   server, and the router/`main.go` wiring must be done cleanly.
2. **The client is outbound-HTTP-only today.** Every client→server call is a REST sync under
   `DeviceAuth`. There is no inbound connection to the client. So the client must be the one that
   opens and owns the outbound WebSocket to the server; the server relays to web watchers.
3. **Online/offline is already observable server-side.** The client writes `last_heartbeat_at`
   into `app_status` every collection cycle and syncs it; `LogCollectorService` uses a 60 s
   staleness threshold for its own crash recovery. `GET /live-stream/employees` reuses that exact
   signal — no new client endpoint is needed for health (requirement 1).
4. **Auth separation is a mandatory rule** (AGENTS.md §6, Client-vs-Web API Auth Separation Rule):
   - client push socket → `DeviceAuth` semantics (the `syncGroup`),
   - web view socket → `JWTAuth` semantics (the `protected` group, httpOnly-cookie web session).
     This exact mis-wiring shipped once before (T&C endpoint under the wrong group 401'd clients).
5. **Consent gate exists** — `terms_consent` (`featureId`, `action='accepted'`) with
   `HasAccepted(employeeID, featureID)` already in `TermsConsentRepo`. The stream is gated on
   `featureId = "live_stream"` server-side; the web UI shows a clear "consent missing" state when
   it isn't granted.
6. **Web proxy**: `web/src/lib/api.ts` uses relative `/api/v1` via Next rewrites
   (`/api/:path*` → `http://localhost:8080/api/:path*`). **Next.js rewrites do not proxy
   WebSockets reliably in all setups**, so the WS client in the browser must talk to the Go server
   origin directly (`NEXT_PUBLIC_WS_URL`, default `ws://localhost:8080`) — documented as a new
   env var, with cookie credentials attached.
7. **Client conventions**: no `IHttpClientFactory`; services are singletons + hosted services;
   platform code guarded by `OperatingSystem.IsWindows()` inside the method body (Cross-Platform
   Analyzer Safety Rule); no hardcoded product names anywhere.
8. **Installer parity** (mandatory): new capture code compiles into `client.dll`; the new env knobs
   (`ALPHA_STREAM_*`) must be added to `.env` **before** `encrypt-config.sh` re-bakes `config.enc`,
   and the feature must be ship-tested from an installed build before "done".

---

## 2. Architecture

```
┌─────────────┐  WS push (JPEG, ~10 fps)   ┌──────────────────────┐  WS (JPEG stream)   ┌─────────┐
│ Client (Win)│ ──────────────────────────▶│ Server relay         │ ───────────────────▶│ Web     │
│ ScreenCapture│  ws://server/api/v1/live-stream/push   │ (in-memory latest-  │ ws://server/api/v1/live-stream/watch │ (canvas)│
└─────────────┘                             │  frame per employee, │                     └─────────┘
                                            │  fan-out to N admins)│
                                            └──────────────────────┘
```

- **One outbound socket per logged-in client**, opened only while streaming is active.
- **Server mailbox**: `map[employeeID] → { frame []byte, seq, ts }` guarded by a RWMutex.
  `POST frame` overwrites; watchers read the mailbox at their own pace. **Nothing is ever written
  to disk or Postgres.** Process restart clears everything (frames are ephemeral by design).
- **Fan-out**: each watcher socket receives the latest frame on change (or on a slow keepalive
  cadence). One employee can be watched by multiple admins simultaneously — the client does not
  know or care how many watchers exist.
- **Activity-gated capture**: the client pushes **only while the server tells it to**. The server
  enables streaming for an employee when the first watcher attaches and sends a `stop` control
  when the last watcher detaches. No watcher ⇒ no frames ⇒ zero capture while nobody looks.
  (Optional idle timeout as defense-in-depth.)
- **Capture rate**: 10 fps JPEG (q≈60, scaled to max 1600px wide). ~50–150 KB/frame → ~400–1200
  kbit/s worst case per active stream. Tunable via env.

### Client performance strategy (no lag)

| Component | Approach | Why |
|---|---|---|
| Screen capture | **DXGI Desktop Duplication API** (Windows 8+) | GPU-accelerated, captures at ~0.5ms per frame vs GDI's ~5-10ms. Zero CPU copy. |
| JPEG encoding | **LibJPEG-turbo** (SIMD-optimized) | ~2-5ms per frame at 1600px vs System.Drawing's ~15-20ms |
| Pipeline | **Dedicated background thread** | Capture + encode runs on its own thread, never touches the collection loop |
| Frame delivery | **Channel<T>** to WebSocket sender | Backpressure if sender is slow — latest frame wins, old frames dropped |

**Total per-frame cost target:** <10ms (capture ~0.5ms + encode ~3ms + resize ~1ms) — at 10 fps that's <10% of one CPU core.

**Adaptive throttling:**
- If frame time >80% of interval → skip frame (never starve the main loop)
- If CPU usage high → drop to 5 fps automatically
- Server sends `stop` → zero capture overhead immediately

---

## 3. Server (Go) — new files & changes

| File                                           | Change                                                                                                                                                                                                                                                                                                                          |
| ---------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `server/internal/stream/hub.go`              | **NEW.** `Hub` struct: `map[employeeID]*mailbox` + watcher registry per employee. Methods: `StartStream(empID)` (idempotent), `StopStream(empID)`, `PushFrame(empID, jpeg)`, `Subscribe(empID, ch)`, `Unsubscribe(...)`. Holds only the latest frame per employee in memory.                                |
| `server/internal/stream/hub_test.go`         | **NEW.** Unit tests: push→subscribe ordering, stop clears mailbox, multi-watcher fan-out, concurrent push/read (race detector). *(Breaks the "no tests" streak for this feature.)*                                                                                                                                     |
| `server/internal/handlers/stream_handler.go` | **NEW.** Two WebSocket endpoints + one REST endpoint:                                                                                                                                                                                                                                                                     |
|                                                | `GET /api/v1/live-stream/push` — **client**, upgraded under DeviceAuth semantics (`Authorization: Device <device_token>`, employee-Bearer legacy fallback). Client pushes binary JPEG frames + JSON control messages. Rejects with 403 if `terms_consent` has no `accepted` row for `featureId="live_stream"`. |
|                                                | `GET /api/v1/live-stream/watch?employeeId=` — **web**, upgraded under JWTAuth semantics (web-admin cookie). Validates consent + employee existence before upgrade. Sends binary frames from the mailbox; sends `{"type":"status","streaming":bool}` on start/stop.                                                   |
|                                                | `GET /api/v1/live-stream/employees` — **web**, REST (JWTAuth): `{ employeeId, name, department, online, streaming }[]` — `online = last_heartbeat_at within 60 s` (single query joining `employees` LEFT JOIN `app_status` on `last_heartbeat_at`).                                                         |
| `server/internal/router/router.go`           | Register`/live-stream/*` routes. Push under the sync group, watch + employees under `protected`. **Respect the auth-separation rule.**                                                                                                                                                                                |
| `server/cmd/server/main.go`                  | Construct`stream.Hub`, pass into the handler; begin graceful-shutdown closing of all sockets.                                                                                                                                                                                                                                 |
| WS library                                     | Add`github.com/gorilla/websocket` (industry default, Echo-compatible; check go.mod after `go get`).                                                                                                                                                                                                                         |
| (optional, phase 2)                            | `server/internal/jobs/stream_idle_sweep.go` — close streams whose watcher left without a `stop` (leak guard).                                                                                                                                                                                                              |

**WS auth notes** (browsers can't set `Authorization` headers on `new WebSocket()`):

- Watch socket: the httpOnly web cookie rides automatically (same-origin/direct-to-Go) — standard
  JWTAuth middleware validates before upgrade.
- Push socket (client is .NET, so it CAN set headers): `Authorization: Device <token>` validated
  by the existing DeviceAuth logic extracted/reused for the upgrade path.

**Server env knobs** (`.env` + `.env.example`): `LIVE_STREAM_ENABLED=true`,
`LIVE_STREAM_MAX_FPS=10`, `LIVE_STREAM_FRAME_MAX_BYTES=524288` (drop oversized frames).

### Consent check (server-side, both sockets)

Reuse `TermsConsentRepo.HasAccepted(ctx, employeeID, "live_stream", "")` before upgrading either
socket. Without consent: push socket → 403 (`consent_required`), watch socket → 200 REST but
`consentMissing: true` in the employees payload + watch rejected with 403.

---

## 4. Client (.NET) — new files & changes

| File                                        | Change                                                                                                                                                                                                                                                                                                                                                                   |
| ------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `client/Services/ScreenCaptureService.cs` | **NEW.** Hosted service. Windows-only body guarded by `OperatingSystem.IsWindows()`; Linux/macOS log once and stay parked. DXGI Desktop Duplication API for capture (hardware-accelerated, ~0.5ms/frame) → LibJPEG-turbo encoding (~3ms/frame) → downscale to ≤1600px wide. Runs on dedicated background thread at `ALPHA_STREAM_FPS`. Produces frames only while `_streamActive`. Adaptive throttling: skip frame if >80% of interval, drop to 5 fps if CPU high. |
| `client/Services/LiveStreamClient.cs`     | **NEW.** Outbound WebSocket (ClientWebSocket) to `ws(s)://{ServerUrl}/api/v1/live-stream/push` with the `Authorization: Device` header (same header logic as TermsService's `ApplyAuthHeader`). Auto-reconnect with capped backoff; sends `{action:"hello"}` on connect; feeds JPEG frames from the capture service; closes cleanly on stop.               |
| `client/Configuration/AppConfig.cs`       | Add:`ALPHA_STREAM_ENABLED` (default **false** — capture is opt-in per fleet, flipped by the server's start control anyway), `ALPHA_STREAM_FPS` (default 10), `ALPHA_STREAM_MAX_WIDTH` (1600), `ALPHA_STREAM_JPEG_QUALITY` (60). `--print-config` lines too.                                                                                              |
| `.env` + `.env.example`                 | Add the four`ALPHA_STREAM_*` keys **before** the next `config.enc` bake (Installer-Parity item 5).                                                                                                                                                                                                                                                             |
| `Program.cs`                              | Register the two services (singleton + hosted, same pattern as SyncService) —**inside the existing feature-gates section**, parked when `ALPHA_STREAM_ENABLED=false`.                                                                                                                                                                                           |

**Notes**

- Screen capture on Windows via DXGI works from a headless service session (the tracker already
  runs in the interactive user session; no UI needed for capture).
- Linux/macOS builds compile the same services but the capture body returns immediately — the
  push socket simply never sends frames; the sidebar shows those employees online with
  `streamAvailable: false` (client advertises capability in its `hello` message; server stores it
  in the mailbox entry and surfaces it in the employees + watch payloads).
- Privacy default: even with `ALPHA_STREAM_ENABLED=true`, **frames flow only after a watcher
  attaches** and the server issues `start`. Closing the web tab stops capture within seconds.

---

## 5. Web (Next.js) — new files & changes

| File                                                   | Change                                                                                                                                                                                                                                                                       |
| ------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `web/src/lib/api.ts`                                 | Add`LiveStreamEmployee` interface + `liveStreamApi.employees()` REST call.                                                                                                                                                                                               |
| `web/src/lib/useLiveStreamSocket.ts`                 | **NEW hook.** Connects to `ws(s)://${NEXT_PUBLIC_WS_URL}/api/v1/live-stream/watch?employeeId=…`, credentials `include`; decodes binary JPEG → `Image`/bitmap → draws to canvas; tracks `streaming` status + reconnect; closes on unmount / employee switch. |
| `web/src/app/(app)/live-stream/page.tsx`             | **REWRITE** (currently an honest empty state). Two-pane layout:                                                                                                                                                                                                        |
|                                                        | **Left rail** — all employees, search filter, green/gray dot `online \| offline` from `GET /live-stream/employees` (React Query, refetchInterval ~15 s). Rows sorted online-first, then name. Streaming indicator dot when active. Click → right pane.            |
|                                                        | **Right pane** — live canvas. States: idle ("select an employee"), offline (grayed + explanation), consent-missing (explanatory), connecting, live (canvas + subtle LIVE badge + fps readout), unavailable (client online but capture not supported on its OS).       |
|                                                        | Selected employeeId is kept in the URL (`?employeeId=…`, `useUrlQueryState`) per the URL-Synced Filters Rule, wrapped in `<Suspense>`.                                                                                                                                |
|                                                        | Search box uses a local debounced mirror (no Clear button) — same pattern as other pages.                                                                                                                                                                                   |
| `web/next.config.ts` / docs                          | `NEXT_PUBLIC_WS_URL` documented (`web/.env.example` if present, otherwise README/ARCHITECTURE note).                                                                                                                                                                     |
| `web/ARCHITECTURE.md` + root `AGENTS.md` changelog | Update the live-stream row from "honest empty state" to the new live page; add the changelog entry when implemented.                                                                                                                                                         |

**UI conventions honored:** server-driven infinite scroll is not required here (the employees
health list is small and polled — if it ever needs pagination, it converts to
`useInfiniteQuery` + sentinel), URL-synced state, no localStorage, no hardcoded employee names.

---

## 6. Data & privacy guarantees (requirement 3)

- **No DB table** is created. No migration. `git grep live_stream` on the server finds only the
  consent `featureId` string and in-memory keys.
- Frames live in a bounded in-memory map, overwritten in place; `StopStream` frees the buffer.
- Server logs never include frame content; logs record start/stop/attach/detach events only.
- The client writes nothing to its SQLite DB for this feature (no `app_items` rows, no sessions).
- When the last watcher disconnects: server sends `stop` → client stops capture + closes socket →
  mailbox entry deleted. Crash of either side leaves at most a stale socket that the hub reaps on
  ping timeout (gorilla default pong-wait 60 s).
- Consent is re-checked on every socket upgrade — revoking consent in the web T&C admin instantly
  prevents new streams (existing sockets get closed by the hub on the next consent re-check tick,
  phase-2 polish).

---

## 7. Implementation order (PR-sized steps)

1. **Server hub + tests** (`stream/hub.go` + tests) — pure logic, no sockets. `go build`, `go vet`, `go test ./internal/stream/...`.
2. **Server endpoints + router wiring** (gorilla dependency, WS upgrade, auth + consent checks, employees REST). Verify: `go build`, `go vet`, live curl of `/live-stream/employees` with a web cookie.
3. **Web page** — left rail + right pane against the real REST endpoint; watch socket wired with a stub loopback test (server echoes a test frame when the client is offline: `LIVE_STREAM_TEST_FRAME=1` dev-only knob). `npx tsc --noEmit`, `next build`.
4. **Client capture + push socket** (Windows first; `ALPHA_STREAM_ENABLED=false` default; `--print-config` lines). Verify: `dotnet build` 0/0, `--print-config`, live end-to-end with `dotnet run` on a Windows machine.
5. **Installer parity** — `.env` keys → re-bake `config.enc`, `bash publish/build-installer.sh -b win`, install, verify streaming from the **installed** build; document the Linux/macOS "unavailable" behavior in the ship-test checklist.
6. **Docs** — `AGENTS.md` changelog + §1/§5 rows, `server/ARCHITECTURE.md` API table, `client/ARCHITECTURE.md` services list, `web/ARCHITECTURE.md` page status flip.

## 8. Verification matrix

| Check                  | Command / method                                                                                                                 | Status |
| ---------------------- | -------------------------------------------------------------------------------------------------------------------------------- | ------ |
| Server build/vet/tests | `go build`, `go vet`, `go test ./internal/stream/...`                                                                      | ⬜     |
| Web typecheck + build  | `npx tsc --noEmit`, `npm run build`                                                                                          | ⬜     |
| Client build           | `dotnet build` (0 warnings / 0 errors)                                                                                         | ⬜     |
| Health API live        | curl`GET /api/v1/live-stream/employees` with web cookie                                                                        | ⬜     |
| End-to-end preview     | Windows machine with installed client + web dashboard, second browser to verify fan-out                                          | ⬜     |
| Ephemeral guarantee    | Watch stream → close tab → confirm client socket closes + no new frames captured (client log) and nothing new in Postgres/disk | ⬜     |
| Consent enforcement    | Revoke`live_stream` consent → watch upgrade returns 403                                                                       | ⬜     |
| Installer ship-test    | installed build streams (Windows) / reports unavailable (Linux dev PC)                                                           | ⬜     |
| Performance            | Client PC CPU <10% during 10 fps streaming; no lag on collection loop                                                           | ⬜     |

## 9. Risks & honest gaps

- **Wayland/Linux capture is out of scope this phase** — sidebar will honestly show those
  employees as online-but-unavailable rather than pretending.
- **Next.js dev proxy does not relay WebSockets** — the browser must reach the Go origin directly
  (`NEXT_PUBLIC_WS_URL`); in production both live behind the same reverse proxy, so this is a dev
  ergonomics issue only. CORS: the Go server must accept the cross-origin WS upgrade with
  credentials for the web origin (echo `Origin` allowlist from an env var).
- **Multi-monitor**: phase 1 captures the primary screen only; "which monitor" selection is a
  follow-up.
- **No rate limiting infra exists** — the push socket is auth-gated, and frames are
  size-capped; a full rate-limiting story remains a known project-wide gap.
- **Security**: WS endpoints inherit the same "no server-side permission model" project gap —
  any web-admin session can watch any consented employee. Acceptable within the current RBAC
  posture (client-side gating), noted rather than hidden.
- **DXGI Desktop Duplication**: requires Windows 8+ and a desktop session (no RDP). If the
  employee is on RDP, capture fails gracefully — the client logs it and reports
  `streamAvailable: false`.
