using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace client.Core.Browser.Engines;

/// <summary>
/// Minimal Chrome DevTools Protocol transport over WebSocket (BCL only, no NuGet).
/// Handles connection, JSON-RPC send/receive correlation, and a raw event pump.
/// </summary>
public sealed class CdpSession : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private int _nextId = 1;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();

    public event EventHandler<(string method, JsonElement? sessionId, JsonElement data)>? EventReceived;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public async Task ConnectAsync(string wsUrl, CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await _ws.ConnectAsync(new Uri(wsUrl), ct);
        _ = PumpAsync(_cts.Token);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(ms.ToArray());
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                {
                    if (_pending.TryGetValue(idEl.GetInt32(), out var tcs))
                    {
                        _pending.Remove(idEl.GetInt32());
                        if (root.TryGetProperty("error", out var err))
                            tcs.TrySetException(new Exception(err.GetRawText()));
                        else if (root.TryGetProperty("result", out var res))
                            tcs.TrySetResult(res.Clone());
                        else
                            tcs.TrySetResult(default);
                    }
                    continue;
                }

                if (root.TryGetProperty("method", out var methodEl))
                {
                    JsonElement? sessionId = null;
                    if (root.TryGetProperty("sessionId", out var s))
                        sessionId = s.Clone();
                    var data = root.TryGetProperty("params", out var p) ? p.Clone() : default;
                    EventReceived?.Invoke(this, (methodEl.GetString() ?? string.Empty, sessionId, data));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception) { }
    }

    public async Task<JsonElement> SendAsync(string method, JsonElement? args, string? sessionId, CancellationToken ct)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("CDP session not connected");

        var id = _nextId++;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var req = new Dictionary<string, object>
        {
            ["id"] = id,
            ["method"] = method,
        };
        if (args != null) req["params"] = args;
        if (!string.IsNullOrEmpty(sessionId)) req["sessionId"] = sessionId!;

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req));
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        return await tcs.Task.WaitAsync(ct);
    }

    public async Task SendVoidAsync(string method, JsonElement? args, string? sessionId, CancellationToken ct)
    {
        try { await SendAsync(method, args, sessionId, ct); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CDP {method} failed: {ex.Message}"); }
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cts.Cancel();
        if (_ws != null)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { }
            _ws.Dispose();
            _ws = null;
        }
        _cts.Dispose();
    }
}
