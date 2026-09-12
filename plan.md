# Live Screen Streaming Feature Plan

## 1. Legal / Compliance Layer (MANDATORY)

| Requirement | Implementation |
|-------------|----------------|
| **Explicit consent** | Employee login wizard must include "Screen monitoring" consent checkbox. Log consent timestamp in `app_status` or new `employee_consent` table. |
| **Real-time disclosure** | Client MUST show a system-tray banner or window chrome while being viewed: `"[ADMIN] is viewing your screen"`. |
| **Audit trail** | Server table `screen_view_sessions`: `admin_user_id`, `employee_id`, `started_at`, `ended_at`, `ip_address`, `user_agent`. Log every connect/disconnect. |
| **Role-gated** | Only `company_admin` or a dedicated permission (e.g. `live_screen_view`) can start a session. RBAC check in handler. |
| **Data retention** | Streams are NOT recorded by default. Recording requires separate consent + retention policy. |

---

## 2. Architecture: MJPEG-over-WebSocket

```
┌──────────────────┐      WebSocket (binary)       ┌──────────────────┐      WebSocket       ┌──────────────────┐
│ Employee Client  │ ─────────────────────────────▶ │      Go Server   │ ──────────────────▶ │   Admin Web      │
│  (.NET 10)       │    JPEG frames @ 1-5 FPS       │  (gorilla/ws)    │    Relay + Auth     │  (Next.js)       │
│                  │ ◀───────────────────────────── │                  │ ◀────────────────── │                  │
│  Screen Capture  │    Start/Stop control          │  Broadcast       │    Canvas/Video     │  Canvas render    │
│  + ImageSharp    │                                 │                  │                     │                  │
└──────────────────┘                                 └──────────────────┘                     └──────────────────┘
```

**Why MJPEG-over-WebSocket:**
- No NAT traversal — both client and server use outbound connections.
- No STUN/TURN servers needed.
- Single persistent connection — no ICE candidate exchange, no SDP negotiation.
- Works through corporate firewalls (standard ws/wss).
- 1–3 FPS MJPEG is standard in employee monitoring tools (Teramind, ActivTrak, Time Doctor).
- Sub-500ms latency with proper tuning.
- Low CPU — JPEG compression at 60–70% quality.

---

## 3. Library Recommendations

### Client (.NET 10)

| Purpose | Recommendation | Why |
|---------|---------------|-----|
| **Screen capture** | Native P/Invoke wrappers (no heavy library) | Thin platform-specific capture classes (~100 lines each). |
| **Image encoding** | `SixLabors.ImageSharp` (v3.x) | Pure managed, cross-platform, fast JPEG encode. Avoids `System.Drawing.Common` GDI+ on Linux. |
| **WebSocket client** | Built-in `System.Net.WebSockets.ClientWebSocket` | No NuGet needed. Send `ArraySegment<byte>` binary frames directly. |

**Screen capture per platform:**
- **Windows**: `BitBlt` from `user32.dll` (fast for 2–3 FPS) or DXGI Desktop Duplication for smoother.
- **Linux (X11)**: `XGetImage` via X11 library. **Wayland (GNOME)**: PipeWire screen capture (`Tmds.DBus.Protocol`) or GNOME Screencast portal (`org.freedesktop.portal.ScreenCast`).
- **macOS**: `CGDisplayCreateImage` from CoreGraphics.

**Image resize + JPEG encode pipeline (client):**
```csharp
using var bitmap = CaptureScreen();
using var resized = bitmap.Resize(1280, 720);
using var ms = new MemoryStream();
await resized.SaveAsync(ms, new JpegEncoder { Quality = 65 });
byte[] jpeg = ms.ToArray();
await _ws.SendAsync(jpeg, Binary, true, ct);
```

### Server (Go)

| Purpose | Recommendation | Why |
|---------|---------------|-----|
| **WebSocket server** | `gorilla/websocket` | De-facto standard. |
| **Auth middleware** | Reuse existing `JWTAuth` | Pass JWT in query param or first message. |
| **Broadcast** | In-memory hub or Redis Pub/Sub | In-memory fine for single-server; Redis for multi-instance. |

### Web Dashboard (Next.js)

| Purpose | Recommendation | Why |
|---------|---------------|-----|
| **WebSocket client** | Native `WebSocket` API | No library needed. |
| **Display** | `<canvas>` + `requestAnimationFrame` | Draw JPEG blobs via `URL.createObjectURL(blob)`. Smoother than `<img>` refresh. |

---

## 4. Implementation Plan

### Step 1: Client screen capture + streamer (new service)

New `ScreenStreamService.cs` (hosted service, like `SyncService`):

- **Config knobs**: `ALPHA_SCREEN_STREAM_FPS` (default 2), `ALPHA_SCREEN_STREAM_WIDTH` (default 1280), `ALPHA_SCREEN_STREAM_QUALITY` (default 65), `ALPHA_SCREEN_STREAM_ENABLED` (default false).
- **Connection lifecycle**: Connect to `wss://{server}/ws/screen-stream?token={jwt}` on startup when enabled. Auto-reconnect with backoff.
- **Control messages from server**: `{ "action": "start", "employeeId": "..." }` / `{ "action": "stop" }`. Client only streams when explicitly told to.
- **Capture loop**: `Timer` or `PeriodicTimer` at target FPS. Skip frame if previous encode hasn't finished (backpressure guard).
- **Disclosure**: Show tray tooltip + optional overlay banner while streaming.

### Step 2: Server WebSocket hub

New file `server/internal/ws/screen_stream_hub.go`:

- `Upgrade` handler at `GET /ws/screen-stream` (under JWTAuth).
- On connect: validate JWT, look up employee, wait for admin `start` command.
- On admin request (`POST /api/v1/admin/screen-stream/start/:employeeId`): find the employee's WS connection, send `start` control message.
- On admin disconnect or `stop` command: send `stop` to client.
- Broadcast: client sends binary JPEG → server forwards to the single admin subscriber.
- **Audit log**: write to `screen_view_sessions` on start/stop.

### Step 3: Admin web page

New route: `/employee-journey/live-screen?employeeId=EMP-12345`

- WebSocket connect to same origin (`/ws/screen-stream` or `/api/v1/ws/screen-stream` via Next.js rewrite).
- Admin button: **"Start Live View"** → calls `POST /api/v1/admin/screen-stream/start` → receives WebSocket frames → draws to `<canvas>`.
- Show: employee name, session timer, FPS indicator, **STOP** button.
- On page leave or STOP: call `POST /api/v1/admin/screen-stream/stop`.

### Step 4: Audit + RBAC

- New server migration: `036_screen_view_audit.sql`.
- New permission submodule: `live-screen` under `employee-journey` in RBAC seed.
- Only `company_admin` or users with that submodule can hit the start endpoint.

---

## 5. Performance / No-Lag Guarantees

| Parameter | Recommended Value | Reason |
|-----------|------------------|--------|
| Target FPS | 1–3 FPS for monitoring | Employee screens don't change at 60fps. 2 FPS = 500ms latency, imperceptible for "any time" viewing. |
| Resolution | 1280×720 or 854×480 | Downscale before JPEG encode. Reduces CPU + bandwidth by 4–10×. |
| JPEG quality | 60–70% | Good text readability, small file size (~15–40 KB/frame). |
| Backpressure | Skip frame if encode > 200ms | Prevents queue buildup on slow CPUs. |
| Idle behavior | Stream ONLY while admin is viewing | Zero CPU when idle. Client stops capture on `stop` command. |
| Network | ~100–300 KB/s at 2 FPS | Fits comfortably on any connection. |

**CPU budget on client** (modern CPU):
- Screen capture: ~1–5 ms
- Resize + JPEG encode (ImageSharp, 1280×720, q=65): ~10–30 ms
- WebSocket send: ~1 ms
- **Total per frame: ~15–40 ms** — well under the 500ms budget at 2 FPS.

**Do NOT do:**
- Do not stream at 30 FPS (wasteful, laggy on poor networks).
- Do not stream when no admin is viewing.
- Do not use `System.Drawing.Common` on Linux.

---

## 6. Codebase Integration Points

| What | Where |
|------|-------|
| New capture interfaces | `client/Core/Abstractions/IScreenCaptureService.cs` |
| Platform impls | `client/Services/ScreenCapture/WindowsScreenCapture.cs`, `LinuxScreenCapture.cs`, `MacOsScreenCapture.cs` |
| Stream service | `client/Services/ScreenStreamService.cs` (hosted, like `SyncService`) |
| WS client | Inside `ScreenStreamService`, uses `System.Net.WebSockets` |
| Server hub | `server/internal/ws/screen_stream_hub.go` |
| REST trigger | `server/internal/handler/screen_stream_handler.go` → `POST /api/v1/admin/screen-stream/start/:employeeId` |
| Audit table | New migration `036_screen_view_audit.sql` |
| Web page | `web/src/app/(app)/employee-journey/live-screen/page.tsx` |
| RBAC module | Add `live-screen` under `employee-journey` in RBAC seed |

---

## 7. Recommended Stack

- **Capture**: Thin P/Invoke wrappers per OS (no heavy library)
- **Encode**: `SixLabors.ImageSharp` (JPEG @ 60–70%, 1280×720)
- **Transport**: `gorilla/websocket` (server) + built-in `ClientWebSocket` (client)
- **Display**: HTML5 `<canvas>` in Next.js
- **FPS**: 1–3 (monitoring-grade, not video-grade)
- **Trigger**: Admin clicks "View" → server tells client to start streaming → client streams only while viewed
