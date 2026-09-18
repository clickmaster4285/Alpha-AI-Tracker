# Live Stream — Implementation Plan

| | |
|---|---|
| **Status** | Phase 1 complete (Windows); Phase 2 multi-monitor in progress |
| **Branch** | `feature/live_stream` |
| **Created** | 2026-09-18 |
| **Updated** | 2026-09-18 |
| **Scope** | Windows capture first; Linux/macOS report unavailable (no Linux machine for ship-test yet) |
| **Privacy** | Preview only — no recording, no disk, no DB storage |

**Driver:** Admin selects an employee → sees that employee's screen live.
Employee T&C consent (`featureId = "live_stream"`) is required and re-checked server-side before any frame flows.

---

## 0. Requirements

| # | Requirement | Delivery |
|---|-------------|----------|
| 1 | Left rail: employees with **online / offline** | `GET /api/v1/live-stream/employees` — online if `app_status.last_heartbeat_at` within 60 s (same threshold as client crash recovery). Web: `/live-stream` left rail. |
| 2 | Click employee → live screen on the right | Web canvas fed by server WebSocket relay. |
| 3 | Preview only — nothing recorded or saved | In-memory latest-frame mailbox only. Client pushes JPEG only while ≥1 admin is watching. Last watcher leaves → capture stops. |

---

## 1. Transport decision: WebSocket now, WebRTC later

### Decision (final for Phase 1)

| Protocol | Role |
|----------|------|
| **HTTP** | Existing sync APIs — unchanged |
| **WebSocket** | Live stream control + JPEG frame relay (client push + admin watch) |
| **WebRTC** | **Not used in Phase 1** — reserved for a later quality/scale phase |

### Why not WebRTC in Phase 1?

WebRTC is attractive for latency and bandwidth (H.264/VP8 + congestion control). For *this* product and stack it is the wrong first ship:

| Concern | WebSocket JPEG relay | WebRTC |
|---------|----------------------|--------|
| Corporate NAT / firewall | Client opens outbound WS to server (already works for sync) | True P2P admin↔employee usually fails; needs **TURN** and/or an **SFU** |
| Server role | Simple in-memory relay (fits “ephemeral, no storage”) | Need signaling + media path (pion SFU / LiveKit / mediasoup) — large new subsystem |
| .NET client maturity | `ClientWebSocket` + DXGI + managed JPEG is proven | Headless DXGI → WebRTC encode on .NET is immature / heavy native deps |
| Privacy model | Trivial: stop pushing frames | Media sessions, ICE state, TURN credentials, harder to reason about “nothing on disk” |
| Time-to-ship | Days | Weeks of infra + ops |
| Scale path | Enough for tens of concurrent previews | Better when you need 30+ fps HD or hundreds of concurrent streams |

**Phase 1 ships the control plane and privacy model that WebRTC would still need** (consent, start/stop, employee list, auth split). A later Phase 2 can replace the JPEG payload path with WebRTC media while keeping the same hub semantics (`start` / `stop` / watcher count).

### Scalability model (Phase 1)

Designed so load grows without rewriting the product:

- **Latest-frame-wins mailbox** — slow watchers never queue; memory is `O(active_employees)` frames, not `O(fps × watchers)`.
- **Activity-gated capture** — zero encode CPU when watcher count is 0.
- **Hard caps (env):** max concurrent streaming employees, max watchers per employee, max frame bytes, max FPS.
- **Backpressure:** client drops frames if the WS send buffer is full; server drops oversized frames.
- **Horizontal scale later:** sticky sessions or Redis pub/sub for mailbox fan-out (not required for v1 single-node). Hub interface stays `PushFrame` / `Subscribe` so the backend can swap.

Capacity target (single server node, conservative):

| Metric | Target |
|--------|--------|
| Concurrent streaming employees | 25 (configurable) |
| Watchers per employee | 10 |
| Frame size | ≤ 512 KB |
| Bitrate per stream | ~0.4–1.2 Mbit/s at 10 fps / 1600 px / q≈60 |

---

## 2. Why this design (repo constraints)

1. **No real-time infra today** — first WebSocket surface; wire cleanly in router + `main.go`.
2. **Client is outbound-only** — client opens the push socket; server never dials the employee PC.
3. **Online signal already exists** — `last_heartbeat_at` in `app_status` (60 s stale). No new health endpoint on the client.
4. **Auth separation (mandatory)** — push → `DeviceAuth` (sync group); watch + employees → `JWTAuth` (protected group).
5. **Consent gate exists** — `TermsConsentRepo.HasAccepted(employeeID, "live_stream", …)`.
6. **Next.js rewrites do not proxy WebSockets reliably** — browser uses `NEXT_PUBLIC_WS_URL` (direct Go origin). Reuse `CORS_ALLOWED_ORIGINS` for WS `CheckOrigin`.
7. **Cross-platform analyzer safety** — `OperatingSystem.IsWindows()` guards inside method bodies; no `[SupportedOSPlatform]` on hosted-service graphs.
8. **Installer parity** — `ALPHA_STREAM_*` in `.env` before `config.enc` bake; ship-test from installed Windows build.

---

## 3. Architecture

```
┌──────────────┐  WS binary JPEG + JSON ctrl   ┌─────────────────────┐  WS binary JPEG   ┌──────────┐
│ Client (Win) │ ─────────────────────────────▶│ Server Hub          │ ─────────────────▶│ Web admin│
│ DXGI capture │  /api/v1/live-stream/push     │ latest-frame mailbox│  /live-stream/watch│  canvas  │
└──────────────┘                               │ fan-out to N admins │                   └──────────┘
                                               └─────────────────────┘
```

### Connection lifecycle (committed)

1. Admin opens watch WS → server increments watcher count for `employeeId`.
2. If count goes `0 → 1`, server marks stream **wanted** and waits for client.
3. Client keeps a **persistent control connection** only while `ALPHA_STREAM_ENABLED=true` and logged in (lightweight; no frames). On `{"type":"start"}`, capture starts and frames flow. On `{"type":"stop"}` or last watcher gone, capture stops immediately; control socket stays up for the next `start`.
4. Idle defense: if no frame and no watcher for `LIVE_STREAM_IDLE_SEC` (default 90), hub clears mailbox and sends `stop`.
5. Client crash / network loss: gorilla ping/pong (~60 s) reaps sockets; watcher count decrements; last watcher path fires `stop`.

Rationale for persistent control WS (not connect-on-demand): start latency stays low when an admin clicks; reconnect storms are avoided; frame path remains strictly activity-gated.

### Wire protocol

**Client → server (push socket)**

| Message | Format | Meaning |
|---------|--------|---------|
| `hello` | JSON `{"type":"hello","platform":"windows","streamAvailable":true,"version":"…"}` | Capability advertisement |
| frame | Binary (raw JPEG bytes) | Latest preview frame |
| `pong` | JSON (optional) | Respond to server ping |

**Server → client (push socket)**

| Message | Meaning |
|---------|---------|
| `{"type":"start"}` | Begin capture + push frames |
| `{"type":"stop"}` | Stop capture; keep control socket |
| `{"type":"error","code":"…"}` | Fatal; client closes |

**Server → web (watch socket)**

| Message | Meaning |
|---------|---------|
| Binary JPEG | Frame |
| `{"type":"status","streaming":bool,"streamAvailable":bool,"consentMissing":bool}` | State |

### Capture pipeline (Windows)

| Stage | Approach | Notes |
|-------|----------|-------|
| Capture | DXGI Desktop Duplication | GPU path; primary monitor only in Phase 1 |
| Encode | **Managed JPEG** (`System.Drawing` / ImageSharp) | No native LibJPEG-turbo in Phase 1 — avoids installer native packaging. Revisit if encode CPU exceeds budget. |
| Resize | Max width `ALPHA_STREAM_MAX_WIDTH` (1600) | Preserve aspect ratio |
| Threading | Dedicated capture loop + `Channel<byte[]>` (capacity 1, drop-oldest) | Never blocks collection / sync loops |
| Rate | `ALPHA_STREAM_FPS` (default 10); adaptive drop to 5 if frame time > 80% of interval | |

**Per-frame budget:** &lt;15 ms average at 1600 px (DXGI + managed JPEG). If over budget, skip frame — never starve the tracker.

Linux / macOS: services register but capture is a no-op; `hello.streamAvailable=false`; UI shows “unavailable”.

---

## 4. Server (Go)

| File | Change |
|------|--------|
| [`server/internal/stream/hub.go`](server/internal/stream/hub.go) | **NEW.** In-memory hub: per-employee mailbox + watcher registry + wanted/streaming flags. Methods: `WantStream`, `UnwantStream`, `PushFrame`, `Subscribe`, `Unsubscribe`, `SetCapability`, `SnapshotEmployees` helpers as needed. Caps from config. |
| [`server/internal/stream/hub_test.go`](server/internal/stream/hub_test.go) | **NEW.** Ordering, multi-watcher fan-out, stop clears mailbox, concurrent push/subscribe (race detector), cap enforcement. |
| [`server/internal/handlers/stream_handler.go`](server/internal/handlers/stream_handler.go) | **NEW.** Endpoints below. |
| [`server/internal/router/router.go`](server/internal/router/router.go) | Push under sync/`DeviceAuth`; watch + employees under `protected`/`JWTAuth`. |
| [`server/cmd/server/main.go`](server/cmd/server/main.go) | Construct `Hub`, inject handler, close all sockets on graceful shutdown. |
| Dependency | `github.com/gorilla/websocket` |

### Endpoints

| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| `GET` | `/api/v1/live-stream/push` | DeviceAuth | Client control + binary frames |
| `GET` | `/api/v1/live-stream/watch?employeeId=` | JWTAuth | Admin frame feed |
| `GET` | `/api/v1/live-stream/employees` | JWTAuth | Rail list (see DTO) |

### Employees DTO (committed shape)

```json
{
  "employeeId": "EMP-10001",
  "name": "…",
  "department": "…",
  "online": true,
  "streaming": false,
  "streamAvailable": true,
  "consentMissing": false
}
```

- `online` — heartbeat within 60 s  
- `streaming` — hub has active capture / recent frame  
- `streamAvailable` — last `hello` from client (false for Linux/macOS / DXGI failure)  
- `consentMissing` — no accepted `live_stream` consent  

Consent: `HasAccepted(ctx, employeeID, "live_stream", "")` before upgrading push or watch. Push without consent → **403** `consent_required`. Watch without consent → **403**. List endpoint still returns the row with `consentMissing: true`.

### Server env

| Key | Default | Purpose |
|-----|---------|---------|
| `LIVE_STREAM_ENABLED` | `true` | Master switch |
| `LIVE_STREAM_MAX_FPS` | `10` | Drop client frames faster than this |
| `LIVE_STREAM_FRAME_MAX_BYTES` | `524288` | Drop oversized frames |
| `LIVE_STREAM_MAX_STREAMS` | `25` | Concurrent streaming employees |
| `LIVE_STREAM_MAX_WATCHERS_PER_EMPLOYEE` | `10` | Fan-out cap |
| `LIVE_STREAM_IDLE_SEC` | `90` | Reap orphaned wanted/streaming state |
| `LIVE_STREAM_TEST_FRAME` | `false` | Dev-only: synthetic frame when no client (web UI bring-up) |

WS `CheckOrigin` reuses `CORS_ALLOWED_ORIGINS` (already in [`server/internal/config/config.go`](server/internal/config/config.go)).

---

## 5. Client (.NET)

| File | Change |
|------|--------|
| [`client/Services/ScreenCaptureService.cs`](client/Services/ScreenCaptureService.cs) | **NEW.** Windows DXGI → resize → JPEG; parked on non-Windows. Active only while streaming. |
| [`client/Services/LiveStreamClient.cs`](client/Services/LiveStreamClient.cs) | **NEW.** Persistent control `ClientWebSocket` to `/api/v1/live-stream/push`; `Authorization: Device` via same pattern as `TermsService.ApplyAuthHeader`; handles `start`/`stop`; capped reconnect backoff. |
| [`client/Configuration/AppConfig.cs`](client/Configuration/AppConfig.cs) | `ALPHA_STREAM_*` + `--print-config`. |
| `.env` / `.env.example` | Four keys before next `config.enc` bake. |
| [`client/Program.cs`](client/Program.cs) | Register singleton + hosted; gated on `ALPHA_STREAM_ENABLED`. |

### Client env

| Key | Default | Purpose |
|-----|---------|---------|
| `ALPHA_STREAM_ENABLED` | `false` | Fleet opt-in (even when true, frames only after server `start`) |
| `ALPHA_STREAM_FPS` | `10` | Capture cadence |
| `ALPHA_STREAM_MAX_WIDTH` | `1600` | Downscale |
| `ALPHA_STREAM_JPEG_QUALITY` | `60` | JPEG quality |

DXGI requires an interactive desktop session (not RDP-only / session 0). On failure: log once, `hello.streamAvailable=false`, no crash loop.

---

## 6. Web (Next.js)

| File | Change |
|------|--------|
| [`web/src/lib/api.ts`](web/src/lib/api.ts) | `LiveStreamEmployee` + `liveStreamApi.employees()`. |
| [`web/src/lib/useLiveStreamSocket.ts`](web/src/lib/useLiveStreamSocket.ts) | **NEW.** Watch WS to `NEXT_PUBLIC_WS_URL`; credentials include; binary → canvas; status + reconnect; close on unmount / employee switch. |
| [`web/src/app/(app)/live-stream/page.tsx`](web/src/app/(app)/live-stream/page.tsx) | **REWRITE** empty state → two-pane UI. |
| Env / docs | Document `NEXT_PUBLIC_WS_URL` (default `ws://localhost:8080`). |
| Architecture docs | Flip live-stream from empty state → live; changelog in `AGENTS.md`. |

### UI states (right pane)

`idle` → `connecting` → `live` | `offline` | `consentMissing` | `unavailable` | `error`

Left rail: search (debounced, no Clear button), online-first sort, streaming indicator, `?employeeId=` via `useUrlQueryState` + `<Suspense>`.

Employee list is polled (~15 s); infinite scroll not required at current company sizes. If the list grows large later, convert to `useInfiniteQuery` + sentinel per the Web Infinite-Scroll Rule.

---

## 7. Privacy guarantees

- No migration, no Postgres table for frames.
- Frames exist only in the hub mailbox; `stop` / idle reap frees buffers.
- Logs: start/stop/attach/detach only — never frame bytes.
- Client SQLite: no stream rows.
- Consent re-checked on every socket upgrade; revoke blocks new streams immediately.
- Phase-1 polish: optional periodic re-check on long-lived sockets (same idle ticker).

---

## 8. Implementation order

1. **Hub + tests** — pure logic, no sockets. `go test ./internal/stream/...`
2. **Endpoints + router** — gorilla, auth, consent, employees REST, idle reap. `go build` / `go vet` + curl employees.
3. **Web UI** — rail + canvas against REST; watch socket with `LIVE_STREAM_TEST_FRAME=1`. `tsc` + `next build`.
4. **Client** — DXGI + managed JPEG + control WS (`ALPHA_STREAM_ENABLED=false` default). `dotnet build` 0/0; Windows e2e with `dotnet run`.
5. **Installer parity** — bake `config.enc`, Windows installer, ship-test installed build; document Linux unavailable.
6. **Docs** — `AGENTS.md`, server/client/web `ARCHITECTURE.md`.

### Phase 2 (in progress)

| Track | Status | Notes |
|-------|--------|-------|
| **Multi-monitor selection** | **Landed (code)** | Admin picker → `select_monitor` → client EnumDisplayMonitors; restart server+client+web to verify |
| Linux PipeWire / portal capture | Deferred | No Linux ship-test machine yet |
| WebRTC media path | Later | Keep control plane; swap JPEG payload |
| Redis-backed hub | Later | Multi-node fan-out |
| Server-side who-can-watch RBAC | Later | Today any admin with module access can watch |
---

## 9. Verification matrix

| Check | Method | Status |
|-------|--------|--------|
| Server build / vet / hub tests | `go build`, `go vet`, `go test ./internal/stream/...` | ⬜ |
| Web typecheck + build | `npx tsc --noEmit`, `npm run build` | ⬜ |
| Client build | `dotnet build` (0/0) | ⬜ |
| Health API | curl `GET /live-stream/employees` with web cookie | ⬜ |
| Online via heartbeat | `GET /live-stream/employees` — online if heartbeat/`updated_at` within **3 min** (sync cadence ~60 s) | ✅ |
| End-to-end preview | Windows client (`dotnet run`) + web watch | ✅ |
| Ephemeral guarantee | Close tab → client stops capture; nothing new on disk/Postgres | ⬜ verify on installed build |
| Consent | Revoke `live_view` → watch/push upgrade 403 | ⬜ |
| Caps | Exceed `MAX_STREAMS` / frame bytes → rejected cleanly | ⬜ |
| Installer | Installed Win streams; Linux shows unavailable | ⬜ Windows-only for now |
| Performance | Tracker collection loop unaffected; client CPU reasonable at 10 fps | ⬜ |

---

## 10. Risks

| Risk | Mitigation |
|------|------------|
| DXGI fails (RDP, locked, no desktop) | Graceful `streamAvailable: false`; UI “unavailable” |
| Cross-origin WS cookies in prod | Same reverse-proxy host in prod; dev uses `NEXT_PUBLIC_WS_URL` + `CORS_ALLOWED_ORIGINS` |
| Managed JPEG CPU | Adaptive FPS; upgrade encoder only if measured hot |
| Watcher disconnect without clean close | Ping/pong + `LIVE_STREAM_IDLE_SEC` |
| Any admin can watch any consented employee | Matches current project RBAC posture (client-side gates); Phase 2 server checks |
| Wayland/Linux out of scope | Honest unavailable state — no fake “loading” |

---

## 11. WebRTC FAQ (short)

**Q: Should we use WebRTC instead?**  
**A: Not for Phase 1.** Ship WebSocket JPEG relay first. It matches outbound-only clients, ephemeral privacy, and Go/Echo today. Revisit WebRTC when you need HD / higher FPS / many concurrent streams — the control plane (`start`/`stop`/consent/employees) stays the same; only the media transport changes.
