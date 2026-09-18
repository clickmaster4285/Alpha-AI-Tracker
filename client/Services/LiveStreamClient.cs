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

        var hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            platform = OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsLinux() ? "linux" : "macos",
            streamAvailable = _capture.StreamAvailable,
            version = AppInfo.Version,
        }, JsonOpts);
        var helloBytes = Encoding.UTF8.GetBytes(hello);
        await ws.SendAsync(helloBytes, WebSocketMessageType.Text, true, ct);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sendPump = Task.Run(() => SendFramesAsync(ws, sessionCts.Token), sessionCts.Token);

        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("type", out var typeProp))
                    continue;
                var type = typeProp.GetString();
                if (type == "start")
                {
                    _logger.LogInformation("LiveStreamClient: start capture");
                    _capture.SetStreamActive(true);
                }
                else if (type == "stop")
                {
                    _logger.LogInformation("LiveStreamClient: stop capture");
                    _capture.SetStreamActive(false);
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

    private async Task SendFramesAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _capture.Frames.ReadAllAsync(ct))
            {
                if (ws.State != WebSocketState.Open)
                    break;
                try
                {
                    await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct);
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
