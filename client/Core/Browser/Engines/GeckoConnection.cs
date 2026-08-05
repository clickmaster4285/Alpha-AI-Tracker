using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace client.Core.Browser.Engines;

/// <summary>
/// WebDriver BiDi connection for Gecko (Firefox). Firefox removed CDP in FF 85 and refuses
/// plain-HTTP /session (requires a WebSocket GET upgrade to /session), so this connection is
/// a genuine BiDi client: WS to ws://127.0.0.1:{port}/session, session.new handshake, then
/// browsingContext events + getTree for tab enumeration. Never fabricates data.
/// </summary>
public sealed class GeckoConnection : Abstractions.IBrowserConnection
{
    private readonly DetectedBrowserRuntime _runtime;
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _pendingGate = new();
    private long _id;
    private ClientWebSocket? _ws;
    private string? _sessionId;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public GeckoConnection(DetectedBrowserRuntime runtime, int port, ILogger logger)
    {
        _runtime = runtime;
        _port = port;
        _logger = logger;
    }

    public event EventHandler<RawBrowserEvent>? EventReceived;

    public bool IsConnected => _ws != null && !_disposed;

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/session"), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Gecko WS connect to /session failed: {Msg}", ex.Message);
            _ws = null;
            return;
        }

        // Receive loop must run BEFORE the handshake so session.new replies get dispatched.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ReceiveLoopAsync(_cts.Token);

        // session.new — the BiDi equivalent of the old POST /session. capabilities is
        // required by the spec (may be empty) but must be present as a JSON object.
        var reply = await SendAsync("session.new", new { capabilities = new { } }, ct);
        if (!reply.TryGetProperty("sessionId", out var sid))
        {
            _logger.LogWarning("Gecko session.new failed on port {Port} — Unsupported.", _port);
            await DisposeWsAsync();
            return;
        }
        _sessionId = sid.GetString();
        _logger.LogInformation("Gecko connection established for {Runtime} port={Port} session={SessionId}",
            _runtime.BinaryName, _port, _sessionId);

        // Subscribe to browsingContext lifecycle events so we never need to poll for nav.
        try
        {
            var sub = await SendAsync("session.subscribe", new
            {
                events = new[]
                {
                    "browsingContext.contextCreated",
                    "browsingContext.contextDestroyed",
                    "browsingContext.navigationCommitted",
                    "browsingContext.domContentLoaded",
                    "browsingContext.load",
                    "browsingContext.fragmentNavigated",
                }
            }, ct);
            _logger.LogDebug("Gecko subscribed to browsingContext events");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gecko session.subscribe failed — live events disabled, getTree rebuild still works.");
        }
    }

    public async Task<IReadOnlyList<BrowserTabSnapshot>> QueryTabsAsync(CancellationToken ct)
    {
        try
        {
            var reply = await SendAsync("browsingContext.getTree", new { }, ct);
            if (!reply.TryGetProperty("contexts", out var contexts))
                return Array.Empty<BrowserTabSnapshot>();

            var list = new List<BrowserTabSnapshot>();
            FlattenContexts(contexts, list);
            return list;
        }
        catch
        {
            return Array.Empty<BrowserTabSnapshot>();
        }
    }

    private static void FlattenContexts(JsonElement contexts, List<BrowserTabSnapshot> into)
    {
        foreach (var ctx in contexts.EnumerateArray())
        {
            var id = ctx.TryGetProperty("context", out var c) ? c.GetString() : null;
            if (!string.IsNullOrEmpty(id))
            {
                into.Add(new BrowserTabSnapshot
                {
                    TargetId = id!,
                    Title = ctx.TryGetProperty("title", out var t) ? t.GetString() : null,
                    Url = ctx.TryGetProperty("url", out var u) ? u.GetString() : null,
                });
            }
            if (ctx.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                FlattenContexts(children, into);
        }
    }

    private async Task<JsonElement> SendAsync(string method, object? @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate) _pending[id] = tcs;

        var payload = JsonSerializer.Serialize(new
        {
            id,
            method,
            @params = @params ?? new { },
        });
        await _sendGate.WaitAsync(ct);
        try
        {
            if (_ws == null) throw new InvalidOperationException("Gecko socket not open");
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendGate.Release();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        finally
        {
            lock (_pendingGate) _pending.Remove(id);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && _ws != null)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await DisposeWsAsync();
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                ProcessMessage(ms.ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug("Gecko receive loop ended: {Msg}", ex.Message);
            await DisposeWsAsync();
        }
    }

    private void ProcessMessage(byte[] raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var id))
            {
                lock (_pendingGate)
                {
                    if (_pending.TryGetValue(id, out var tcs))
                    {
                        if (root.TryGetProperty("result", out var result))
                            tcs.TrySetResult(result.Clone());
                        else
                            tcs.TrySetException(new InvalidOperationException("BiDi error: " + root.GetRawText()));
                    }
                }
                return;
            }

            // Event: {"type":"event","method":"browsingContext.navigated","params":{...}}
            if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "event" &&
                root.TryGetProperty("method", out var methodEl) && root.TryGetProperty("params", out var p))
            {
                EmitEvent(methodEl.GetString(), p);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to parse Gecko message: {Msg}", ex.Message);
        }
    }

    private void EmitEvent(string? method, JsonElement p)
    {
        var action = method switch
        {
            "browsingContext.contextCreated" => "created",
            "browsingContext.contextDestroyed" => "closed",
            "browsingContext.navigationCommitted" => "navigated",
            "browsingContext.domContentLoaded" => "navigated",
            "browsingContext.load" => "navigated",
            "browsingContext.fragmentNavigated" => "navigated",
            _ => null,
        };
        if (action == null) return;

        var context = p.TryGetProperty("context", out var ctx) ? ctx.GetString() : null;
        if (string.IsNullOrEmpty(context)) return;

        var url = p.TryGetProperty("url", out var u) ? u.GetString() : null;
        var title = p.TryGetProperty("title", out var t) ? t.GetString() : null;

        EventReceived?.Invoke(this, new RawBrowserEvent
        {
            Engine = BrowserEngine.Gecko,
            Source = BrowserEventSource.GeckoRdp,
            RuntimeId = _runtime.Id.ToString("N"),
            TabId = context,
            WindowId = context,
            Url = url,
            Title = title,
            Action = action,
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        await DisposeWsAsync();
    }

    private async Task DisposeWsAsync()
    {
        var ws = _ws;
        _ws = null;
        if (ws == null) return;
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None);
            ws.Dispose();
        }
        catch { }
    }
}
