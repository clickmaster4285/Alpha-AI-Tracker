# Live Employee Screen Viewing - Legal and Production Plan

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

### Client

- Add capture/publisher services under `client/Services/LiveView/`.
- Add platform providers under `client/Platform/Windows`, `client/Platform/Linux`, and
  `client/Platform/MacOS` or the repository's established partial-file pattern.
- Add configuration gates such as `ALPHA_LIVE_VIEW_ENABLED`, max duration, max resolution, and
  max fps to `.env`, `.env.example`, config printing, and the encrypted installer config path.
- Add the employee indicator to the existing Avalonia shell/tray. It must work in headless mode
  and must not depend on opening the GUI.
- Add a dedicated control channel; do not reuse telemetry payloads or the 11 sync endpoints.

### Server

- Add live-view handler/service/repository packages and sequential migrations.
- Add server-side RBAC checks and employee scope checks before issuing any token.
- Add an expiry/revocation worker and an audit writer.
- Add SFU token generation using the provider's Go SDK and keep the SFU secret server-side.
- Add a control-channel transport that is authenticated, reconnectable, idempotent, and bounded.

### Web

- Replace the current placeholder in `web/src/app/(app)/live-stream/page.tsx` only after the
  backend contract exists.
- Use the existing URL-synced filter and infinite-scroll rules for employee/session history.
- Use the LiveKit browser client for playback, not a custom `<img>` polling loop.
- Show request state, employee indicator state, timer, reason, connection quality, and an
  unmistakable Stop button. Never autoplay audio because audio is not part of this feature.
- Add an audit/history view with permissions separate from live viewing where policy requires it.

## 10. Dependency and deployment recommendation

### Initial dependency decision

1. Prototype the media plane with self-hosted LiveKit in a staging environment.
2. Validate a maintained native .NET publisher binding for the exact target OS versions. If no
   binding is production-quality, create a small native bridge around the official libwebrtc/
   LiveKit native SDK per platform rather than selecting an abandoned all-in-one NuGet package.
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
- Rebuild and verify the installed client on every supported platform per the repository's
  Installer-Parity Rule. A successful `dotnet run` is not release evidence.

## 11. Delivery phases

### Phase 0 - Governance and feasibility

- Complete DPIA, policy/notice, lawful-basis review, RBAC matrix, retention decision, and threat
  model.
- Build a capture-only lab prototype for Windows, macOS, Wayland, and X11.
- Measure CPU, memory, latency, bandwidth, and battery impact on minimum supported hardware.

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

## 12. Definition of done

- Counsel/DPIA and employee notice approved for every deployment jurisdiction.
- No stream can start without a server-authorized lease and required consent/indicator.
- Unauthorized admins cannot request, subscribe, or mint tokens.
- Stop, expiry, revoke, logout, lock, disconnect, and server restart terminate publishing.
- No media frames or raw tokens are persisted in application databases or normal logs.
- Audit records are complete, tamper-resistant to ordinary admins, queryable, and retained per
  policy.
- CPU, memory, bandwidth, latency, and battery targets are met on the support matrix.
- API, SFU, TURN, client, and web failure modes have tested recovery behavior.
- `go build`/`go vet`, `npx tsc --noEmit`, `next build`, and `dotnet build` pass, plus focused
  integration/security tests.
- The feature is verified from installed client artifacts on every supported platform.