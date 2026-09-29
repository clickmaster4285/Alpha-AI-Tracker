using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;
using client.Services.Streaming;

namespace client.Services;

/// <summary>
/// WebRTC publisher to the server SFU via GET /api/v1/live-stream/push (DeviceAuth signaling).
/// Captures only after the server sends {"type":"start"}; stops on {"type":"stop"}.
/// Independent of <see cref="WsClient"/> (presence) — this socket is media signaling only.
/// </summary>
public sealed class LiveStreamClient : BackgroundService
{
    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly ScreenCaptureService _capture;
    private readonly ILogger<LiveStreamClient> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public LiveStreamClient(
        ILogStore store,
        AppConfig config,
        ScreenCaptureService capture,
        ILogger<LiveStreamClient> logger)
    {
        _store = store;
        _config = config;
        _capture = capture;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.StreamEnabled)
        {
            _logger.LogInformation("LiveStreamClient parked (ALPHA_STREAM_ENABLED=false)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        var backoffSec = 2;
        while (!stoppingToken.IsCancellationRequested)
        {
            EmployeeInfo? employee = null;
            try { employee = await _store.GetEmployeeInfoAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "LiveStreamClient: employee lookup failed"); }

            if (employee is null ||
                (string.IsNullOrWhiteSpace(employee.DeviceToken) && string.IsNullOrWhiteSpace(employee.Token)))
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await RunSessionAsync(employee, stoppingToken);
                backoffSec = 2;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "LiveStreamClient session ended; reconnect in {Sec}s " +
                    "(403/consent_required means accept live_view terms on the client)",
                    backoffSec);
            }
            finally
            {
                _capture.SetStreamActive(false);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(backoffSec), stoppingToken); }
            catch (OperationCanceledException) { break; }
            backoffSec = Math.Min(30, backoffSec * 2);
        }
    }

    private async Task RunSessionAsync(EmployeeInfo employee, CancellationToken ct)
    {
        var wsUrl = BuildWsUrl(_config.ServerUrl);
        if (wsUrl is null)
        {
            _logger.LogWarning("LiveStreamClient: ALPHA_SERVER_URL missing or invalid");
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return;
        }

        using var ws = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(employee.DeviceToken))
            ws.Options.SetRequestHeader("Authorization", $"Device {employee.DeviceToken}");
        else
            ws.Options.SetRequestHeader("Authorization", $"Bearer {employee.Token}");

        _logger.LogInformation("LiveStreamClient connecting to {Url}", wsUrl);
        await ws.ConnectAsync(new Uri(wsUrl), ct);

        await SendHelloAsync(ws, ct);

        List<RTCIceServer> iceServers = new()
        {
            new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
        };

        RTCPeerConnection? pc = null;
        ScreenVp8Encoder? encoder = null;
        CancellationTokenSource? mediaCts = null;
        Task? mediaPump = null;
        var buffer = new byte[64 * 1024];
        var messageBuf = new MemoryStream(8 * 1024);

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                }
                catch (WebSocketException ex)
                {
                    _logger.LogDebug(ex, "LiveStreamClient receive ended");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    messageBuf.SetLength(0);
                    continue;
                }

                messageBuf.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var json = Encoding.UTF8.GetString(messageBuf.GetBuffer(), 0, (int)messageBuf.Length);
                messageBuf.SetLength(0);

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("type", out var typeProp))
                        continue;
                    var type = typeProp.GetString();

                    switch (type)
                    {
                        case "ice_servers":
                            iceServers = ParseIceServers(doc.RootElement);
                            break;

                        case "start":
                            _logger.LogInformation("LiveStreamClient: start capture (WebRTC)");
                            try
                            {
                                await StopMediaAsync(pc, encoder, mediaCts, mediaPump);
                                (pc, encoder, mediaCts, mediaPump) = await StartPublisherAsync(ws, iceServers, ct);
                            }
                            catch (Exception ex)
                            {
                                // Keep the push signaling socket alive — a one-shot
                                // encode/WebRTC failure must not reconnect-storm.
                                _logger.LogError(ex, "LiveStreamClient: failed to start WebRTC publisher");
                                await StopMediaAsync(pc, encoder, mediaCts, mediaPump);
                                pc = null;
                                encoder = null;
                                mediaCts = null;
                                mediaPump = null;
                                _capture.SetStreamActive(false);
                                try
                                {
                                    var err = JsonSerializer.Serialize(new { type = "error", code = "publisher_start_failed" }, JsonOpts);
                                    await ws.SendAsync(Encoding.UTF8.GetBytes(err), WebSocketMessageType.Text, true, ct);
                                }
                                catch { /* ignored */ }
                            }
                            break;

                        case "stop":
                            _logger.LogInformation("LiveStreamClient: stop capture");
                            await StopMediaAsync(pc, encoder, mediaCts, mediaPump);
                            pc = null;
                            encoder = null;
                            mediaCts = null;
                            mediaPump = null;
                            _capture.SetStreamActive(false);
                            break;

                        case "select_monitor":
                        {
                            var idx = 0;
                            if (doc.RootElement.TryGetProperty("index", out var idxProp) &&
                                idxProp.ValueKind == JsonValueKind.Number)
                                idx = idxProp.GetInt32();
                            var applied = _capture.SetSelectedMonitor(idx);
                            _logger.LogInformation("LiveStreamClient: select_monitor → {Index}", applied);
                            await SendHelloAsync(ws, ct);
                            break;
                        }

                        case "answer":
                            if (pc is not null && doc.RootElement.TryGetProperty("sdp", out var sdpProp))
                            {
                                var sdp = sdpProp.GetString();
                                if (!string.IsNullOrEmpty(sdp))
                                {
                                    var answer = new RTCSessionDescriptionInit
                                    {
                                        type = RTCSdpType.answer,
                                        sdp = sdp,
                                    };
                                    var setResult = pc.setRemoteDescription(answer);
                                    if (setResult != SetDescriptionResultEnum.OK)
                                        _logger.LogWarning("LiveStreamClient setRemoteDescription: {Result}", setResult);
                                }
                            }
                            break;

                        case "ice":
                            if (pc is not null)
                                ApplyRemoteIce(pc, doc.RootElement);
                            break;

                        case "error":
                            _logger.LogWarning("LiveStreamClient server error: {Json}", json);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "LiveStreamClient: bad signaling message (keeping socket)");
                }
            }
        }
        finally
        {
            await StopMediaAsync(pc, encoder, mediaCts, mediaPump);
            _capture.SetStreamActive(false);
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch { /* ignored */ }
            }
        }
    }

    private async Task<(RTCPeerConnection pc, ScreenVp8Encoder encoder, CancellationTokenSource mediaCts, Task mediaPump)>
        StartPublisherAsync(ClientWebSocket ws, List<RTCIceServer> iceServers, CancellationToken ct)
    {
        _capture.SetStreamActive(true);

        var config = new RTCConfiguration { iceServers = iceServers };
        var pc = new RTCPeerConnection(config);
        // ScreenVp8Encoder fixes the SIPSorcery Vp8Codec bug that wiped TargetKbps.
        var kbps = (uint)Math.Clamp(_config.StreamMaxBitrateKbps, 500, 15000);
        var keyframeEvery = Math.Max(1, _config.StreamFps * Math.Max(1, _config.StreamKeyframeIntervalSec));
        var encoder = new ScreenVp8Encoder
        {
            TargetKbps = kbps,
            Fps = (uint)Math.Clamp(_config.StreamFps, 1, 30),
            KeyframeMaxDistance = (uint)keyframeEvery,
        };
        encoder.ForceKeyFrame();
        _logger.LogInformation(
            "LiveStreamClient VP8 encode bitrate={Kbps}kbps fps={Fps} maxWidth={MaxW} keyframeEvery={Kf}frames (~{Sec}s) [ScreenVp8Encoder]",
            kbps, _config.StreamFps, _config.StreamMaxWidth, keyframeEvery, _config.StreamKeyframeIntervalSec);
        var mediaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // ctor is (codec, formatID=RTP PT, clockRate) — NOT (codec, clockRate).
        // Passing 90000 as formatID throws (max PT is 127) and killed the push WS.
        var videoTrack = new MediaStreamTrack(
            new VideoFormat(VideoCodecsEnum.VP8, 96, 90000),
            MediaStreamStatusEnum.SendOnly);
        pc.addTrack(videoTrack);

        pc.onconnectionstatechange += (state) =>
        {
            _logger.LogInformation("LiveStreamClient PC state → {State}", state);
        };

        // ICE can fire during setLocalDescription — before we send the offer.
        // Hold sends until the offer is on the wire so the SFU has a publisher PC.
        var iceReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        pc.onicecandidate += async (cand) =>
        {
            if (cand is null || ws.State != WebSocketState.Open)
                return;
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "ice",
                    candidate = cand.candidate,
                    sdpMid = cand.sdpMid,
                    sdpMLineIndex = cand.sdpMLineIndex,
                }, JsonOpts);
                await iceReady.Task.WaitAsync(CancellationToken.None);
                if (ws.State != WebSocketState.Open)
                    return;
                await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LiveStreamClient ICE send failed");
            }
        };

        var offer = pc.createOffer(null);
        await pc.setLocalDescription(offer);

        var sdp = offer.sdp ?? pc.localDescription?.sdp?.ToString();
        if (string.IsNullOrWhiteSpace(sdp))
            throw new InvalidOperationException("WebRTC offer SDP is empty");

        var offerJson = JsonSerializer.Serialize(new { type = "offer", sdp }, JsonOpts);
        await ws.SendAsync(Encoding.UTF8.GetBytes(offerJson), WebSocketMessageType.Text, true, ct);
        iceReady.TrySetResult();

        var mediaPump = Task.Run(() => MediaPumpAsync(pc, encoder, mediaCts.Token), mediaCts.Token);
        return (pc, encoder, mediaCts, mediaPump);
    }

    private async Task MediaPumpAsync(RTCPeerConnection pc, ScreenVp8Encoder encoder, CancellationToken ct)
    {
        var fps = Math.Max(1, _config.StreamFps);
        var frameDurationRtp = 90000 / fps;
        // ~1s GOP by default: I-frame then P-frames so TargetKbps buys sharp UI text.
        var keyframeEvery = Math.Max(1, fps * Math.Max(1, _config.StreamKeyframeIntervalSec));
        var frameIndex = 0;
        long bytesWindow = 0;
        var windowStarted = Environment.TickCount64;

        try
        {
            await foreach (var frame in _capture.Frames.ReadAllAsync(ct))
            {
                if (pc.connectionState is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed)
                    break;

                try
                {
                    if (frameIndex % keyframeEvery == 0)
                        encoder.ForceKeyFrame();
                    frameIndex++;

                    var encoded = encoder.EncodeBgra(frame.Width, frame.Height, frame.Bgra);
                    if (encoded is { Length: > 0 })
                    {
                        bytesWindow += encoded.Length;
                        if (frameIndex <= 3 || frameIndex % keyframeEvery == 1)
                        {
                            _logger.LogInformation(
                                "LiveStreamClient frame#{N} encoded {Bytes} bytes ({W}x{H})",
                                frameIndex, encoded.Length, frame.Width, frame.Height);
                        }
                        pc.SendVideo((uint)frameDurationRtp, encoded);
                    }

                    var elapsed = Environment.TickCount64 - windowStarted;
                    if (elapsed >= 5000)
                    {
                        var kbpsOut = bytesWindow * 8.0 / elapsed; // bytes→kbps over window
                        _logger.LogInformation(
                            "LiveStreamClient encode stats: ~{Kbps:F0}kbps out, frame={W}x{H}, frames={N}",
                            kbpsOut, frame.Width, frame.Height, frameIndex);
                        bytesWindow = 0;
                        windowStarted = Environment.TickCount64;
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "LiveStreamClient encode/send failed");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task StopMediaAsync(
        RTCPeerConnection? pc,
        ScreenVp8Encoder? encoder,
        CancellationTokenSource? mediaCts,
        Task? mediaPump)
    {
        try { mediaCts?.Cancel(); } catch { /* ignored */ }
        if (mediaPump is not null)
        {
            try { await mediaPump; } catch { /* ignored */ }
        }
        try { mediaCts?.Dispose(); } catch { /* ignored */ }
        try { encoder?.Dispose(); } catch { /* ignored */ }
        try { pc?.close(); } catch { /* ignored */ }
        try { pc?.Dispose(); } catch { /* ignored */ }
    }

    private async Task SendHelloAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var monitors = _capture.GetMonitors();
        var hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            platform = OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsLinux() ? "linux" : "macos",
            streamAvailable = _capture.StreamAvailable,
            version = AppInfo.Version,
            selectedMonitor = _capture.SelectedMonitorIndex,
            monitors = monitors.Select(m => new
            {
                index = m.Index,
                name = m.Name,
                width = m.Width,
                height = m.Height,
                isPrimary = m.IsPrimary,
            }).ToArray(),
        }, JsonOpts);
        await ws.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, ct);
    }

    private static List<RTCIceServer> ParseIceServers(JsonElement root)
    {
        var list = new List<RTCIceServer>();
        if (!root.TryGetProperty("iceServers", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            list.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });
            return list;
        }

        foreach (var el in arr.EnumerateArray())
        {
            var urls = new List<string>();
            if (el.TryGetProperty("urls", out var urlsEl))
            {
                if (urlsEl.ValueKind == JsonValueKind.String)
                    urls.Add(urlsEl.GetString()!);
                else if (urlsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var u in urlsEl.EnumerateArray())
                    {
                        var s = u.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            urls.Add(s);
                    }
                }
            }
            if (urls.Count == 0)
                continue;

            var server = new RTCIceServer { urls = string.Join(",", urls) };
            if (el.TryGetProperty("username", out var user) && user.ValueKind == JsonValueKind.String)
                server.username = user.GetString();
            if (el.TryGetProperty("credential", out var cred) && cred.ValueKind == JsonValueKind.String)
                server.credential = cred.GetString();
            list.Add(server);
        }

        if (list.Count == 0)
            list.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });
        return list;
    }

    private static void ApplyRemoteIce(RTCPeerConnection pc, JsonElement root)
    {
        if (!root.TryGetProperty("candidate", out var candProp))
            return;
        var candidate = candProp.GetString();
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        var init = new RTCIceCandidateInit { candidate = candidate };
        if (root.TryGetProperty("sdpMid", out var mid) && mid.ValueKind == JsonValueKind.String)
            init.sdpMid = mid.GetString();
        if (root.TryGetProperty("sdpMLineIndex", out var mline) && mline.ValueKind == JsonValueKind.Number)
            init.sdpMLineIndex = (ushort)mline.GetInt32();

        pc.addIceCandidate(init);
    }

    private static string? BuildWsUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return null;
        if (!Uri.TryCreate(serverUrl.TrimEnd('/'), UriKind.Absolute, out var uri))
            return null;
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/api/v1/live-stream/push",
            Query = string.Empty,
        };
        return builder.Uri.ToString();
    }
}
