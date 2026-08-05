using System.Net.Http.Json;
using System.Text.Json;
using client.Core.Browser.Abstractions;
using Microsoft.Extensions.Logging;

namespace client.Core.Browser.Engines;

/// <summary>
/// Live CDP connection to one Chromium runtime. Raises normalized RawBrowserEvents
/// (engine-shaped) from target discovery + page navigation events, backed by a
/// /json/list poll as a safety net. One connection covers all windows/tabs/incognito.
/// </summary>
public sealed class ChromiumConnection : IBrowserConnection
{
    private readonly DetectedBrowserRuntime _runtime;
    private readonly string _wsUrl;
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly CdpSession _session = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string, string> _pageSessions = new(); // targetId -> sessionId
    private readonly Dictionary<string, BrowserTabSnapshot> _lastTabs = new();
    private readonly object _lock = new();
    private readonly Guid _defaultProfileId;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    public ChromiumConnection(DetectedBrowserRuntime runtime, string wsUrl, int port, ILogger logger)
    {
        _runtime = runtime;
        _wsUrl = wsUrl;
        _port = port;
        _logger = logger;
        _defaultProfileId = runtime.Profiles.FirstOrDefault()?.Id ?? Guid.NewGuid();
    }

    public event EventHandler<RawBrowserEvent>? EventReceived;
    public bool IsConnected => _session.IsConnected;

    public async Task StartAsync(CancellationToken ct)
    {
        await _session.ConnectAsync(_wsUrl, ct);
        _session.EventReceived += OnEvent;

        // Discover existing targets first.
        foreach (var tab in await QueryTabsAsync(ct))
            await AttachPageAsync(tab.TargetId, ct);

        await _session.SendVoidAsync("Target.setDiscoverTargets",
            JsonSerializer.Deserialize<JsonElement>("{\"discover\":true}"), null, ct);

        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = PollLoopAsync(_pollCts.Token);
        _logger.LogInformation("CDP connected: {Runtime} port={Port} ws={Ws}", _runtime.BinaryName, _port, _wsUrl);
    }

    public async Task StopAsync()
    {
        _pollCts?.Cancel();
        if (_pollTask != null)
        {
            try { await _pollTask; } catch { }
        }
        _session.EventReceived -= OnEvent;
        await _session.DisposeAsync();
    }

    public async Task<IReadOnlyList<BrowserTabSnapshot>> QueryTabsAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"http://127.0.0.1:{_port}/json/list", ct);
            if (!resp.IsSuccessStatusCode) return Array.Empty<BrowserTabSnapshot>();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var list = new List<BrowserTabSnapshot>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type != "page") continue;
                var id = el.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                var incognito = el.TryGetProperty("browserContextId", out var ctx)
                    && ctx.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ctx.GetString());
                list.Add(new BrowserTabSnapshot
                {
                    TargetId = id!,
                    Title = el.TryGetProperty("title", out var ti) ? ti.GetString() : null,
                    Url = el.TryGetProperty("url", out var u) ? u.GetString() : null,
                    Incognito = incognito ? true : null,
                });
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "/json/list poll failed");
            return Array.Empty<BrowserTabSnapshot>();
        }
    }

    // ── event handling ──

    private void OnEvent(object? sender, (string method, JsonElement? sessionId, JsonElement data) e)
    {
        try
        {
            switch (e.method)
            {
                case "Target.targetCreated":
                    HandleTargetCreated(e.data);
                    break;
                case "Target.targetInfoChanged":
                    HandleTargetInfoChanged(e.data);
                    break;
                case "Target.targetDestroyed":
                    HandleTargetDestroyed(e.data);
                    break;
                case "Page.frameNavigated":
                    HandleFrameNavigated(e.data);
                    break;
                case "Page.titleChanged":
                    HandleTitleChanged(e.data, e.sessionId);
                    break;
                case "Network.requestWillBeSent":
                    HandleRequestWillBeSent(e.data);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error handling CDP event {Method}", e.method);
        }
    }

    private async void HandleTargetCreated(JsonElement data)
    {
        var target = data.TryGetProperty("targetInfo", out var t) ? t : data;
        var type = target.TryGetProperty("type", out var ty) ? ty.GetString() : null;
        if (type != "page") return;
        var id = target.TryGetProperty("targetId", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(id)) return;

        await AttachPageAsync(id!, CancellationToken.None);
        EmitFromTarget(id!, target, BrowserEventAction.Created);
    }

    private void HandleTargetInfoChanged(JsonElement data)
    {
        var info = data.TryGetProperty("targetInfo", out var t) ? t : data;
        var type = info.TryGetProperty("type", out var ty) ? ty.GetString() : null;
        if (type != "page") return;
        var id = info.TryGetProperty("targetId", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(id)) return;
        EmitFromTarget(id!, info, BrowserEventAction.Updated);
    }

    private void HandleTargetDestroyed(JsonElement data)
    {
        var id = data.TryGetProperty("targetId", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock) _pageSessions.Remove(id!);
        Emit(new RawBrowserEvent
        {
            Engine = BrowserEngine.Chromium,
            Source = BrowserEventSource.Cdp,
            RuntimeId = _runtime.Id.ToString("N"),
            TabId = id,
            Title = _lastTabs.TryGetValue(id!, out var t) ? t.Title : null,
            Url = _lastTabs.TryGetValue(id!, out var u) ? u.Url : null,
            Action = "closed",
        });
    }

    private void HandleFrameNavigated(JsonElement data)
    {
        if (!data.TryGetProperty("frame", out var frame)) return;
        var isMain = frame.TryGetProperty("parentId", out _) == false;
        if (!isMain) return;
        var url = frame.TryGetProperty("url", out var u) ? u.GetString() : null;
        var targetId = frame.TryGetProperty("targetId", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(targetId)) return;
        Emit(new RawBrowserEvent
        {
            Engine = BrowserEngine.Chromium,
            Source = BrowserEventSource.Cdp,
            RuntimeId = _runtime.Id.ToString("N"),
            TabId = targetId,
            Url = url,
            Action = "navigated",
        });
    }

    private void HandleTitleChanged(JsonElement data, JsonElement? sessionId)
    {
        var title = data.TryGetProperty("title", out var t) ? t.GetString() : null;
        if (title == null) return;
        var targetId = FindTargetBySession(sessionId);
        if (targetId == null) return;
        Emit(new RawBrowserEvent
        {
            Engine = BrowserEngine.Chromium,
            Source = BrowserEventSource.Cdp,
            RuntimeId = _runtime.Id.ToString("N"),
            TabId = targetId,
            Title = title,
            Action = "updated",
        });
    }

    private void HandleRequestWillBeSent(JsonElement data)
    {
        var type = data.TryGetProperty("type", out var ty) ? ty.GetString() : null;
        if (type != "Document") return;
        var url = data.TryGetProperty("request", out var req)
            && req.TryGetProperty("url", out var u) ? u.GetString() : null;
        var targetId = data.TryGetProperty("frameId", out var f) ? f.GetString() : null;
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(targetId)) return;
        Emit(new RawBrowserEvent
        {
            Engine = BrowserEngine.Chromium,
            Source = BrowserEventSource.Cdp,
            RuntimeId = _runtime.Id.ToString("N"),
            TabId = targetId,
            Url = url,
            Action = "navigated",
        });
    }

    private string? FindTargetBySession(JsonElement? sessionId)
    {
        if (sessionId == null) return null;
        lock (_lock)
            return _pageSessions.FirstOrDefault(kv => kv.Value == sessionId!.Value.GetString()).Key;
    }

    private void EmitFromTarget(string targetId, JsonElement target, BrowserEventAction action)
    {
        var url = target.TryGetProperty("url", out var u) ? u.GetString() : null;
        var title = target.TryGetProperty("title", out var t) ? t.GetString() : null;
        var incognito = target.TryGetProperty("browserContextId", out var ctx)
            && ctx.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ctx.GetString());
        Emit(new RawBrowserEvent
        {
            Engine = BrowserEngine.Chromium,
            Source = BrowserEventSource.Cdp,
            RuntimeId = _runtime.Id.ToString("N"),
            TabId = targetId,
            Url = string.IsNullOrEmpty(url) ? null : url,
            Title = string.IsNullOrEmpty(title) ? null : title,
            Incognito = incognito ? true : null,
            Action = action.ToString().ToLowerInvariant(),
        });
    }

    private async Task AttachPageAsync(string targetId, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_pageSessions.ContainsKey(targetId)) return;
        }
        try
        {
            var args = JsonSerializer.Serialize(new { targetId, flatten = true });
            var res = await _session.SendAsync("Target.attachToTarget",
                JsonSerializer.Deserialize<JsonElement>(args), null, ct);
            var sessionId = res.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            if (sessionId == null) return;
            lock (_lock) _pageSessions[targetId] = sessionId;
            await _session.SendVoidAsync("Page.enable", null, sessionId, ct);
            await _session.SendVoidAsync("Network.enable", null, sessionId, ct);
            await _session.SendVoidAsync("Runtime.enable", null, sessionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to attach to target {Target}", targetId);
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tabs = await QueryTabsAsync(ct);
                var live = new HashSet<string>();
                foreach (var tab in tabs)
                {
                    live.Add(tab.TargetId);
                    lock (_lock)
                    {
                        var prev = _lastTabs.TryGetValue(tab.TargetId, out var p) ? p : null;
                        if (prev == null)
                        {
                            _lastTabs[tab.TargetId] = tab;
                            _ = Task.Run(() => AttachPageAsync(tab.TargetId, CancellationToken.None), ct);
                            Emit(new RawBrowserEvent
                            {
                                Engine = BrowserEngine.Chromium,
                                Source = BrowserEventSource.Cdp,
                                RuntimeId = _runtime.Id.ToString("N"),
                                TabId = tab.TargetId,
                                Url = tab.Url,
                                Title = tab.Title,
                                Incognito = tab.Incognito,
                                Action = "created",
                            });
                        }
                        else if (prev.Url != tab.Url || prev.Title != tab.Title)
                        {
                            _lastTabs[tab.TargetId] = tab;
                            Emit(new RawBrowserEvent
                            {
                                Engine = BrowserEngine.Chromium,
                                Source = BrowserEventSource.Cdp,
                                RuntimeId = _runtime.Id.ToString("N"),
                                TabId = tab.TargetId,
                                Url = tab.Url,
                                Title = tab.Title,
                                Incognito = tab.Incognito,
                                Action = tab.Url != prev.Url ? "navigated" : "updated",
                            });
                        }
                    }
                }

                List<string> removed;
                lock (_lock)
                {
                    removed = _lastTabs.Keys.Where(k => !live.Contains(k)).ToList();
                    foreach (var r in removed) _lastTabs.Remove(r);
                }
                foreach (var r in removed)
                {
                    Emit(new RawBrowserEvent
                    {
                        Engine = BrowserEngine.Chromium,
                        Source = BrowserEventSource.Cdp,
                        RuntimeId = _runtime.Id.ToString("N"),
                        TabId = r,
                        Action = "closed",
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CDP poll loop error");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private void Emit(RawBrowserEvent raw)
    {
        raw.ProfileId ??= _defaultProfileId.ToString("N");
        raw.Timestamp = DateTimeOffset.UtcNow;
        EventReceived?.Invoke(this, raw);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
