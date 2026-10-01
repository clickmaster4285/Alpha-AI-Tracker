# Plan ΓÇö Client "ws" instance: long-lived WebSocket connection to the server

**Status:** IMPLEMENTED ΓÇö connectivity gated on `SyncService` sync-success (sticky 2xx) +
timeout/reconnect fallback for idle employees. Dedicated `GET /api/v1/ws` under DeviceAuth.
**Scope:** client-focused (new `ws` service) + the tiny server WS endpoint it needs.
**Does NOT include:** screen-preview streaming (existing `LiveStreamClient`), web changes, or any
payload protocol beyond a keep-alive control channel.

---

## 1. Goal (from the request)

Add one more instance under `client/` called **`ws`** (WebSocket). Its ONLY job is to open and hold a
**long-lived WebSocket connection to the server**. On client start:

1. First check that the internet is available.
2. Only when internet is available, bring the `ws` instance up.
3. The `ws` instance connects to the server over WebSocket and stays connected indefinitely
   (auto-reconnecting).

This is a **presence / keep-alive / control channel** ΓÇö a connection that simply stays open, separate
from the on-demand screen-preview stream already handled by `LiveStreamClient`.

---

## 2. Current state (evidence)

- `client/Services/LiveStreamClient.cs` ΓÇö `BackgroundService` that already maintains a long-lived
  outbound WS to `GET /api/v1/live-stream/push` (DeviceAuth), using:
  - `System.Net.WebSockets` (built-in .NET 10 WS client ΓÇö no new dependency),
  - employee `DeviceToken`/`Token` for auth,
  - exponential-backoff reconnect loop (2s ΓåÆ 60s cap),
  - `stoppingToken` cooperative cancellation.
  - ΓÜá∩╕Å It is gated on `ALPHA_STREAM_ENABLED` and semantically about **capturing/previewing frames**.
    It is not a generic always-on channel, so we do NOT reuse it ΓÇö we make a new, simpler service
    that reuses its connection/auth pattern.
- `server/internal/stream/hub.go` + `server/internal/router/router.go` ΓÇö the `/live-stream/push`
  WS route exists under DeviceAuth but is frame-stream specific (hub tracks streams/watchers/frames).
  Pointing a *general* long-lived connection at it would misuse its state machine.
- `client/Configuration/AppConfig.cs` ΓÇö all feature knobs are declared here (e.g. `StreamEnabled`).
- `client/Program.cs` ΓÇö DI registration point for all `BackgroundService`s.
- `client/.env.example` ΓÇö where new config knobs are documented.

---

## 3. Design
### 3.1 New client service: `client/Services/WsClient.cs` (`BackgroundService`)

Mirrors `LiveStreamClient`'s proven shape but with **no capture and no frame logic** ΓÇö its entire job
is "stay connected".

> **Connectivity signal ΓÇö user-approved design (no separate internet probe).** Instead of the ws
> probing the network itself, it reuses a signal the client already produces every minute: **"a
> journey/app-session sync POST reached the server with a 2xx"**. When `SyncService` completes a
> successful drain pass, it sets a `serverReachable` flag; `WsClient` waits on that flag before
> opening the socket. Because the sync fires roughly every minute while there is anything to sync,
> this proves the server is reachable with **zero extra network calls** and no polling loop.

**Lifecycle loop (`ExecuteAsync`):**

1. **Wait for the sync-success signal.** Load `EmployeeInfo` from the store for identity, then wait
   for `SyncService` to report a successful server round-trip (the every-minute journey sync). While
   "not yet reachable", sleep briefly and re-wait ΓÇö **no WS attempts, no probing, no lag** (this is
   the "check internet first; push ws up only when available" gate).
   - ΓÜá∩╕Å **Caveat handled:** the journey sync only fires when there is data to send, so an idle
     employee could sit with no sync. The ws's **built-in reconnect (step 5) is the fallback** ΓÇö it
     quietly retries the socket on a short cadence, so the ws still comes up promptly even when there
     was nothing to sync. The sync signal is the *primary* enabler; the reconnect loop guarantees the
     ws is never stranded.
2. **Wait for identity.** Load `EmployeeInfo` from the store; if there is no `DeviceToken`/`Token`,
   wait (like `LiveStreamClient` does) until a login exists.
3. **Connect.** Open the WS to the configured endpoint with the `Authorization: Device <token>` (or
   employee Bearer) header ΓÇö same handshake as `LiveStreamClient`.
4. **Stay connected.** Keep the socket open. Send an application-level ping on a timer; treat any
   received frames (heartbeat/ack/control) by ignoring/logging. This is the "long time" part.
5. **Reconnect.** On error/close, back off exponentially (2s ΓåÆ 60s) and restart at step 2/3. This
   reconnect loop is also the safety net that brings the socket up without waiting for the next sync.
6. **Shutdown.** On `stoppingToken` cancellation, close the socket gracefully.

Cancellation, logging, and cooperative shutdown follow the exact conventions of `LiveStreamClient`.
### 3.2 New config (client)

Add to `client/Configuration/AppConfig.cs` (+ `.env.example` + comments):

| Key | Type | Default | Purpose |
| --- | --- | --- | --- |
| `ALPHA_WS_ENABLED` | bool | `false` | Master switch (fleet opt-in, default OFF like `StreamEnabled`). |
| `ALPHA_WS_PING_SEC` | int | `30` | Keep-alive ping interval while connected. |
| `ALPHA_WS_RECONNECT_BASE_SEC` | int | `2` | Initial reconnect backoff (capped at 60s, same as live stream). |

WS URL is **derived** from `ALPHA_SERVER_URL` + a fixed path (no extra config), so it can never drift
from the server the client already talks to. Connectivity is gated on `SyncService` success (below),
so there is **no separate probe knob**.

### 3.3 Server: dedicated lightweight WS endpoint

Add one minimal route under the existing **DeviceAuth** `syncGroup` (not the web `JWTAuth` group ΓÇö the
client owns it; see the *Client-vs-Web API Auth Separation Rule*):

- `GET /api/v1/ws` ΓåÆ establishes the WS, sends a welcome/hello, and holds the connection. Keep-alive
  is application-level (ping/pong), and the connection is tracked in an in-memory registry (no DB, no
  streaming/hub semantics) so the client's long-lived socket is visible/alive server-side.

This keeps the new channel **independent** from the screen-preview `stream` hub.

### 3.4 DI registration (client `Program.cs`)

Register `WsClient` as a `BackgroundService` following the same pattern as `LiveStreamClient`, gated by
`ALPHA_WS_ENABLED`.

---

## 4. Files to change

| File | Change |
| --- | --- |
| `client/Services/WsClient.cs` | **new** ΓÇö long-lived WS `BackgroundService` (sync-success gate ΓåÆ auth ΓåÆ connect ΓåÆ stay ΓåÆ reconnect). |
| `client/Configuration/AppConfig.cs` | add `WsEnabled`, `WsPingSec`, `WsReconnectBaseSec` (no probe knob). |
| `client/Services/SyncService.cs` | expose the "server round-trip succeeded" signal (`serverReachable` flag + wait handle) that gates the ws. |
| `client/Program.cs` | DI-register `WsClient` as a `BackgroundService`. |
| `client/.env.example` | document the 3 new `ALPHA_WS_*` keys (defaults). |
| `server/internal/router/router.go` | register `GET /api/v1/ws` under the syncGroup (DeviceAuth). |
| `server/internal/stream/ΓÇª` (or new `server/internal/ws/ΓÇª`) | minimal WS handler + in-memory connection registry (no DB). |
| `client/ARCHITECTURE.md` | note the new service (optional, if a service table exists). |

No SQLite schema change, no web change, no branding/version change, no new third-party dependency
(`System.Net.WebSockets` is built in).

---

## 5. Verification

1. `dotnet build` (client) ΓÇö 0 errors / 0 warnings.
2. `go build` + `go vet` (server).
3. Manual/dev: run server, run client with `ALPHA_SERVER_URL` set ΓåÆ confirm the ws connects only after
   a successful sync round-trip, stays connected, survives a server restart (reconnects), and shuts
   down on stop.
4. **Installer parity** (mandatory per AGENTS.md): the new service compiles into `client.dll` (no new
   binary asset), but `ALPHA_WS_*` knobs must reach installed builds via the normal `config.enc`
   pipeline ΓÇö add them to `.env` **before** `encrypt-config.sh`, then build + ship-test a platform
   installer.

---

## 6. Open questions / risks

- **Endpoint choice:** I recommend a new dedicated `/api/v1/ws` rather than reusing `/live-stream/push`,
  because the stream route is wired into the frame hub's watcher state machine. (Please confirm.)
- **Payload:** currently none by design ("only to connect"). If you later want the channel to carry
  serverΓåÆclient commands or clientΓåÆserver telemetry, that is a follow-up ΓÇö the connection transport
  this plan adds is the foundation.
- **Always-on vs opt-in:** default `ALPHA_WS_ENABLED=false` to match existing privacy/fleet-opt-in
  conventions (`StreamEnabled`, `LocationEnabled`). Confirm you want it ON by default for your fleet.

---

## 7. Definition of done

1. Γ£à `WsClient` gated on `SyncService` sync-success + long-lived WS connection implemented and registered.
2. Γ£à Config knobs in `AppConfig.cs`, `.env.example` (ship `config.enc` before installer bake).
3. Γ£à Server `/api/v1/ws` DeviceAuth endpoint keeps the connection alive (`PRESENCE_WS_*`).
4. Γ¼£ `dotnet build` clean; `go build`/`go vet` clean; installer built & ship-tested.
5. Γ£à No unrelated changes; no commit/push (only on your explicit request).
