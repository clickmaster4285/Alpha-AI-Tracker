# Live Employee Screen Viewing — Engineering Plan

> **Status:** Design. Not implemented.
> **Scope:** Admin-authorized, employee-visible, time-bounded live screen viewing.
> **Depends on:** Phase 0.5 (control channel), Phase 0 (T&C framework), server migration
> sequencing, and the existing Installer-Parity Rule.

## 1. Scope and non-negotiable product rule

The feature is **admin-authorized, employee-visible, time-bounded live screen viewing**. It
must not be implemented as hidden surveillance or as a permanent recording system.

Before implementation, obtain jurisdiction-specific legal and privacy advice. This plan is an
engineering design, not legal advice. The company should complete a DPIA/privacy impact
assessment and have counsel confirm the requirements for the countries in which employees work.

The first release should be **live-only**:

- No recording, replay, screenshots, audio, microphone, webcam, keyboard logging, or clipboard
  capture.
- The employee sees a persistent "Screen is being viewed" indicator while a session is active.
- The employee can stop sharing immediately, subject to documented workplace policy and local law.
- An admin must provide a reason and select an explicit duration (for example, 5, 15, or 30
  minutes); there is no "view forever" option.
- The server is the authority for authorization, session expiry, and revocation.

If recording is ever proposed, treat it as a separate product requiring a new DPIA, explicit
notice/consent or another documented lawful basis, retention/deletion controls, and a separate
approval process. Do not silently add recording to this feature.

## 2. Legal, privacy, and employment controls

### Required before pilot

1. Document the lawful basis and purpose limitation for every deployment jurisdiction. Obtain
   employee notice/consent where required; do not rely on a generic tracker login notice.
2. Publish an employee-facing notice explaining what is captured, who can view it, when it can be
   viewed, whether audio is excluded, retention, support contacts, and objection/rights processes.
3. Sign/update processor and subprocessor agreements, including the SFU/relay hosting provider,
   cloud region, subprocessors, support access, and international transfer safeguards.
4. Define role-based access, least privilege, break-glass access, manager approval requirements,
   and an access review cadence.
5. Prohibit use for covert monitoring, protected activity, union activity, health data inference,
   passwords/secret collection, or discriminatory decisions. A visible indicator does not make
   unlawful monitoring lawful.
6. Define incident response for unauthorized viewing, leaked session tokens, and a compromised
   admin account. Include notification timelines required by the applicable law.
7. Test accessibility and provide a non-streaming support path for employees who cannot use the
   indicator or stop control.

### Enforcement requirements

- Require an authorized permission such as `employee-journey/live-stream/view`.
- Prefer a second permission such as `employee-journey/live-stream/approve` for sensitive
  teams or production use.
- Enforce permissions in the Go API, not only in the Next.js route guard.
- Restrict an admin to employees/departments they are allowed to manage.
- Require MFA and a recent re-authentication for starting a view session.
- Log the requester, target employee, reason, approval, start/end time, duration, client/device,
  IP/address metadata appropriate to policy, revocation, and failed attempts.
- Make audit records append-only to normal admins and exportable for investigations.
- Never put the raw media stream, a reusable media token, or credentials in PostgreSQL logs,
  browser URLs, analytics, or ordinary application logs.

## 3. Recommended architecture for this repository

### Recommendation: WebRTC with an SFU, not screenshots, MJPEG, RTMP, or peer-to-peer

Use a WebRTC Selective Forwarding Unit (SFU) so the employee client uploads one encoded stream
and the admin browser receives it without the server decoding/re-encoding every frame.

Recommended first choice:

- **LiveKit self-hosted SFU** for the media plane and its Go server SDK for token generation.
- **WebRTC in the browser** using the LiveKit JavaScript client.
- A native WebRTC publisher in the .NET client, behind an `IScreenCapturePublisher`
  abstraction. Evaluate the LiveKit native SDK binding or a maintained libwebrtc binding
  before committing to a package; do not write a WebRTC protocol implementation.

Acceptable equivalent: a self-hosted Janus or mediasoup deployment if the operations team
already runs it. Do not introduce multiple media platforms. The SFU must be deployed in the
same approved data region as the API or in a documented regional topology.

Why this is preferred:

- Low latency (normally hundreds of milliseconds to about two seconds).
- Hardware/video encoder support and adaptive bitrate.
- No full-resolution image upload every second.
- Browser playback without a plugin.
- Selective forwarding avoids the Go API becoming a media bottleneck.
- Short-lived room tokens make revocation and least privilege practical.

Do **not** use:

- Base64 screenshots over REST/WebSocket: high bandwidth, poor motion quality, and weak
  real-time behavior.
- MJPEG or repeated PNG/JPEG frames: CPU-heavy, no congestion control, and poor scaling.
- RTMP/HLS for interactive support: HLS latency is too high; RTMP is not a browser-native
  end-to-end admin viewing path.
- Raw P2P as the only transport: corporate NAT/firewall failures and no reliable policy point.
- FFmpeg screen capture as the default client path: it can be useful for an offline recording
  workflow, but it is not the lowest-lag, lowest-CPU interactive design.

## 4. Cross-platform capture strategy

Keep platform capture separate from transport. The existing .NET client remains the agent and
the web application remains the viewer.

```text
OS capture API
  -> native frame source
  -> hardware/software H.264 or VP8/VP9 encoder
  -> WebRTC publisher (short-lived room token)
  -> SFU
  -> LiveKit Web viewer
```

Implement `IScreenCaptureSource` with these providers:

- **Windows:** Windows Graphics Capture / `Windows.Graphics.Capture`, with a user-visible
  OS capture consent flow where required. Prefer the monitor/window picker rather than silently
  selecting a hidden desktop. Use Desktop Duplication only as a documented fallback for older
  supported Windows versions.
- **macOS:** ScreenCaptureKit, with the system Screen Recording permission. Do not attempt to
  bypass TCC permissions.
- **Linux:** PipeWire ScreenCast portal (`xdg-desktop-portal`) and the user's desktop consent
  dialog. Support Wayland first. Treat X11 fallback as a separate compatibility provider, not
  as permission bypass.

The client must report capture permission state and provider diagnostics to the server without
capturing frames when permission is denied. Do not make an employee grant a broad accessibility
permission as a substitute for screen-capture permission.

Suggested client abstractions:

- `IScreenViewAuthorization`
- `IScreenCaptureSource`
- `IScreenCapturePublisher`
- `ILiveViewSession`
- `ScreenShareIndicatorService`

Register one platform implementation through the existing OS guards and DI conventions. Keep
capture off unless a server-issued, currently valid view lease is present.

## 5. Session and token flow

1. Admin opens `/live-stream`, selects an employee, enters a reason, and chooses a bounded
   duration.
2. Web calls `POST /api/v1/live-view/sessions` with target employee, reason, and requested
   duration.
3. Go service checks web authentication, RBAC, employee scope, MFA/re-auth freshness, policy
   limits, and whether the target client is online.
4. Server creates a `live_view_session` in `REQUESTED` state and sends a wake/request signal
   through the existing authenticated client control channel. If a control channel does not
   exist, add one; do not pollute the existing telemetry sync endpoints.
5. Employee client receives the request, verifies the server signature/lease, displays the
   indicator and OS consent UI, and only then publishes.
6. Server changes the session to `ACTIVE`, mints a short-lived publisher token for the client
   and a separate subscriber token for the requesting admin. Tokens should expire in minutes,
   be scoped to one room/identity, and permit only publish or subscribe as appropriate.
7. Admin web joins the room using the subscriber token. The API never proxies media bytes.
8. Any stop action, lease expiry, employee stop, disconnect, token revocation, or policy event
   closes the room and writes an immutable audit event. The client stops its capture source and
   releases native resources.

Recommended state machine:

```text
REQUESTED -> DENIED
REQUESTED -> APPROVED -> STARTING -> ACTIVE
ACTIVE -> STOPPING -> ENDED
ACTIVE -> REVOKED
ACTIVE -> EXPIRED
ACTIVE -> FAILED
```

Use an idempotency key on start/stop requests. A background server worker must expire sessions
even if the browser disappears. Revoke media tokens and disconnect the room on expiry; a UI
timeout alone is not sufficient.

## 6. Data model and API surface

Add a migration for a server-side `live_view_sessions` table with at least:

- `id`, `employee_id`, `requested_by_user_id`, `approved_by_user_id` (nullable)
- `status`, `reason`, `requested_at`, `approved_at`, `started_at`, `ended_at`, `expires_at`
- `end_reason`, `room_name` (opaque/non-guessable), `client_device_id`
- `created_at`, `updated_at`, and a retention/deletion marker if policy requires one

Add an append-only `live_view_audit_events` table or a shared audit facility with:

- actor, target, action, outcome, reason/session ID, timestamp, request correlation ID
- enough network/device context for investigation, minimized according to the privacy policy

Suggested endpoints:

- `POST /api/v1/live-view/sessions`
- `GET /api/v1/live-view/sessions` (server-side pagination/infinite scroll)
- `GET /api/v1/live-view/sessions/:id`
- `POST /api/v1/live-view/sessions/:id/stop`
- `POST /api/v1/live-view/sessions/:id/approve` if approval is required
- `POST /api/v1/live-view/sessions/:id/deny`
- `POST /api/v1/live-view/sessions/:id/token` (short-lived, policy-checked token only)
- client control messages for request/approve/stop/heartbeat

Do not add screen frames to PostgreSQL, SQLite telemetry tables, object storage, or the current
sync batch endpoints. If recording is later approved, design a separate encrypted media storage
and retention system.

## 7. Performance and reliability targets

Start conservatively and measure on representative employee hardware:

- Default 1280x720, 10-15 fps, target 1-2 Mbps; allow adaptive reduction to 640x360.
- H.264 hardware encoding where available; VP8 fallback for compatibility.
- Cap one active publisher per employee and one or two authorized viewers per session.
- Client CPU target: under 5% sustained on a typical supported machine; memory target under
  150 MB incremental. Measure, do not assume.
- Target glass-to-glass latency under 2 seconds on a normal network.
- Pause/stop publishing when the viewer leaves, the lease expires, the screen locks, or the
  client loses authorization.
- Use WebRTC congestion control, TURN for restrictive networks, and regional SFUs.
- Never block the existing activity collector or sync worker on capture/network work. Use a
  bounded channel and cancellation tokens; stopping must complete promptly.
- Backpressure must reduce bitrate/frame rate, never queue unlimited frames.
- Health metrics: active rooms, join latency, RTT, packet loss, bitrate, encoder CPU, failed
  consent, denied requests, unexpected disconnects, and expiry/stop latency.

## 8. Security hardening

- HTTPS/WSS only; TLS certificates managed and rotated.
- Short-lived signed room tokens, audience/room/identity claims, publish/subscribe grants,
  nonce-based room names, and server-side revocation.
- Bind the publisher identity to the authenticated employee device and lease ID.
- Do not trust employee IDs supplied by the client; derive identity from the authenticated
  device/session.
- Apply CSRF protection to cookie-authenticated control endpoints and strict origin checks.
- Use secure/httpOnly cookies for web auth, existing refresh-token rotation, MFA, and re-auth.
- Do not expose SFU admin APIs to the browser. Put the SFU behind private networking/firewall
  rules; only the required signaling/media ports are public.
- Redact tokens and room names from logs. Add automated secret-scanning and dependency scanning.
- Review LiveKit/libwebrtc native binaries, licenses, CVEs, update cadence, and SBOM before
  production approval.
- Add rate limits and abuse detection for start, token, stop, and failed authorization calls.

## 9. Where this fits in the current codebase

All file placement follows `FILE_HIERARCHY.md` ownership and the naming conventions in
`AGENTS.md` §6 (PascalCase C#, camelCase TypeScript, PascalCase Go exports, camelCase JSON).

### Client

- Add capture/publisher services under `client/Services/LiveView/`.
- Add platform providers under `client/Platform/Windows`, `client/Platform/Linux`, and
  `client/Platform/MacOS` using the repository's established partial-file pattern. Guard every
  platform method body with `OperatingSystem.IsWindows/Linux/MacOS()` — do NOT propagate
  `[SupportedOSPlatform]` through cross-platform partial/background-service call graphs
  (Cross-Platform Analyzer Safety Rule).
- Add configuration gates (`ALPHA_LIVE_VIEW_ENABLED`, max duration, max resolution, max fps) to
  `.env`, `.env.example`, config printing (`--print-config`), and the encrypted installer config
  path. Per the **Installer-Parity Rule**, these vars MUST be in `.env` BEFORE
  `encrypt-config.sh` runs — `dotnet run` reads `.env` directly, but installed builds ship
  `config.enc` baked at build time.
- Add the employee indicator to the existing Avalonia shell/tray. It must work in headless mode
  (`--background`) and must not depend on opening the GUI.
- Add a dedicated control channel; do not reuse telemetry payloads or the 11 sync endpoints.
- Branding: the indicator text and any new UI strings must resolve through `Core/AppInfo.cs`
  (the `APP_IDENTIFIERS` + `VERSION` single source) — never hardcoded literals.

### Server

- Add live-view handler/service/repository packages and sequential migration files in
  `server/migrations/`.
- Add server-side RBAC checks (using the existing `roles`/`modules`/`submodules` catalog from
  migration 025) and employee scope checks before issuing any token. Permission keys follow the
  `employee-journey/live-view/*` convention.
- Add an expiry/revocation worker and an audit writer.
- Add SFU token generation using the provider's Go SDK and keep the SFU secret server-side.
- Add a control-channel transport that is authenticated, reconnectable, idempotent, and bounded.
- All new endpoints go under `/api/v1/live-view/` and use the existing `dto.APIError` error
  response format. Register routes in `cmd/server/main.go` before existing `:id` routes (safe
  ordering convention).

### Web

- Replace the current placeholder in `web/src/app/(app)/live-stream/page.tsx` only after the
  backend contract exists.
- **URL-Synced Filters Rule:** the session history page must keep filter state (employee,
  date range, status) in the browser URL query string via `useUrlQueryState` or
  `useUrlActivityFilter`. The search input uses a local debounced mirror (~400 ms); date
  modals have no Clear button.
- **Web Infinite-Scroll Rule:** session history and audit history tables MUST use
  `useInfiniteQuery` with an IntersectionObserver sentinel. Previous/Next buttons are forbidden.
- **Server-Projected Flags Rule:** any boolean that depends on a cross-table relationship (e.g.
  "has this employee accepted live-view T&C?") must be projected in the server query, not
  reconstructed client-side.
- Use the LiveKit browser client for playback, not a custom `<img>` polling loop.
- Show request state, employee indicator state, timer, reason, connection quality, and an
  unmistakable Stop button. Never autoplay audio because audio is not part of this feature.
- Add an audit/history view with permissions separate from live viewing where policy requires it.
- Wrap any component using `useSearchParams` in a `<Suspense>` boundary.

## 10. Dependency and deployment recommendation

### Initial dependency decision

1. Prototype the media plane with self-hosted LiveKit in a staging environment.
2. Validate a maintained native .NET publisher binding for the exact target OS versions. If no
   binding is production-quality, create a small native bridge around the official libwebrtc/
   LiveKit native SDK per platform rather than selecting an abandoned all-in-one NuGet package.
   Review the binding's license, CVE history, update cadence, and SBOM impact before commitment.
3. Keep the browser dependency to the LiveKit JavaScript client and the server dependency to
   the LiveKit Go SDK. Pin versions and generate an SBOM.
4. Do not add FFmpeg, GStreamer, or a software JPEG pipeline for the first interactive release.
   Consider GStreamer only if a measured platform lacks a supported WebRTC capture/encode path.

### Operations

- Deploy API and SFU separately so media load cannot starve API requests.
- Use TURN (coturn or managed equivalent), region-aware SFUs, health checks, capacity limits,
  and a documented failover plan.
- Monitor SFU and API metrics without recording media.
- Test NAT/firewall, VPN, proxy, Wayland permissions, macOS TCC, Windows capture permission,
  lock/unlock, sleep/resume, fast client restart, token expiry, and server restart.

### Verification (per the Installer-Parity Rule)

A successful `dotnet run` is NOT release evidence. Every phase must be verified against these
checks before proceeding:

| Service | Required checks |
|---------|----------------|
| **Client** | `dotnet build` (0 warnings/0 errors); for cross-platform analyzer changes, non-incremental build must not exceed 2× baseline duration or approach 1 GB compiler memory |
| **Server** | `go build` + `go vet` clean |
| **Web** | `npx tsc --noEmit` clean, `next build` passes |
| **Cross-service** | All affected services pass; serialized contracts (DTO ↔ API client ↔ TypeScript types) are consistent |
| **Installer** | Build the platform installer (`bash publish/build-installer.sh -b linux`), install the artifact, run the new functionality from the installed binary. Report clearly when installation could not be completed. |

New env vars must be added to `.env` BEFORE `encrypt-config.sh` runs. Config changes now
auto-propagate via `EnvLoader` replacing stale user-config copies, but the initial bake is
mandatory.

## 11. Delivery phases

### Phase 0 - Governance and feasibility

- Complete DPIA, policy/notice, lawful-basis review, RBAC matrix, retention decision, and threat
  model.
- Build a capture-only lab prototype for Windows, macOS, Wayland, and X11.
- Measure CPU, memory, latency, bandwidth, and battery impact on minimum supported hardware.
- **Per-feature Terms & Conditions system** — implement the client-side T&C framework (see
  section 14) so every feature (browser journey, app usage, live stream, future features)
  carries its own consent gate. This must be in place before any feature that requires explicit
  employee consent ships.

### Phase 0.5 - Control Channel RFC

The control channel is the most architecturally novel piece in this feature — it does not
exist in the current codebase and is the dependency for everything else (request, approve,
stop, heartbeat, indicator state). Prototype it in isolation before touching WebRTC.

**Scope of this phase:**

1. **Define the transport.** Evaluate options against the existing codebase constraints:
   - **WebSocket (recommended starting point):** persistent authenticated connection from
     employee client to server. Reconnectable, bounded, supports server-push (request signal)
     and client-push (heartbeat, stop, indicator state). The .NET client already has HTTP
     infrastructure; a single `System.Net.Websockets.ClientWebSocket` connection with
     exponential backoff reconnect is minimal new dependency.
   - **gRPC bidirectional stream:** stronger contract, code-gen, but heavier for a .NET
     client that currently has zero gRPC dependencies. Consider only if WebSocket proves
     inadequate.
   - **Long-poll on existing sync endpoints:** rejected — the plan explicitly says "do not
     pollute the existing telemetry sync endpoints," and long-poll adds latency to request
     delivery.

2. **Define the message contract.** All messages are JSON envelopes with:
   ```
   { "type": "...", "id": "<uuid>", "timestamp": "...", "payload": {...} }
   ```
   Message types (server → client):
   - `view.request` — admin wants to view; carries session ID, reason, duration, expiry
   - `view.approve` — server approves (carries room token, SFU URL)
   - `view.deny` — server denies
   - `view.stop` — server forces stop (expiry, revocation, policy)
   - `view.heartbeat` — server liveness check, client must respond within N seconds

   Message types (client → server):
   - `view.accept` — employee accepts, client ready to publish
   - `view.deny` — employee declines
   - `view.stop` — employee stops sharing
   - `view.heartbeat.ack` — liveness response
   - `view.status` — permission state, capture provider diagnostics
   - `view.error` — capture failed, permission denied, etc.

   Every message carries an idempotency key (`id` field). Duplicate delivery is safe.

3. **Define the connection lifecycle:**
   - Client connects on startup (after login, after `StartTracking()`).
   - Client authenticates the WebSocket with the same JWT/device token used for sync.
   - Server validates and associates the connection with the employee's device.
   - Client sends heartbeat every 30s; server expects ack within 10s.
   - On disconnect, client reconnects with exponential backoff (1s → 2s → 4s → … → 30s max).
   - Server marks the device "offline" after 2 missed heartbeats; no view requests are sent
     to offline devices.
   - On reconnect, client sends a `sync` message with the last received message ID; server
     replays any missed state changes (idempotent replay from in-memory buffer, bounded to
     last 5 minutes).

4. **Prototype in isolation:**
   - Server: new `control_channel` package with `WebSocketHandler`, `ConnectionRegistry`
     (in-memory map of employee_id → active connection), `MessageBus` (route messages by
     type), and `HeartbeatMonitor` (goroutine per connection).
   - Client: new `ControlChannelService` (BackgroundService) with `WebSocketClient`,
     `MessageHandler` dispatch, and `HeartbeatSender`.
   - No WebRTC, no SFU, no screen capture in this phase. Just the channel + messages.
   - Test: server sends `view.request` → client logs it and responds `view.accept` → server
     logs it. Proves the full round-trip works.

5. **Failure modes to test before Phase 1:**
   - Client disconnects mid-request (server must not leave session in limbo).
   - Server restarts while a view is active (client must reconnect and report state).
   - Multiple connections from the same device (server rejects duplicates).
   - Malformed/unknown message types (server logs and ignores, client is resilient).
   - Token expiry during an active WebSocket (server closes; client reconnects with fresh token).

**Exit criteria for Phase 0.5:**
- Control channel works end-to-end in `dotnet run` against the Go server.
- Heartbeat + reconnect + idempotent message delivery verified.
- Message contract documented and reviewed.
- No screen capture, no WebRTC, no SFU — just the channel.

### Phase 1 - Secure internal pilot

- Implement one-to-one live-only WebRTC viewing, explicit employee indicator/consent, bounded
  leases, server-side RBAC, audit events, expiry, stop/revoke, and no recording.
- Deploy to an isolated staging tenant with synthetic or consenting test users.
- Complete security review, dependency/SBOM review, and installed-build testing.

### Phase 2 - Controlled production rollout

- Enable by tenant/department feature flag, with a small operator group.
- Review audit logs and support incidents weekly; tune bitrate and duration limits.
- Add regional SFU/TURN capacity only after measured demand.

### Phase 3 - Expansion

- Add multi-viewer or manager approval only if a new policy review supports it.
- Treat recording, audio, mobile clients, and unattended access as separate projects.

## 12. What this plan does NOT cover

The following are explicitly out of scope. If any is proposed, treat it as a separate project
requiring its own plan, DPIA, and approval process:

- **Recording, replay, screenshots, or archival** of any screen content.
- **Audio capture** — microphone, system audio, or speaker output.
- **Webcam capture** — camera feeds are unrelated to screen viewing.
- **Keyboard logging, clipboard capture, or input recording.**
- **Multi-viewer sessions** — more than one admin viewing simultaneously.
- **Manager approval workflows** — a second-level approver before a view starts.
- **Unattended access** — viewing when the employee is away from the machine.
- **Mobile client support** — iOS/Android screen capture and viewing.
- **AI-based analysis** of screen content (OCR, activity classification, etc.).

## 13. Definition of done

- Counsel/DPIA and employee notice approved for every deployment jurisdiction.
- Per-feature T&C accepted by the target employee (server-side check on `terms_consent`).
- No stream can start without a server-authorized lease, required consent/indicator, and a
  valid control-channel connection to the target device.
- Unauthorized admins cannot request, subscribe, or mint tokens.
- Stop, expiry, revoke, logout, lock, disconnect, and server restart terminate publishing.
- No media frames or raw tokens are persisted in application databases or normal logs.
- Audit records are complete, tamper-resistant to ordinary admins, queryable, and retained per
  policy.
- CPU, memory, bandwidth, latency, and battery targets are met on the support matrix.
- API, SFU, TURN, client, and web failure modes have tested recovery behavior.
- `dotnet build` (0 warnings/0 errors), `go build`/`go vet` clean, `npx tsc --noEmit` clean,
  `next build` passes. Focused integration and security tests added.
- The feature is verified from installed client artifacts on every supported platform
  (Installer-Parity Rule). `dotnet run` alone is insufficient.
- Cross-platform analyzer safety: no `[SupportedOSPlatform]` propagation through
  background-service call graphs; every platform method guarded with
  `OperatingSystem.IsWindows/Linux/MacOS()`.
- New env vars baked into `config.enc` before installer build.

## 14. Per-feature Terms & Conditions — Client-side consent framework

Every feature that collects, transmits, or exposes employee data must have its own
Terms & Conditions that the employee reads and explicitly accepts before the feature activates.
This is not a single generic "I agree to be monitored" checkbox — each feature carries its
own T&C because each feature has different data scope, retention, and access characteristics.

### Why per-feature, not one blanket T&C

- Browser journey tracking (URLs, titles) has different privacy implications than app usage
  duration (which apps, how long). A single blanket notice is legally weak — it does not
  satisfy purpose limitation under GDPR Art. 5(1)(b) or similar regulations.
- Live screen viewing is fundamentally different from passive telemetry. An employee who
  accepted app-duration tracking has NOT consented to being watched live.
- Future features (file journeys, location, screenshots) each need their own scope-limited
  notice. The framework must scale to features that don't exist yet.
- Jurisdiction-specific requirements vary per feature. Some features may require explicit
  opt-in (live view), others may operate under legitimate interest with an opt-out (basic
  app usage). Per-feature T&C makes this enforceable.

### Feature registry

Define a `FeatureTermsRegistry` in the client that maps each feature to its T&C metadata:

```text
FeatureTermsEntry {
    FeatureId:         string          // e.g. "browser_journey", "app_usage", "live_view"
    DisplayName:       string          // human-readable name for the modal title
    Description:       string          // what the feature does (plain language)
    TermsVersion:      string          // semver, bumped when legal text changes
    TermsText:         string          // the full T&C text (or a file path / resource key)
    IsRequired:        bool            // true = employee MUST accept to use the app at all
                                      // false = employee can decline; feature is disabled
    CanRevoke:         bool            // true = employee can revoke acceptance later
    RevokeEffect:      string          // what happens on revoke (e.g. "browser journey tracking stops")
    MinimumAcceptedVersion: string     // if stored version < this, re-acceptance required
}
```

Features register themselves at startup. The first set:

| FeatureId        | IsRequired | CanRevoke | Notes                                              |
|------------------|------------|-----------|-----------------------------------------------------|
| `app_usage`     | true       | false     | Core tracking — app open/close duration. Required for the app to function. |
| `browser_journey` | false    | true      | URL and title tracking. Employee can decline; browser tracking is disabled. |
| `live_view`     | false      | true      | Live screen viewing. Employee can decline; live view requests are rejected. |
| `file_journey`  | false      | true      | File explorer tracking. Employee can decline; file event bus is disabled. |
| *(future)*      | —          | —         | Every new feature adds a row to the registry.       |

### Storage

- Accepted T&C records are stored in SQLite `app_status` table as key-value pairs:
  `terms_accepted_{featureId} = { version, acceptedAt, revokedAt? }`.
- On first launch (no `app_status` rows), the framework initializes and shows the required
  T&C modals before any tracking starts.
- The server receives the accepted versions in the sync payload so it can verify consent
  state during audit — but the server does NOT gate feature activation (the client is the
  authority for local consent; the server is the authority for server-side access like
  live-view requests).

### Modal behavior — standalone window, not inside the GUI

The T&C modal is a **separate Avalonia window**, not embedded in the existing
`MainWindow.axaml` GUI shell. Reasons:

1. The GUI may not be open (headless `--background` mode). T&C must still be enforceable.
2. The modal must be **uncloseable** until the employee accepts or declines. A separate
   window with no close button, no Alt+F4, and no tray interaction is simpler to enforce
   than modal state inside a complex router.
3. The modal must appear **before any tracking starts** — even in `--background` mode.

**Launch sequence:**

```
Program.cs startup
  -> ILogStore.InitializeAsync()
  -> FeatureTermsRegistry.InitializeAsync()   // reads app_status for accepted versions
  -> if (required T&C not accepted)
       -> FeatureTermsModal.Show()             // blocking, separate window
       -> employee accepts or declines
       -> write to app_status
  -> StartTracking()                           // only after required T&C accepted
  -> --background mode: same flow, but the modal appears as a system-level window
     (on Linux: use XDG activation or a minimal GTK/Qt dialog; on Windows: WinForms/WPF
      MessageBox-style; on macOS: NSAlert). The Avalonia window is NOT created in
      --background mode — platform-native dialogs are used instead.
```

**Modal UI (GUI mode):**

- Full-screen or large centered window, no close button, no escape key, no Alt+F4.
- Title: feature display name (e.g. "Browser Journey Tracking").
- Body: the T&C text in readable formatting (scrollable if long).
- Footer: two buttons — "I Accept" (enabled only after scrolling to bottom, if text is
  long) and "I Decline" (only for non-required features; for required features, only
  "I Accept" is shown — declining means the app exits).
- Version number displayed ("Terms v1.2.0").
- On accept: write `{ version: "1.2.0", acceptedAt: <now> }` to `app_status`, close the
  modal, proceed to next T&C or to tracking.
- On decline (non-required): feature is disabled, tracking continues without it. A small
  indicator in the dashboard shows "Browser Journey: Declined — click to review".

**Re-acceptance on version bump:**

When a new client build ships with a higher `MinimumAcceptedVersion` for a feature, the
stored version is below the minimum → the modal re-appears on next launch. The employee
cannot use the feature (or the app, if required) until they accept the updated terms.

**Revoke flow (non-required features):**

- Employee opens the client GUI → Settings/Privacy → per-feature toggle.
- Toggling OFF shows a confirmation: "This will stop [feature]. You can re-enable it
  anytime." On confirm: `revokedAt` is set in `app_status`, feature stops.
- Toggling ON shows the T&C modal again → re-acceptance required.

### Headless (`--background`) mode handling

In `--background` mode there is no Avalonia window. Platform-specific dialog calls are
guarded with `OperatingSystem.IsWindows/Linux/MacOS()` — each platform has its own native
dialog implementation, and the cross-platform call site must never propagate
`[SupportedOSPlatform]` through the `BackgroundService` call graph.

The approach per platform:

- **Windows:** Use `System.Windows.Forms.MessageBox` (requires `System.Windows.Forms`
  reference, which is available in .NET 10). The MessageBox is modal, blocks the thread,
  and cannot be closed without clicking a button. Set topmost.
- **Linux:** Use `zenity --text-info --filename=<terms.txt> --checkbox="I Accept"` or
  `kdialog --textinfo <terms.txt> --checkbbox "I Accept"`. These are modal system dialogs
  that work without a display server in many environments. Fallback: if no display is
  available (SSH/headless server), log a warning and skip the feature (required features
  should still block — document this as a known limitation for server-only installs).
- **macOS:** Use `osascript` with `display dialog` — modal, blocks, cannot be dismissed
  without clicking.

If the platform dialog is unavailable (no display, no zenity, etc.) and the T&C is
required: the client logs a FATAL error and exits. The employee must run the GUI at least
once to accept terms. This is intentional — required consent must not be silently bypassed.

### Data model addition

New client SQLite migration adds nothing — the existing `app_status` key-value table
stores T&C state. New server migration adds a `terms_consent` table for audit:

```sql
CREATE TABLE terms_consent (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    employee_id     VARCHAR(20) NOT NULL REFERENCES employees(employee_id),
    feature_id      TEXT NOT NULL,
    terms_version   TEXT NOT NULL,
    action          TEXT NOT NULL CHECK (action IN ('accepted', 'revoked', 're_accepted')),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (employee_id, feature_id, terms_version, action)
);
```

Server sync endpoint: `POST /api/v1/terms-consent/sync` — follows the existing 11 sync endpoint
conventions (`{employeeId, token, entries: [...]}` body, JWT auth, upsert on conflict). The
client sends consent events on every sync pass so the server audit trail stays current. The
server does NOT use this table to gate features — it is append-only for compliance evidence.

### Codebase integration

All file placement follows `FILE_HIERARCHY.md` and the naming conventions in `AGENTS.md` §6.

- `client/Core/TermsAndConditions/FeatureTermsRegistry.cs` — feature registration and
  version checking.
- `client/Core/TermsAndConditions/TermsModal.cs` — standalone window / platform dialog
  launcher. Platform-specific dialog calls guarded with `OperatingSystem.IsWindows/Linux/MacOS()`
  (Cross-Platform Analyzer Safety Rule).
- `client/Core/TermsAndConditions/TermsConsentStore.cs` — read/write `app_status`.
- `client/Services/TermsConsentSyncService.cs` — sync consent events to server (can be
  part of the existing `SyncService` loop, not a separate BackgroundService). Follows the
  existing sync payload conventions (`{employeeId, token, entries: [...]}`).
- `server/internal/repository/terms_consent_repo.go` — append-only insert + list.
- `server/internal/handler/terms_consent_handler.go` — sync endpoint.
- `server/migrations/0XX_terms_consent.sql` — the table above.
- `web/src/app/(app)/settings/privacy/page.tsx` — employee-facing T&C status and revoke
  controls (reads from `GET /api/v1/terms-consent?employeeId=` endpoint). Uses
  `useUrlQueryState` for filter state (URL-Synced Filters Rule) and `useInfiniteQuery` for
  any list view (Web Infinite-Scroll Rule).
- Config gates: `ALPHA_TERMS_BROWSER_JOURNEY_ENABLED`, `ALPHA_TERMS_LIVE_VIEW_ENABLED`,
  etc. — feature-level kill switches independent of T&C (a feature can be code-complete
  but terms-gated). Added to `.env`, `.env.example`, `AppConfig`, and `--print-config`.

### Relationship to live stream feature

For live screen viewing specifically, the T&C must be accepted BEFORE a live-view session
can start. The server checks `terms_consent` for `feature_id = 'live_view'` when processing
`POST /api/v1/live-view/sessions` — if the target employee has not accepted (or has revoked),
the request is rejected with a clear error: "Employee has not accepted the Live Viewing
terms." This is a server-side enforcement, not just client-side — even if a compromised
client tried to start publishing, the server would refuse to mint tokens.