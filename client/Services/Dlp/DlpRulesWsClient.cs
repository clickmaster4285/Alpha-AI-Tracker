using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services.Dlp;

/// <summary>
/// DeviceAuth WebSocket to GET /api/v1/dlp/ws — lives only in <c>client --dlp</c>.
/// Receives <c>dlp_rules</c> snapshots on connect and after admin rule CRUD.
/// Presence (<see cref="WsClient"/>) and live-stream stay on the main tracker.
/// </summary>
public sealed class DlpRulesWsClient : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly DlpEngine _engine;
    private readonly ILogger<DlpRulesWsClient> _logger;

    public DlpRulesWsClient(
        AppConfig config,
        ILogStore store,
        DlpEngine engine,
        ILogger<DlpRulesWsClient> logger)
    {
        _config = config;
        _store = store;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DlpRulesWsClient: starting (DLP rule push channel)");
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
                _logger.LogDebug(ex, "DlpRulesWsClient: employee lookup failed");
            }

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
                _logger.LogWarning(ex, "DlpRulesWsClient session ended; reconnect in {Sec}s", backoffSec);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(backoffSec), stoppingToken); }
            catch (OperationCanceledException) { break; }
            backoffSec = Math.Min(60, backoffSec * 2);
        }
    }

    private async Task RunSessionAsync(EmployeeInfo employee, CancellationToken ct)
    {
        var wsUrl = BuildWsUrl(_config.ServerUrl);
        if (wsUrl is null)
        {
            _logger.LogWarning("DlpRulesWsClient: ALPHA_SERVER_URL missing or invalid");
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return;
        }

        using var ws = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(employee.DeviceToken))
            ws.Options.SetRequestHeader("Authorization", $"Device {employee.DeviceToken}");
        else
            ws.Options.SetRequestHeader("Authorization", $"Bearer {employee.Token}");
        ClientIdentityHeaders.Apply(ws.Options);

        _logger.LogInformation("DlpRulesWsClient connecting to {Url}", wsUrl);
        await ws.ConnectAsync(new Uri(wsUrl), ct);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingPump = Task.Run(() => PingLoopAsync(ws, sessionCts.Token), sessionCts.Token);

        var buffer = new byte[256 * 1024];
        var acc = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                acc.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var json = Encoding.UTF8.GetString(acc.ToArray());
                acc.SetLength(0);
                HandleMessage(json);
            }
        }
        finally
        {
            sessionCts.Cancel();
            try { await pingPump; } catch { /* ignored */ }
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

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl))
                return;
            var type = typeEl.GetString();
            if (!string.Equals(type, "dlp_rules", StringComparison.OrdinalIgnoreCase))
                return;

            var payload = JsonSerializer.Deserialize<RulesPayload>(json, JsonOpts);
            _engine.ApplyRules(payload?.Rules, "websocket");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DlpRulesWsClient: failed to apply message");
        }
    }

    private async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var pingBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "ping" }, JsonOpts));
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(25), ct);
                if (ws.State != WebSocketState.Open)
                    break;
                await ws.SendAsync(pingBytes, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "DlpRulesWsClient ping failed");
        }
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
            Path = "/api/v1/dlp/ws",
            Query = string.Empty,
        };
        return builder.Uri.ToString();
    }

    private sealed class RulesPayload
    {
        public List<DlpRule>? Rules { get; set; }
    }
}
