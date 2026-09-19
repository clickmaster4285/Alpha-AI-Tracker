using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Outbound WebSocket to GET /api/v1/live-stream/push (DeviceAuth).
/// Keeps a persistent control connection while enabled + logged in; captures only
/// after the server sends {"type":"start"} and stops on {"type":"stop"}.
/// When start.media is webrtc/both, publishes VP8 via WebRTC in parallel with JPEG.
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
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) { }
            return;
        }

        var backoffSec = 2;
        while (!stoppingToken.IsCancellationRequested)
        {
            EmployeeInfo? employee = null;
            try
            {
                employee = await _store.GetEmployeeInfoAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LiveStreamClient: employee lookup failed");
            }

            if (employee is null ||
                (string.IsNullOrWhiteSpace(employee.DeviceToken) && string.IsNullOrWhiteSpace(employee.Token)))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
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
                _logger.LogWarning(ex, "LiveStreamClient session ended; reconnect in {Sec}s", backoffSec);
            }
            finally
            {
                _capture.SetStreamActive(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSec), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoffSec = Math.Min(60, backoffSec * 2);
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

        // Prefer WebRTC on Windows when capture works. Always advertise truthfully —
        // a missing/false webrtcCapable with LIVE_STREAM_MEDIA=webrtc used to disable
        // JPEG and produce no frames at all.
        var webrtcCapable = OperatingSystem.IsWindows() && _capture.StreamAvailable;
        await SendHelloAsync(ws, webrtcCapable, ct);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var publisher = new LiveStreamWebRtcPublisher(_capture, _logger);
        var sendJpeg = true; // false only when media=webrtc and publisher is active
        var wsSendGate = new SemaphoreSlim(1, 1);
        var sendPump = Task.Run(
            () => SendFramesAsync(ws, () => sendJpeg, wsSendGate, sessionCts.Token),
            sessionCts.Token);

        async Task SendTextAsync(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await wsSendGate.WaitAsync(ct);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                wsSendGate.Release();
            }
        }

        var buffer = new byte[256 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ReceiveFullMessageAsync(ws, buffer, ct);
                if (result is null)
                    break;

                if (result.Value.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.Value.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(result.Value.Payload);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("type", out var typeProp))
                    continue;
                var type = typeProp.GetString();
                if (type == "start")
                {
                    var media = "jpeg";
                    if (doc.RootElement.TryGetProperty("media", out var mediaProp) &&
                        mediaProp.ValueKind == JsonValueKind.String)
                        media = mediaProp.GetString() ?? "jpeg";
                    var iceServers = ParseIceServers(doc.RootElement);
                    _logger.LogInformation(
                        "LiveStreamClient: start capture media={Media} iceServers={Count} webrtcCapable={Cap}",
                        media, iceServers.Count, webrtcCapable);

                    // Always keep JPEG on until WebRTC has actually sent an offer.
                    // With LIVE_STREAM_MEDIA=webrtc + webrtcCapable=false (or a native
                    // encoder crash), disabling JPEG left the admin with a black pane
                    // and looked like "stream not start".
                    sendJpeg = true;
                    publisher.Stop();

                    var wantWebRtc = (media is "webrtc" or "both") && webrtcCapable;
                    if (wantWebRtc)
                    {
                        try
                        {
                            // Build peer connection BEFORE capture starts so OnRawFrame
                            // cannot hit libvpx before formats are negotiated.
                            await publisher.StartAsync(SendTextAsync, _config.StreamFps, iceServers, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "WebRTC publisher failed — JPEG only");
                            publisher.Stop();
                        }
                    }
                    else if (media == "webrtc" && !webrtcCapable)
                    {
                        _logger.LogWarning(
                            "Server asked for webrtc but this client is not capable — using JPEG");
                    }

                    _capture.SetStreamActive(true);

                    // Only drop JPEG when WebRTC offer is out (SFU path is live).
                    if (media == "webrtc" && publisher.OfferSent)
                        sendJpeg = false;
                }
                else if (type == "stop")
                {
                    _logger.LogInformation("LiveStreamClient: stop capture");
                    publisher.Stop();
                    _capture.SetStreamActive(false);
                    sendJpeg = true;
                }
                else if (type == "select_monitor")
                {
                    var idx = 0;
                    if (doc.RootElement.TryGetProperty("index", out var idxProp) &&
                        idxProp.ValueKind == JsonValueKind.Number)
                        idx = idxProp.GetInt32();
                    var applied = _capture.SetSelectedMonitor(idx);
                    _logger.LogInformation("LiveStreamClient: select_monitor → {Index}", applied);
                    await SendHelloAsync(ws, webrtcCapable, ct, wsSendGate);
                }
                else if (type is "offer" or "answer" or "ice")
                {
                    publisher.HandleRemoteSignal(doc.RootElement);
                }
                else if (type == "error")
                {
                    _logger.LogWarning("LiveStreamClient server error: {Json}", json);
                    break;
                }
            }
        }
        finally
        {
            publisher.Stop();
            sessionCts.Cancel();
            _capture.SetStreamActive(false);
            try { await sendPump; } catch { /* ignored */ }
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch { /* ignored */ }
            }
        }
    }

    private async Task SendHelloAsync(
        ClientWebSocket ws,
        bool webrtcCapable,
        CancellationToken ct,
        SemaphoreSlim? gate = null)
    {
        var hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            platform = OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsLinux() ? "linux" : "macos",
            streamAvailable = _capture.StreamAvailable,
            version = AppInfo.Version,
            selectedMonitor = _capture.SelectedMonitorIndex,
            webrtcCapable,
            mediaModes = webrtcCapable ? new[] { "jpeg", "webrtc" } : new[] { "jpeg" },
            monitors = _capture.GetMonitors().Select(m => new
            {
                index = m.Index,
                name = m.Name,
                width = m.Width,
                height = m.Height,
                isPrimary = m.IsPrimary,
            }).ToArray(),
        }, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(hello);
        if (gate is not null)
        {
            await gate.WaitAsync(ct);
            try
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                gate.Release();
            }
        }
        else
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
    }

    private async Task SendFramesAsync(
        ClientWebSocket ws,
        Func<bool> sendJpeg,
        SemaphoreSlim sendGate,
        CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _capture.Frames.ReadAllAsync(ct))
            {
                if (ws.State != WebSocketState.Open)
                    break;
                if (!sendJpeg())
                    continue;
                try
                {
                    await sendGate.WaitAsync(ct);
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                            await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
                    }
                    finally
                    {
                        sendGate.Release();
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "LiveStreamClient frame send failed");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static async Task<(WebSocketMessageType MessageType, byte[] Payload)?> ReceiveFullMessageAsync(
        ClientWebSocket ws,
        byte[] buffer,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, Array.Empty<byte>());
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return (result.MessageType, ms.ToArray());
    }

    private static List<SIPSorcery.Net.RTCIceServer> ParseIceServers(JsonElement root)
    {
        var list = new List<SIPSorcery.Net.RTCIceServer>();
        if (!root.TryGetProperty("iceServers", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var el in arr.EnumerateArray())
        {
            var urls = new List<string>();
            if (el.TryGetProperty("urls", out var urlsEl))
            {
                if (urlsEl.ValueKind == JsonValueKind.String)
                {
                    var u = urlsEl.GetString();
                    if (!string.IsNullOrWhiteSpace(u))
                        urls.Add(u!);
                }
                else if (urlsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var uEl in urlsEl.EnumerateArray())
                    {
                        var u = uEl.GetString();
                        if (!string.IsNullOrWhiteSpace(u))
                            urls.Add(u!);
                    }
                }
            }

            string? username = null;
            string? credential = null;
            if (el.TryGetProperty("username", out var userEl) && userEl.ValueKind == JsonValueKind.String)
                username = userEl.GetString();
            if (el.TryGetProperty("credential", out var credEl) && credEl.ValueKind == JsonValueKind.String)
                credential = credEl.GetString();

            foreach (var u in urls)
            {
                list.Add(new SIPSorcery.Net.RTCIceServer
                {
                    urls = u,
                    username = username,
                    credential = credential,
                });
            }
        }

        return list;
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
