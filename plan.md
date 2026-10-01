# Live Stream V3.0 — WebRTC/WebSocket Reliability & Scale Plan

> **Status:** Plan only — no code changed.
> **Branch:** `feature/live_streamV3.0`
> **Scope:** client (`client/`), server (`server/`), web (`web/`)
> **Baseline:** V2.0 shipped 2026-09-29 — Pion SFU (VP8 RTP relay) + dedicated presence WS.
> The JPEG frame relay is confirmed removed with no dead code remaining.
> **Governing rules:** `AGENTS.md` §6, `prompt.md`.
> **Supersedes:** the V2 presence-WS plan, archived at `plan.v2-presence-ws.archived.md`.
> **Track progress in:** §7 Execution checklist.

---

## 1. Assessment summary

### 1.1 What is already correct

The V2 architecture is sound and should not be redesigned:

| Decision                                                        | Verdict                                                                |
| --------------------------------------------------------------- | ---------------------------------------------------------------------- |
| One`TrackLocalStaticRTP` per room (not per subscriber)        | Correct — publisher read once, Pion fans out per subscriber DTLS/SRTP |
| 1500-byte RTP buffer hoisted out of the read loop               | Zero per-packet allocation on the hottest path                         |
| Single-writer goroutine per watch socket +`writeMu`           | No interleaved frame corruption                                        |
| `subOfferGen` generation counters + pointer-identity checks   | A superseded offer/PC cannot clobber its replacement                   |
| `trackReady` gate closed-then-recreated under `sync.Once`   | Waiters unblock exactly once; no lost wakeup                           |
| Non-blocking drop-oldest control channel (`cap 4`)            | Hub cannot be stalled by a slow watcher                                |
| 10 s reaper + shutdown`Close()`                               | No unbounded map growth                                                |
| 128-bit one-shot watch ticket, 60 s TTL, burned on use          | Correct (browsers drop cookies on cross-port WS to`:8080`)           |
| Presence hub with`atomic.UInt64` gen-scoped unregister        | A stale teardown cannot evict a newer connection                       |
| 2 s PLI keepalive                                               | Keeps keyframes flowing without full renegotiation                     |
| `ScreenVp8Encoder` working around the SIPSorcery bitrate wipe | Correct — diagnosed from live encode logs                             |

**Conclusion:** the design is production-grade. The defects below are implementation bugs, not
architectural problems. Do **not** replace the SFU.

### 1.2 Current scale ceiling

All streaming state is **process-local and in-memory**. There is no Redis pub/sub, no shared
registry, and no sticky-session assumption anywhere in the streaming or presence paths.

- A watcher on instance B **cannot** see a publisher on instance A — the RTP track physically
  lives in instance A's `sfuRoom`. `Subscribe` writes to a `ctrl` channel with no reader
  (`m.clientConnected == false`, so the send is skipped) and the watcher fails after the 12 s
  `subscriberTrackWait` with `withTrack=false`.
- `ConsumeWatchTicket` on instance B cannot find a ticket minted on instance A.
- `ws.Hub.conns` presence is per-instance, so `/live-stream/employees` reports false negatives.

Effective ceiling on a single box:

```
1 publisher PC ≈ 12 Mbps @ 1080p/12fps (ALPHA_STREAM_MAX_BITRATE_KBPS default)
MaxStreams=25 × MaxWatchersPerEmployee=10 = 250 subscriber PCs
250 × 12 Mbps ≈ 3 Gbps egress — a NIC limit, not a code limit
```

---

## 2. Findings

Severity: 🔴 data loss / corruption · 🟠 resource leak or degradation · 🟡 efficiency · 🟢 hygiene

| #    | Sev  | Location                                                                             | Finding                                                                                                                                                                                                                                                                                                                  |
| ---- | ---- | ------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| F1   | 🔴   | `client/Services/Streaming/ScreenVp8Encoder.cs:145-155`, `:182-188`              | `VpxImgWrap` overwrites the `VpxImgAlloc`'d plane pointers with a **pinned managed** buffer while `_imgAllocated` stays `true`, so `DisposeEncoderUnlocked` calls `VpxImgFree` on a GC-heap pointer. Runs on **every** encoder dispose (every `stop`, every restart, every teardown). |
| F2   | 🟠   | `client/Services/LiveStreamClient.cs:292-336`                                      | `iceReady` is completed only after `ws.SendAsync(offerJson)`. If that send throws, the `finally` closes the PC but every pending `onicecandidate` continuation stays parked on `WaitAsync(CancellationToken.None)`, retaining `pc` + `ws` + closure. One dead task per failed publish.                     |
| F3   | 🟠   | `web/src/lib/useLiveStreamSocket.ts:114-136`                                       | `cleanupPc` nulls `srcObject` and closes the PC but never calls `stream.getTracks().forEach(t => t.stop())` nor `video.pause()`. `ontrack` can fire again after a re-offer and silently overwrite the prior stream. Compounds at `LIVE_STREAM_THEATER_MAX_TILES = 16`.                                       |
| F4   | 🟠   | `server/internal/middleware/device_auth.go:51-54`                                  | Background goroutine reads`c.Request().Context()` after the handler returns and captures the pooled Echo `c` by reference. On a WS upgrade the handler returns when the socket ends, so `TouchLastSeen` races cancellation. Data race + one goroutine per device request.                                          |
| F5   | 🟠   | `server/internal/stream/stream_handler.go:64`, `ws_handler.go:43`                | `CheckOrigin` returns `true` when `allowedOrigins` is empty — fail-open CSWSH on both sockets if `CORS_ALLOWED_ORIGINS` resolves to a zero-length slice.                                                                                                                                                        |
| F5b  | 🟠   | `server/internal/stream/sfu.go`                                                    | Configured`MaxBitrateKbps` is parsed and logged but **never enforced**. A buggy or hostile client can encode at any rate.                                                                                                                                                                                        |
| F6   | 🟠   | `client/Services/LiveStreamClient.cs:274-279`                                      | Bitrate is fixed once at construction.`ScreenVp8Encoder.TargetKbps` **has** a correct runtime setter that rebuilds the libvpx context, but nothing calls it. No congestion control: RTCP/TWCC feedback never reaches the encoder.                                                                                |
| F7   | 🟡   | `server/internal/stream/sfu.go:269`                                                | `forwardRTP` reads into a fixed 1500-byte buffer with no truncation check. An oversized RTP packet is silently corrupted (`remote.Read` returns full length; short `n` is written).                                                                                                                                |
| F8   | 🟡   | `server/internal/stream/sfu.go:280-289`                                            | `sendPLI` is `for range ticker.C` with no `select` on a done channel; relies on `WriteRTCP` erroring after close. Bounded (~2 s) but not by design.                                                                                                                                                              |
| F8b  | ⓘ   | `server/internal/stream/stream_handler.go:378`                                     | The watch ticket's`UserID` is fetched then discarded (`if _, err := ...`). No per-watcher identity retained, so "who watched what" is not auditable.                                                                                                                                                                 |
| F9   | 🟡   | `server/internal/stream/stream_handler.go:397`                                     | `Subscribe` runs **after** `Upgrade`, so `ErrTooManyWatchers` writes an error frame on a live socket instead of returning HTTP 429.                                                                                                                                                                          |
| F10  | 🟡   | `server/internal/stream/stream_handler.go:511`, `:539`                           | Unbounded goroutine churn per`offer` (each may spawn another), each living up to `subscriberTrackWait` (12 s). `subOfferGen` prevents damage but not existence. `Hub.tickets` also has no hard cap between 10 s reaps.                                                                                           |
| F11  | 🟡   | `client/Services/ScreenCaptureService.cs:268-329` + `ScreenVp8Encoder.cs:81,169` | ~100–200 MB/s of LOH garbage at 12 fps (Bitmap + scaled Bitmap + 1–2`byte[]` + I420 + encoded). No pooling.                                                                                                                                                                                                          |
| F12  | 🟡   | `client/Services/ScreenCaptureService.cs:137`, `:268`                            | Synchronous GDI`CopyFromScreen` + bicubic downscale + `LockBits`/`Marshal.Copy` blocks a thread-pool thread for the whole capture. No dedicated thread, no `TaskCreationOptions.LongRunning`.                                                                                                                    |
| F13  | 🟡   | `client/Services/ScreenCaptureService.cs:167`                                      | Adaptive FPS is**CPU-only** (drops to a hard floor of 8 fps at >90 % of the capture budget). Network congestion is invisible to it, and it has no resolution lever.                                                                                                                                                |
| F14  | 🟢   | `web/src/lib/useLiveStreamSocket.ts:249-251`                                       | The 300 ms post-`onopen` timer is untracked and never cleared on unmount. Guarded by `cancelled`, so benign.                                                                                                                                                                                                         |
| F14b | 🟢   | `client/Services/ScreenCaptureService.cs:121-122`                                  | The 200 ms idle poll runs forever on Windows even when`ALPHA_STREAM_ENABLED=false`.                                                                                                                                                                                                                                    |
| F14c | 🟢   | `server/internal/stream/stream_handler.go:20-23`                                   | No token re-validation on open sockets; a device token valid at upgrade stays valid for the connection's life (liveness-bounded only).                                                                                                                                                                                   |
| F19  | ℹ️ | `server/internal/router/router.go:250,252`                                         | Pre-existing, unrelated:`/terms-consent/check` registered twice; the second is dead. Report only.                                                                                                                                                                                                                      |

---

## 3. Slow-client behaviour (design gap, not a bug)

**The stream does not degrade gracefully on a constrained uplink.** There is no congestion
control. On a 2 Mbps uplink with a 12 Mbps encode:

1. **Keyframe burst saturates the pipe.** Observed I-frames are ~50–270 KB. At
   `keyframeEvery = 12` (1 I-frame/sec at 12 fps), a 270 KB I-frame needs ~1.1 s to clear
   2 Mbps. Everything queues behind it, every second.
2. **Queue growth is accidentally survivable.** `ScreenCaptureService`'s channel is
   `BoundedChannelFullMode.DropOldest` at capacity 1, so when `pc.SendVideo` blocks the pump
   stops reading and frames self-drop. This prevents unbounded memory growth but yields a
   **slideshow with ~1 s gaps**, not a lower-quality stream.
3. **Adaptive FPS does not help** — it measures capture cost, not network cost (F13).
4. **No PLI response.** The server sends PLI every 2 s (F8), but SIPSorcery does not map an
   incoming PLI onto the hand-rolled encoder, so `ForceKeyFrame` never fires on demand. Recovery
   depends solely on the blind `frameIndex % keyframeEvery` counter.
5. **STUN only by default.** `WEBRTC_TURN_URLS` is empty. Behind CGNAT / enterprise firewall /
   symmetric NAT, ICE fails outright — not degraded, **no stream at all**. Adding TURN doubles
   the bandwidth (client→TURN→server), worsening marginal links.
6. **`GLagInFrames = 0`** (`ScreenVp8Encoder.cs:107`) disables altref frames — a latency call
   that costs real compression efficiency on screen content, which is altref's best case.

| Client uplink | Current behaviour                                                    |
| ------------- | -------------------------------------------------------------------- |
| > 20 Mbps     | Fine                                                                 |
| 5–20 Mbps    | Watchable, intermittent stalls                                       |
| 1–5 Mbps     | Slideshow, ~1 s gaps, appears frozen                                 |
| < 1 Mbps      | Effectively broken; CPU still pegged encoding frames nobody receives |
| Behind CGNAT  | No connection at all (no TURN)                                       |

The worst property: the client keeps burning CPU capturing and encoding at full rate regardless,
and never asks whether anyone is receiving.

---

## 3b. Findings registered as TODO comments (V3 deferral)

The items below were identified during analysis and deliberately **deferred**, with a tracking
comment left at each site. They are not scheduled for implementation in this cycle.

| #   | Sev | Location                                                   | Deferred finding                                                                      |
| --- | --- | ---------------------------------------------------------- | ------------------------------------------------------------------------------------- |
| D1  | 🟠  | `client/Services/LiveStreamClient.cs:274-279`            | Bitrate fixed at construction;`TargetKbps` setter unused → no congestion control   |
| D1b | 🟠  | `server/internal/stream/sfu.go`                          | `MaxBitrateKbps` configured but never enforced                                      |
| D2  | 🟡  | `server/internal/stream/sfu.go:269`                      | `forwardRTP` 1500-byte buffer has no truncation guard                               |
| D3  | 🟡  | `server/internal/stream/sfu.go:280-289`                  | `sendPLI` has no cancellation channel                                               |
| D4  | 🟡  | `server/internal/stream/stream_handler.go:397`           | `Subscribe` after `Upgrade` → error frame instead of HTTP 429                    |
| D5  | ⓘ  | `server/internal/stream/stream_handler.go:378`           | Watch-ticket`UserID` discarded → watchers not auditable                            |
| D6  | 🟡  | `server/internal/stream/stream_handler.go:511`, `:539` | Unbounded per-offer goroutines; uncapped`Hub.tickets`                               |
| D7  | 🟡  | `client/Services/ScreenCaptureService.cs:268-329`        | ~100–200 MB/s LOH garbage; no buffer pooling                                         |
| D8  | 🟡  | `client/Services/ScreenCaptureService.cs:137`            | Blocking GDI on a thread-pool thread; no dedicated thread                             |
| D9  | 🟡  | `client/Services/ScreenCaptureService.cs:167`            | Adaptive FPS is CPU-only; no resolution lever                                         |
| D10 | 🟠  | `client/Services/Streaming/ScreenVp8Encoder.cs:145-155`  | `VpxImgFree` on a managed pointer (heap corruption) — **fixed in Phase 0.1** |
| D11 | 🟡  | `server/internal/router/router.go:250,252`               | Duplicate`/terms-consent/check` route                                               |

---

## 4. Execution phases

Phases are ordered by real-world harm, not by implementation convenience. Each phase is
independently shippable.

### Phase 0 — Safety (before the next installer build)

Surgical edits. No streaming behaviour change. Covers F1–F5 and the 🟢 hygiene items.

| #    | Change                                                                                                                                                              | File                                                            |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------- |
| 0.1  | Remove the `VpxImgAlloc` call and the `_imgAllocated` branch; rely on `VpxImgWrap` alone so no managed pointer is ever freed (F1/D10)              | `client/Services/Streaming/ScreenVp8Encoder.cs`               |
| 0.1b | Add a `// TODO(V3): ...` guard comment at `sfu.go` `MaxBitrateKbps` so the unenforced cap is not mistaken for working enforcement              | `server/internal/stream/sfu.go`                               |
| 0.2  | Complete `iceReady` in a `finally`; pass the session `ct` instead of `CancellationToken.None` (F2)                                         | `client/Services/LiveStreamClient.cs`                         |
| 0.3  | Stop prior `MediaStream` tracks and `video.pause()` in cleanup and before `ontrack` overwrite (F3)                                           | `web/src/lib/useLiveStreamSocket.ts`                          |
| 0.4  | Capture the `onopen` timer ref and clear it in cleanup (F14)                                                                                   | `web/src/lib/useLiveStreamSocket.ts`                          |
| 0.5  | Copy `device.ID` out of the Echo context and use `context.Background()` with an explicit timeout in the `TouchLastSeen` goroutine (F4)          | `server/internal/middleware/device_auth.go`                   |
| 0.6  | Make `CheckOrigin` **fail closed** when `allowedOrigins` is empty (F5)                                                                         | `server/internal/stream/stream_handler.go`, `ws_handler.go` |
| 0.7  | Check `Subscribe` capacity **before** `Upgrade` → real HTTP 429 (F9/D4)                                                                       | `server/internal/stream/stream_handler.go`                    |
| 0.8  | Give `sendPLI` a done channel tied to publisher close (F8/D3)                                                                                  | `server/internal/stream/sfu.go`                               |
| 0.9  | Skip the 200 ms idle poll when streaming is disabled (F14b)                                                                                   | `client/Services/ScreenCaptureService.cs`                     |

**Exit criteria:** `dotnet build` 0 warnings / 0 errors; `go build` + `go vet` clean;
`npx tsc --noEmit` clean; `next build` passes.

**Verification note (F1):** a source build cannot catch F1 — it is a runtime native-heap fault.
Correctness must be argued from the libvpx contract (`VpxImgFree` is only valid on an
`VpxImgAlloc`'d, never-wrapped image), not from a clean build. State this honestly in handoff.

### Phase 1 — Slow-network resilience (§3)

Do **1.1 first** — every other item is unverifiable without it.

| #    | Change                                                                                                                                                                                                                                                | File                                                                                       |
| ---- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------ |
| 1.1  | **Client-side stream telemetry.** Report measured uplink, selected bitrate, real send rate, encode ms, dropped-frame count via `app_status`.                                                                                                  | `client/Services/LiveStreamClient.cs`                                                    |
| 1.1b | Share the employee poll across tabs; `refetchInterval: 1_000` with `staleTime: 0` is 2 HTTP req/sec **per open tab**.                                                                                                                        | `web/src/app/(app)/live-stream/page.tsx`, `(live-popout)/live-stream/theater/page.tsx` |
| 1.2  | **Uplink probe before publishing.** `ALPHA_STREAM_MIN_UPLINK_KBPS` (default 2500). Small upload benchmark at boot; below the floor → do not publish, log the reason explicitly. Converts "silent broken slideshow" into a diagnosable state. | new `NetProbeService`; `LiveStreamClient`                                               |
| 1.2b | Read `ALPHA_STREAM_MIN_UPLINK_KBPS` in `AppConfig`, clamp, and expose it in `--print-config`; add the key to `.env` + `.env.example`.                                                                                                        | `client/Configuration/AppConfig.cs`, `client/.env`                                     |
| 1.3  | **Network-aware bitrate ladder.** Choose encoder bitrate from the measured uplink tier instead of `ALPHA_STREAM_MAX_BITRATE_KBPS` unconditionally (D1).                                                                                       | `client/Services/LiveStreamClient.cs`                                                    |
| 1.3b | Enforce `MaxBitrateKbps` server-side via `RTPSender.SetMaxBitrate()` on subscriber senders (D1b/F5b).                                                                                                                                              | `server/internal/stream/sfu.go`                                                          |
| 1.3c | Add a truncation guard to `forwardRTP` (D2/F7).                                                                                                                                                                                                      | `server/internal/stream/sfu.go`                                                          |
| 1.4  | **Adaptive degradation on send backpressure.** Time `pc.SendVideo`; on sustained blocking step down bitrate → fps → resolution, recovering slowly. Reuses the existing correct `TargetKbps` setter (D1).                                  | `client/Services/LiveStreamClient.cs` (`MediaPumpAsync`)                               |
| 1.4b | **Resolution lever** in adaptive degradation — drop resolution before dropping below the usable fps floor (D9/F13).                                                                                                                            | `client/Services/ScreenCaptureService.cs`                                                |
| 1.5  | **Map server PLI → `ForceKeyFrame()`** so packet loss costs one I-frame, not one second.                                                                                                                                                     | `ScreenVp8Encoder` + `LiveStreamClient`                                                |
| 1.5b | Buffer pooling to remove the ~100–200 MB/s of LOH garbage (D7/F11).                                                                                                                                                                                  | `ScreenCaptureService`, `ScreenVp8Encoder`                                             |
| 1.5c | Dedicated capture thread (`LongRunning`) instead of blocking GDI on a pool thread (D8/F12).                                                                                                                                                         | `client/Services/ScreenCaptureService.cs`                                                |
| 1.6  | **Evaluate altref re-enable** (`GLagInFrames`) or A-Q mode. Screen content is altref's best case. Requires an A/B on a real stream.                                                                                                           | `client/Services/Streaming/ScreenVp8Encoder.cs`                                          |
| 1.6b | Retain the watch-ticket `UserID` for auditability (D5/F8b).                                                                                                                                                                                          | `server/internal/stream/stream_handler.go`                                               |
| 1.6c | Cap `Hub.tickets` and bound the per-offer goroutines (D6/F10).                                                                                                                                                                                       | `hub.go`, `stream_handler.go`                                                          |
| 1.7  | **Require TURN in production.** Warn at startup when `WEBRTC_TURN_URLS` is empty; document the 2× relay cost; prefer time-limited HMAC `turnCredentials` over a public open relay.                                                         | `server/internal/config/config.go`, `server/.env.example`                              |
| 1.8  | Periodic token re-validation or a max socket lifetime (F14c).                                                                                                                                                                                         | `server/internal/stream/`                                                                |

⚠️ **Installer parity:** Phase 1 adds or changes `ALPHA_STREAM_*` keys → `client/.env` must be
edited **before** `publish/encrypt-config.sh`, and `config.enc` re-baked. `dotnet run` reads
plaintext `.env` and will misleadingly appear configured.

### Phase 2 — Horizontal scale (only when a second instance is required)

Do not start here. Single-instance is fully functional today.

- **Short term (~15 min, no code):** sticky sessions at the LB (NGINX `ip_hash` or cookie
  affinity). Un-breaks cross-instance watching without touching the media path.
- **Real fix:** Redis pub/sub for presence + ticket routing, plus a shared
  `employeeId → instanceId` registry so watch sockets land on the box holding the RTP.
  Note the media path itself still cannot be split without a real SFU (mediasoup / LiveKit).
  `server/internal/ws/hub.go` and `stream/hub.go` are the touch points.

### Phase 3 — Server-side hardening (deferred from the V2.1 review)

Not scheduled this cycle:

- Socket lifetime cap / token re-validation cadence (F14c).
- Presence accuracy re-verification against the DB heartbeat fallback.

---

## 5. Verification matrix

| Phase | Checks                                                                                                                                                                                        |
| ----- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 0     | `dotnet build` (0/0) · `go build` · `go vet` · `npx tsc --noEmit` · `next build`                                                                                                |
| 1     | `dotnet build` (0/0) + **installed-build** verification per the Installer-Parity Rule; live uplink-throttle test (throttle the client NIC to 2 Mbps and confirm graceful degradation) |
| 2     | Two-instance test: publish on A, watch on B                                                                                                                                                   |
| 3     | `go build` · `go vet`                                                                                                                                                                    |

---

## 6. Known gaps carried forward

- Linux screen capture remains unimplemented (Windows GDI only) — unchanged by this plan.
- Zero automated tests across all three services. Every phase is verified by build + manual
  test only.
- TURN server provisioning is an operational prerequisite for Phase 1.7, not a code change.
- F1 cannot be proven fixed by any build; it requires an installed-build runtime test.

---

## 7. Execution checklist

Tick boxes as work lands. Order is mandatory within each phase. A phase is only complete when
its **Gate** passes — do not tick the gate from a partial run.

### Pre-flight (before any edit)

- [ ] Confirm branch is `feature/live_streamV3.0` and the working tree has no unrelated edits
- [ ] `git stash list` reviewed — do not disturb the user's in-flight work
- [ ] Read `AGENTS.md` §6 (mandatory rules) for the service being touched
- [ ] Re-read the target file end-to-end before editing; do not patch from memory

### Phase 0 — Safety

- [x] **0.1** Drop `VpxImgAlloc` + `_imgAllocated` branch; `VpxImgWrap` only — `ScreenVp8Encoder.cs`
- [x] **0.1b** Add `// TODO(V3)` note on the unenforced `MaxBitrateKbps` — `sfu.go`
- [x] **0.2** `iceReady` completed in `finally`; session `ct` not `CancellationToken.None` — `LiveStreamClient.cs`
- [x] **0.3** `getTracks().forEach(t => t.stop())` + `video.pause()` in cleanup and before `ontrack` overwrite — `use-live-stream-socket.ts`
- [x] **0.4** `onopen` timer stored in a ref and cleared in cleanup — `use-live-stream-socket.ts`
- [x] **0.5** `device.ID` copied out of Echo ctx; `context.Background()` + timeout in the `TouchLastSeen` goroutine — `device_auth.go`
- [x] **0.6** `CheckOrigin` fails **closed** on empty `allowedOrigins` — `stream_handler.go`, `ws_handler.go`
- [x] **0.7** `Subscribe` capacity checked **before** `Upgrade` → HTTP 429 — `stream_handler.go`
- [x] **0.8** `sendPLI` takes a done channel tied to publisher close — `sfu.go`
- [x] **0.9** 200 ms idle poll skipped when streaming disabled — `ScreenCaptureService.cs`
- [x] **GATE 0** `dotnet build` 0 warnings / 0 errors
- [x] **GATE 0** `go build` clean · `go vet` clean
- [x] **GATE 0** `npx tsc --noEmit` clean · `next build` passes

> ⚠️ **F1 is not provable by build.** A green `dotnet build` does **not** mean the heap-corruption
> fix is correct. It needs an installed-build start/stop/restart cycle. Do not report Phase 0
> as verified on build evidence alone.

### Phase 1 — Slow-network resilience

- [x] **1.1** Stream telemetry → `app_status` (uplink, bitrate, real send rate, encode ms, drops) — **do this first**
- [x] **1.1b** Employee poll shared across console + theater tabs
- [x] **1.2** Uplink probe; `ALPHA_STREAM_MIN_UPLINK_KBPS` floor; skip publish + log reason
- [x] **1.2b** `ALPHA_STREAM_MIN_UPLINK_KBPS` in `AppConfig` (clamped) + `--print-config` + `.env` + `.env.example`
- [x] **1.3** Network-aware bitrate ladder from measured uplink
- [x] **1.3b** Server-side bitrate enforcement (token-bucket on `forwardRTP`; Pion v4 has no `SetMaxBitrate`)
- [x] **1.3c** `forwardRTP` truncation guard
- [x] **1.4** Adaptive degradation on send backpressure (bitrate → fps → resolution)
- [x] **1.4b** Resolution lever drops before the fps floor
- [x] **1.5** Server PLI → `ForceKeyFrame()` (via `force_keyframe` ctrl)
- [x] **1.5b** Frame-buffer pooling (reuse Bitmaps + scratch; kill LOH churn from per-frame Bitmap alloc)
- [x] **1.5c** Dedicated capture thread (`LongRunning`)
- [x] **1.6** altref / A-Q — `ALPHA_STREAM_VP8_LAG_FRAMES` default 1 (revert with env=0)
- [x] **1.6b** Watch-ticket `UserID` retained for audit
- [x] **1.6c** `Hub.tickets` capped; per-offer goroutines bounded
- [x] **1.7** Startup warning when `WEBRTC_TURN_URLS` empty; TURN ops note in `.env.example`
- [x] **1.8** Token re-validation cadence or socket lifetime cap
- [x] **GATE 1** `dotnet build` 0/0 + **source-build** verified (Installer-Parity: re-bake `config.enc` + installed start/stop still required for F1)
- [ ] **GATE 1** `config.enc` re-baked **after** `.env` edited — installer ships the new keys
- [ ] **GATE 1** Live throttle test: NIC capped to 2 Mbps → graceful degradation, no crash, log explains why
- [ ] **GATE 1** Multi-watcher room test (4 tiles + theater) — no reconnect storm

### Phase 2 — Horizontal scale (only when a 2nd instance is actually needed)

- [ ] Sticky sessions enabled at the LB (NGINX `ip_hash` / cookie affinity)
- [ ] Two-instance test: publish on **A**, watch on **B** — media renders
- [ ] Redis pub/sub wired for presence (`ws.Hub`)
- [ ] Redis pub/sub wired for watch-ticket routing (`stream.Hub`)
- [ ] Shared `employeeId → instanceId` registry; watch socket lands on the RTP holder
- [ ] Decide: keep Pion SFU, or migrate to mediasoup / LiveKit (only justified at this stage)
> Deferred — single VPS remains the deploy shape; do not start until a second instance is required.

### Phase 3 — Server hardening (deferred)

- [ ] Socket lifetime cap / token re-validation cadence (F14c)
- [ ] Presence accuracy re-verified against the DB heartbeat fallback

### Deferred / TODO-comment registry (§3b)

- [ ] D1 · D1b · D2 · D3 · D4 · D5 · D6 · D7 · D8 · D9 — reviewed, scheduled, or closed with rationale
- [ ] D10 — closed by **0.1**
- [ ] D11 — duplicate `/terms-consent/check` route reported (do **not** fix in this cycle)

### Final handoff

- [ ] Diff reviewed for unrelated changes, secrets, and generated artifacts
- [ ] Docs updated: `AGENTS.md` changelog, `client/ARCHITECTURE.md` / `server/ARCHITECTURE.md` / `web/ARCHITECTURE.md`
- [ ] `plan.md` ticked boxes match reality — no box ticked from a partial run
- [ ] Handoff states, honestly: **source build verified** vs **installer built** vs **installed artifact verified**
- [ ] No commit / push / branch / PR unless explicitly requested
