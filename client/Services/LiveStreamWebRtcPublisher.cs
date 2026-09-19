using System.Text.Json;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace client.Services;

/// <summary>
/// Windows-only WebRTC publisher: feeds screen BGRA frames into a VP8 encoder and
/// publishes via SIPSorcery RTCPeerConnection. Signaling rides the push WebSocket.
/// Native encode is gated until formats are negotiated — premature encode can AV-crash
/// the whole process (vpxmd.dll).
/// </summary>
public sealed class LiveStreamWebRtcPublisher : IDisposable
{
    private readonly ScreenCaptureService _capture;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private RTCPeerConnection? _pc;
    private VideoEncoderEndPoint? _encoder;
    private Func<string, Task>? _sendJson;
    private bool _rawSubscribed;
    private bool _started;
    private bool _encodeReady;
    private int _frameDurationMs = 100;

    public LiveStreamWebRtcPublisher(ScreenCaptureService capture, ILogger logger)
    {
        _capture = capture;
        _logger = logger;
    }

    public bool IsActive
    {
        get { lock (_gate) return _started && _pc is not null; }
    }

    /// <summary>True after a local offer was sent successfully.</summary>
    public bool OfferSent { get; private set; }

    public async Task StartAsync(
        Func<string, Task> sendJson,
        int fps,
        IReadOnlyList<RTCIceServer>? iceServers,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("WebRTC publisher skipped — Windows only");
            return;
        }

        Stop();
        OfferSent = false;

        _sendJson = sendJson ?? throw new ArgumentNullException(nameof(sendJson));
        _frameDurationMs = Math.Clamp(1000 / Math.Max(1, fps), 33, 200);

        var servers = iceServers is { Count: > 0 }
            ? iceServers.ToList()
            : new List<RTCIceServer> { new() { urls = "stun:stun.l.google.com:19302" } };

        RTCPeerConnection pc;
        VideoEncoderEndPoint encoder;
        try
        {
            pc = new RTCPeerConnection(new RTCConfiguration { iceServers = servers });
            encoder = new VideoEncoderEndPoint();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebRTC publisher: failed to create peer/encoder (missing vpxmd.dll?)");
            throw;
        }

        var track = new MediaStreamTrack(encoder.GetVideoSourceFormats(), MediaStreamStatusEnum.SendOnly);
        pc.addTrack(track);

        encoder.OnVideoSourceEncodedSample += pc.SendVideo;
        pc.OnVideoFormatsNegotiated += formats =>
        {
            if (formats is { Count: > 0 })
            {
                encoder.SetVideoSourceFormat(formats[0]);
                lock (_gate) _encodeReady = true;
                _logger.LogInformation("WebRTC publisher: video format negotiated → encode enabled");
            }
        };

        pc.onicecandidate += async cand =>
        {
            if (cand is null || string.IsNullOrWhiteSpace(cand.candidate))
                return;
            try
            {
                var json = JsonSerializer.Serialize(new
                {
                    type = "ice",
                    role = "publisher",
                    candidate = new
                    {
                        candidate = cand.candidate,
                        sdpMid = cand.sdpMid,
                        sdpMLineIndex = cand.sdpMLineIndex,
                    },
                });
                var send = _sendJson;
                if (send is not null)
                    await send(json);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebRTC publisher ICE send failed");
            }
        };

        pc.onconnectionstatechange += state =>
        {
            _logger.LogInformation("WebRTC publisher connection state → {State}", state);
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
            {
                lock (_gate) _encodeReady = false;
            }
            else if (state == RTCPeerConnectionState.connected)
            {
                lock (_gate) _encodeReady = true;
            }
        };

        lock (_gate)
        {
            _pc = pc;
            _encoder = encoder;
            _encodeReady = false;
            if (!_rawSubscribed)
            {
                _capture.OnRawFrame += OnRawFrame;
                _rawSubscribed = true;
            }
            _started = true;
        }

        await encoder.StartVideo();

        var offer = pc.createOffer(null);
        await pc.setLocalDescription(offer);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (pc.iceGatheringState != RTCIceGatheringState.complete && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, ct);
        }

        var local = pc.localDescription;
        if (local?.sdp is null)
        {
            _logger.LogWarning("WebRTC publisher: empty local SDP");
            Stop();
            return;
        }

        var offerJson = JsonSerializer.Serialize(new
        {
            type = "offer",
            role = "publisher",
            sdp = local.sdp.ToString(),
        });
        await sendJson(offerJson);
        OfferSent = true;
        _logger.LogInformation("WebRTC publisher: offer sent ({Bytes} bytes SDP)", offerJson.Length);
    }

    public void HandleRemoteSignal(JsonElement root)
    {
        RTCPeerConnection? pc;
        lock (_gate) pc = _pc;
        if (pc is null)
            return;

        if (!root.TryGetProperty("type", out var typeProp))
            return;
        var type = typeProp.GetString();

        try
        {
            if (type == "answer" && root.TryGetProperty("sdp", out var sdpProp))
            {
                var sdp = sdpProp.GetString();
                if (string.IsNullOrWhiteSpace(sdp))
                    return;
                var result = pc.setRemoteDescription(new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.answer,
                    sdp = sdp,
                });
                _logger.LogInformation("WebRTC publisher: remote answer → {Result}", result);
                if (result == SetDescriptionResultEnum.OK)
                {
                    lock (_gate) _encodeReady = true;
                }
            }
            else if (type == "ice" && root.TryGetProperty("candidate", out var candEl))
            {
                var init = new RTCIceCandidateInit();
                if (candEl.TryGetProperty("candidate", out var c) && c.ValueKind == JsonValueKind.String)
                    init.candidate = c.GetString();
                if (candEl.TryGetProperty("sdpMid", out var mid) && mid.ValueKind == JsonValueKind.String)
                    init.sdpMid = mid.GetString();
                if (candEl.TryGetProperty("sdpMLineIndex", out var mli) && mli.ValueKind == JsonValueKind.Number)
                    init.sdpMLineIndex = (ushort)mli.GetInt32();

                if (!string.IsNullOrWhiteSpace(init.candidate))
                    pc.addIceCandidate(init);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebRTC publisher signal handling failed type={Type}", type);
        }
    }

    public void Stop()
    {
        RTCPeerConnection? pc;
        VideoEncoderEndPoint? encoder;
        lock (_gate)
        {
            _started = false;
            _encodeReady = false;
            pc = _pc;
            encoder = _encoder;
            _pc = null;
            _encoder = null;
            if (_rawSubscribed)
            {
                _capture.OnRawFrame -= OnRawFrame;
                _rawSubscribed = false;
            }
        }
        OfferSent = false;

        try { encoder?.Dispose(); } catch { /* ignore */ }
        try { pc?.Close("stop"); } catch { /* ignore */ }
        try { pc?.Dispose(); } catch { /* ignore */ }
    }

    private void OnRawFrame(int width, int height, byte[] bgra)
    {
        VideoEncoderEndPoint? encoder;
        lock (_gate)
        {
            if (!_started || !_encodeReady)
                return;
            encoder = _encoder;
        }
        if (encoder is null || bgra.Length == 0 || width < 2 || height < 2)
            return;

        // libvpx requires even dimensions — odd sizes can AV-crash the process.
        var evenW = width & ~1;
        var evenH = height & ~1;
        if (evenW != width || evenH != height)
        {
            bgra = CropBgra(bgra, width, height, evenW, evenH);
            width = evenW;
            height = evenH;
        }

        try
        {
            encoder.ExternalVideoSourceRawSample(
                (uint)_frameDurationMs,
                width,
                height,
                bgra,
                VideoPixelFormatsEnum.Bgra);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WebRTC encode frame failed");
        }
    }

    private static byte[] CropBgra(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        var srcStride = srcW * 4;
        var dstStride = dstW * 4;
        var rows = Math.Min(dstH, srcH);
        for (var y = 0; y < rows; y++)
            Buffer.BlockCopy(src, y * srcStride, dst, y * dstStride, dstStride);
        return dst;
    }

    public void Dispose() => Stop();
}
