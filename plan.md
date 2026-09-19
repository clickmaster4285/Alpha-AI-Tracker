# Live Stream — Implementation Plan

| | |
|---|---|
| **Status** | **Phase 1 COMPLETE.** **Phase 2 IN PROGRESS** — WebRTC + ICE/TURN distribution landed; deploy coturn for corp NAT next |
| **Branch** | `feature/live_stream` |
| **Created** | 2026-09-18 |
| **Updated** | 2026-09-19 |
| **Scope** | Windows only for capture. **Linux / macOS skipped** (stay `streamAvailable: false`) |
| **Privacy** | Preview only — no recording, no disk, no DB storage of frames |
| **Version** | Client **1.2.0** (Phase 1 ship) |

**Driver:** Admin selects an employee → sees that employee's screen live.
Consent: `featureId = "live_view"` — re-checked before any media flows.

**Access control:** Existing RBAC module `live-stream` (sidebar + RouteGuard). No per-employee watch ACL — **neglected / N/A**.

---

## Phase 1 — COMPLETE ✅

Shipped: WS JPEG relay, Windows GDI capture, consent, online rail (3 min), multi-monitor picker, migrations 040/041, installer 1.2.0.

| Deliverable | Status |
|-------------|--------|
| Hub + push/watch WS + employees REST | ✅ |
| Windows capture + push client | ✅ |
| Web `/live-stream` UI + monitor dropdown | ✅ |
| Consent `live_view` | ✅ |
| Online window 3 min | ✅ |
| Multi-monitor (`select_monitor`) | ✅ |
| Linux / macOS capture | **Skipped** — unavailable UI only |
| Who-can-watch RBAC | **N/A** — sidebar RBAC sufficient |

Phase 1 architecture (kept as control plane for Phase 2):

```
Client (Win) ──WS control + JPEG──▶ Server Hub (in-memory) ──JPEG──▶ Web canvas
```

JPEG path remains a **fallback** until WebRTC is proven; do not delete Phase 1 code until WebRTC is the default and stable.

---

## Phase 2 — ACTIVE (WebRTC + Redis)

### Goals

1. **WebRTC media** — replace JPEG binary frames with real-time video (lower latency, better quality, congestion control).
2. **Redis-backed hub** — share wanted/streaming/watcher state (+ optional signaling fan-out) across multiple Go nodes.
3. **Keep Phase 1 control semantics** — `start` / `stop` / watcher count / consent / employees list / monitor select stay the same ideas; only the **media transport** changes.

### Non-goals (this phase)

| Item | Status |
|------|--------|
| Linux / PipeWire / portal capture | **Skipped** |
| Per-employee watch ACL | **Skipped** |
| Recording / DVR / frame storage | **Never** (privacy) |

---

### Track A — WebRTC media path

#### Decision (committed direction)

| Piece | Choice | Why |
|-------|--------|-----|
| Topology | **SFU (server-mediated)**, not true P2P | Corporate NAT; employee PC is outbound-only; admin never dials the PC |
| Signaling | Reuse existing **push + watch WebSockets** for SDP/ICE JSON | Avoid a third socket type; auth already solved |
| Media | Client encodes screen → publishes to SFU; admins subscribe | Same activity gate: no encode when watcher count = 0 |
| Server stack (preferred) | **pion** WebRTC in Go (or embed **LiveKit** if ops prefers a dedicated media service) | Fits Echo monorepo; LiveKit is the escape hatch if pion SFU effort balloons |
| TURN | Coturn (or cloud TURN) behind env config | Required for restrictive corp networks |
| Codec | H.264 preferred (hardware where available); VP8 fallback | Browser + Windows encode reality |

#### Target architecture

```
┌─────────────┐  WS signaling (SDP/ICE)   ┌──────────────────┐  WS signaling   ┌─────────┐
│ Client Win  │ ─────────────────────────▶│ Go API + Hub     │◀───────────────│ Web     │
│ capture+enc │                           │ Redis state      │                │ RTCPeer │
└──────┬──────┘                           └────────┬─────────┘                └────┬────┘
       │ RTP / WebRTC                              │                               │
       └──────────────▶ SFU (pion or LiveKit) ◀────┴───────────────────────────────┘
                         media never hits Postgres / disk
```

#### Wire additions (signaling over existing sockets)

| Message | Who | Meaning |
|---------|-----|---------|
| `{"type":"offer","sdp":"…"}` | client ↔ server ↔ web | SDP offer |
| `{"type":"answer","sdp":"…"}` | … | SDP answer |
| `{"type":"ice","candidate":…}` | … | ICE trickle |
| `{"type":"media","mode":"webrtc"|"jpeg"}` | server → both | Negotiate / fallback |
| Existing `start` / `stop` / `select_monitor` | unchanged semantics | Capture lifecycle |

JPEG binary frames stay supported until `LIVE_STREAM_MEDIA=webrtc` is default and verified.

#### Client work

| File / area | Change |
|-------------|--------|
| Capture | Keep GDI/monitor select; feed frames to encoder instead of (or in addition to) JPEG |
| Encoder | Windows hardware H.264 if available; software fallback |
| WebRTC publish | .NET WebRTC stack TBD (SIPSorcery / native interop) — spike first |
| `LiveStreamClient` | Signaling messages + publish lifecycle tied to `start`/`stop` |

#### Web work

| File / area | Change |
|-------------|--------|
| `useLiveStreamSocket` → `useLiveStream` | Handle SDP/ICE; attach `MediaStream` to `<video>` (canvas only for JPEG fallback) |
| UI | Same states; optional “Connecting media…” while ICE completes |

#### Server work

| File / area | Change |
|-------------|--------|
| `stream` package | Signaling relay + SFU session per employee (or LiveKit room bridge) |
| Config | `LIVE_STREAM_MEDIA`, TURN URLs/creds, SFU limits |
| Consent / caps | Same gates as Phase 1 on session create |

#### Implementation order (Track A)

1. **Spike** — pion SFU hello-world: one publisher, one subscriber, localhost (no Redis yet).
2. **Signaling** — map offer/answer/ICE onto push + watch sockets; keep JPEG path.
3. **Client encode + publish** — Windows only; feature-flagged.
4. **Web `<video>` consumer** — fallback to JPEG if WebRTC fails.
5. **TURN + prod nginx** — UDP/TCP TURN; document firewall ports.
6. **Default WebRTC** — JPEG as emergency fallback only.
7. **Installer parity** — native deps / config.enc; ship-test Windows installed build.

---

### Track B — Redis-backed hub

#### Why

In-memory hub is single-node. Multiple API replicas break watcher counts and “wanted” signals unless state is shared.

#### Decision

| Piece | Choice |
|-------|--------|
| Store | Existing Redis (already used for employee secrets) |
| What in Redis | Per-employee: `wanted`, `watcher_count`, `stream_available`, `selected_monitor`, `client_connected`, last-capability JSON |
| Pub/sub | Channel `live_stream:events` for start/stop/select_monitor / signaling fan-out across nodes |
| What stays local | SFU media sockets stick to one node (**sticky sessions** or dedicated media process) |
| Mailbox (JPEG era) | Either sticky node for JPEG fan-out, or drop JPEG once WebRTC is default |

#### Config

| Key | Purpose |
|-----|---------|
| `LIVE_STREAM_HUB=memory\|redis` | Default `memory` until redis path verified |
| `REDIS_*` | Reuse existing Redis connection |
| `LIVE_STREAM_REDIS_PREFIX` | Key namespace (default `live_stream:`) |

#### Implementation order (Track B)

1. Extract hub interface (`WantStream`, `UnwantStream`, `SetCapability`, …) if not already clean.
2. Implement `RedisHub` + pub/sub; unit/integration tests against local Redis.
3. Dual-run: `LIVE_STREAM_HUB=redis` on staging with 2 Go processes + sticky LB.
4. Document: media node sticky requirement when WebRTC SFU is in-process.

**Recommended sequence:** Track A spike (1–2) first on single node → then Track B → then full WebRTC default. Redis without WebRTC still helps multi-node JPEG; WebRTC without Redis stays single-node.

---

### Phase 2 status board

| Track | Status | Notes |
|-------|--------|-------|
| Multi-monitor (from earlier) | ✅ Done | |
| WebRTC — pion SFU rooms | ✅ Landed | `server/internal/stream/sfu.go` |
| WebRTC — signaling on push/watch | ✅ Landed | offer / answer / ice |
| WebRTC — web `<video>` + JPEG fallback | ✅ Landed | `useLiveStreamSocket` dual path |
| Redis presence mirror | ✅ Landed | `LIVE_STREAM_HUB=redis` |
| WebRTC — Windows publish/encode | ✅ Landed | SIPSorcery VP8 + BGRA from GDI; `webrtcCapable=true` on Windows |
| TURN / prod ICE distribution | ✅ Landed | `iceServers` on start/status → client + web |
| Default `LIVE_STREAM_MEDIA` | ✅ `both` | JPEG fallback kept; set `webrtc` when TURN soak done |
| Deploy TURN (coturn) in prod | ⬜ **Next** | Configure `LIVE_STREAM_ICE_SERVERS` + TURN user/pass on VPS |
| Linux capture | **Skipped** | |
| Who-can-watch RBAC | **N/A** | |

---

## Privacy (unchanged)

- No Postgres / disk for frames or RTP.
- Media ephemeral in SFU memory / kernel buffers only.
- Logs: session start/stop / ICE failures — never media payloads.
- Consent on every new publish/subscribe session.

---

## Env sketch (Phase 2 additions)

| Key | Default | Purpose |
|-----|---------|---------|
| `LIVE_STREAM_MEDIA` | `jpeg` → later `webrtc` | Active media mode |
| `LIVE_STREAM_HUB` | `memory` → later `redis` | Hub backend |
| `LIVE_STREAM_TURN_URLS` | empty | Comma-separated TURN URIs |
| `LIVE_STREAM_TURN_USER` / `LIVE_STREAM_TURN_PASS` | empty | TURN creds (or short-lived tokens later) |
| `LIVE_STREAM_REDIS_PREFIX` | `live_stream:` | Redis key prefix |

Client: keep `ALPHA_STREAM_*`; add encoder / WebRTC flags when spike chooses the stack.

---

## Risks (Phase 2)

| Risk | Mitigation |
|------|------------|
| .NET WebRTC encode immature | Spike early; LiveKit egress / native helper as backup |
| Corp firewall blocks UDP | TURN over TCP/TLS; document ports |
| Multi-node SFU affinity | Sticky LB or separate media service |
| JPEG + WebRTC dual path complexity | Feature flag; delete JPEG path only after soak |
| Redis split-brain watcher counts | Single writer patterns + pub/sub ack; idle reap still authoritative |

---

## FAQ

**Q: Is Phase 1 done?**  
**A: Yes.** Windows WS JPEG live preview + multi-monitor is complete. Linux stays skipped.

**Q: What is Phase 2?**  
**A: WebRTC media + Redis hub**, keeping the same consent/start/stop/employees UX.

**Q: Does WebRTC remove Online polling?**  
**A: Not by itself.** Rail Online can later prefer `client_connected` from the hub/Redis; independent of media codec.

**Q: Start where?**  
**A: WebRTC works.** Default media is `both`. ICE/TURN from env is pushed to desktop + browser on start/status. Next: deploy coturn (or cloud TURN) and set `LIVE_STREAM_ICE_SERVERS` + `LIVE_STREAM_TURN_USER` / `PASS` on the VPS; then optionally `LIVE_STREAM_MEDIA=webrtc`.  
Note: `SIPSorcery` 8.0.23 has NuGet advisory warnings — plan a package upgrade after soak.
