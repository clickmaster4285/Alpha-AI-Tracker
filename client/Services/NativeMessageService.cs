using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using client.Core.Abstractions;
using client.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Listens on a Unix domain socket (Linux/macOS) or named pipe (Windows)
/// for browser navigation events forwarded by the Native Messaging host (native-host.py).
///
/// Messages arrive as JSON from the browser extension via the native messaging pipeline:
///   Extension → native-host.py (stdin) → Unix socket → NativeMessageService
///
/// Each message represents a tab navigation event (url, title, tabId, action)
/// and is stored as an AppItem (browser_navigation) in the SQLite database.
/// </summary>
public class NativeMessageService : BackgroundService
{
    private readonly ILogStore _store;
    private readonly ILogger<NativeMessageService> _logger;
    private readonly string _socketPath;

    // Cache: browser process name → (installed_app_id, installed_app_displayName)
    private readonly Dictionary<string, (string id, string displayName)> _browserAppCache = new(StringComparer.OrdinalIgnoreCase);
    // Cache: browser process name + tabId → current AppSession.Id
    private readonly Dictionary<string, string> _tabSessionCache = new(StringComparer.OrdinalIgnoreCase);

    // Heartbeat: last time a ping (or any message) was received from the extension.
    // Used by BrowserExtensionService to confirm the extension is truly alive
    // without relying on process-based heuristics. Stored as UTC ticks for
    // lock-free cross-thread reads via Interlocked.
    private long _lastHeartbeatAtTicks = DateTime.MinValue.Ticks;

    public NativeMessageService(ILogStore store, ILogger<NativeMessageService> logger)
    {
        _store = store;
        _logger = logger;
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _socketPath = Path.Combine(baseDir, ".local", "share", "alpha-ai-tracker", "native-messaging.sock");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ensure socket directory exists
        var dir = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Remove any stale socket file from a previous run
        if (File.Exists(_socketPath))
        {
            try { File.Delete(_socketPath); }
            catch { _logger.LogWarning("Could not remove stale socket at {Path}", _socketPath); }
        }

        _logger.LogInformation("NativeMessageService starting on socket {Path}", _socketPath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var endpoint = new UnixDomainSocketEndPoint(_socketPath);

                try
                {
                    listener.Bind(endpoint);
                    listener.Listen(10); // max 10 pending connections

                    // Set permissions so the Python proxy can connect
                    try
                    {
                        File.SetUnixFileMode(_socketPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite |
                            UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                            UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
                    }
                    catch { /* platform might not support this */ }

                    _logger.LogInformation("Native messaging socket listening");

                    while (!stoppingToken.IsCancellationRequested)
                    {
                        var handler = await listener.AcceptAsync(stoppingToken);
                        // Fire-and-forget: accept loop never blocks on a slow client.
                        // Each connection is handled concurrently in its own task.
                        _ = HandleConnectionAsync(handler, stoppingToken);
                    }
                }
                catch (SocketException ex) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Native messaging socket error, restarting listener");
                    await Task.Delay(1000, stoppingToken);
                }
                finally
                {
                    try { File.Delete(_socketPath); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Cleanup socket on shutdown
        try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
        _logger.LogInformation("NativeMessageService stopped");
    }

    private async Task HandleConnectionAsync(Socket handler, CancellationToken ct)
    {
        using (handler) // dispose handler when done
        {
            try
            {
                var buffer = new byte[8192];

                // FIX 2026-07-28: 5-second receive timeout so the accept loop never
                // stalls permanently. If a client connects but doesn't send data within
                // 5 seconds, we disconnect and the loop immediately accepts the next.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                var bytesRead = await handler.ReceiveAsync(buffer, SocketFlags.None, timeoutCts.Token);
                if (bytesRead == 0) return;

                var json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var msg = JsonSerializer.Deserialize<BrowserMessage>(json);
                if (msg == null) return;

                _logger.LogDebug("Browser event: {Action} tab={TabId} url={Url}",
                    msg.Action, msg.TabId, TruncateUrl(msg.Url, 80));

                // Always send a response, even if processing throws.
                // If no response is sent, native-host.py blocks waiting for it
                // and the entire messaging pipeline stalls after a few timeouts.
                try
                {
                    await ProcessMessageAsync(msg, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing browser message: {Action} tab={TabId}",
                        msg.Action, msg.TabId);
                }
                finally
                {
                    // Always send acknowledgment — native-host.py blocks on this response
                    var response = JsonSerializer.Serialize(new { status = "ok", tabId = msg.TabId });
                    var responseBytes = Encoding.UTF8.GetBytes(response);
                    await handler.SendAsync(responseBytes, SocketFlags.None, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Server shutting down — exit silently
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Receive timed out after 5s — client disconnected without sending data");
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Invalid JSON from browser extension");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error handling browser extension message");
            }
        }
    }

    /// <summary>Whether the browser extension has sent a heartbeat within the last <paramref name="maxAgeSeconds"/> seconds.
    /// The extension's background.js sends a ping every ~27s via chrome.alarms.
    /// 60s threshold covers ~2 missed cycles before declaring the extension disconnected.</summary>
    public bool IsExtensionConnected(int maxAgeSeconds = 60)
    {
        var lastTicks = Interlocked.Read(ref _lastHeartbeatAtTicks);
        if (lastTicks == DateTime.MinValue.Ticks) return false;
        return (DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc)).TotalSeconds < maxAgeSeconds;
    }

    /// <summary>Reset heartbeat (e.g., on startup to clear stale state from a previous session).</summary>
    public void ResetHeartbeat() => Interlocked.Exchange(ref _lastHeartbeatAtTicks, DateTime.MinValue.Ticks);

    private async Task ProcessMessageAsync(BrowserMessage msg, CancellationToken ct)
    {
        // Ping messages — record heartbeat, nothing else to store
        if (msg.Action == "ping")
        {
            Interlocked.Exchange(ref _lastHeartbeatAtTicks, DateTime.UtcNow.Ticks);
            return;
        }

        // Tab closed — mark the session as ended
        if (msg.Action == "closed")
        {
            var cacheKey = BuildTabCacheKey(msg);
            if (_tabSessionCache.TryGetValue(cacheKey, out var sessionId))
            {
                await _store.StoreAppSessionsAsync(new[]
                {
                    new AppSession { Id = sessionId, ProcessName = string.Empty, EndedAt = DateTime.UtcNow }
                }, ct);
                // Also close the session's app_items (browser_tab, browser_navigation)
                await _store.CloseAppItemsBySessionIdsAsync(new[] { sessionId }, DateTime.UtcNow, ct);
                _tabSessionCache.Remove(cacheKey);
                _logger.LogDebug("Closed tab session: {TabId}", msg.TabId);
            }
            return;
        }

        if (string.IsNullOrEmpty(msg.Url)) return;

        // Extract URL and domain once for reuse
        var (navUrl, navDomain) = ExtractUrlAndDomain(msg.Url);

        // Resolve browser app_id and display name
        var (browserAppId, browserDisplayName) = await ResolveBrowserAppAsync(msg, ct);

        // Build a cache key: browser + tabId (survives browser/extension restarts)
        var tabCacheKey = BuildTabCacheKey(msg);

        // Check if we already have an open session for this tab
        if (_tabSessionCache.TryGetValue(tabCacheKey, out var existingSessionId))
        {
            // Existing tab — update the navigation item
            var navItem = new AppItem
            {
                AppSessionId = existingSessionId,
                ItemType = "browser_navigation",
                Title = msg.Title ?? msg.Url,
                Identifier = msg.Url,
                Url = navUrl,
                Domain = navDomain,
                OpenedAt = DateTime.UtcNow,
            };
            await _store.StoreAppItemsAsync(new[] { navItem }, ct);
            return;
        }

        // New tab — create a new session + browser_tab + navigation item
        var session = new AppSession
        {
            ProcessName = msg.Browser ?? "browser",
            AppDisplayName = browserDisplayName ?? msg.Browser ?? "Browser",
            StartedAt = DateTime.UtcNow,
            MachineId = Environment.MachineName,
            SessionId = SessionInfo.SessionId,
            Platform = GetPlatform(),
            InstalledAppId = browserAppId,
        };

        var rootItem = new AppItem
        {
            AppSessionId = session.Id,
            ItemType = "browser_tab",
            Title = msg.Title ?? "New Tab",
            Identifier = msg.Url,
            Url = navUrl,
            Domain = navDomain,
            OpenedAt = DateTime.UtcNow,
        };

        var navigationItem = new AppItem
        {
            AppSessionId = session.Id,
            ParentItemId = rootItem.Id,
            ItemType = "browser_navigation",
            Title = msg.Title ?? msg.Url,
            Identifier = msg.Url,
            Url = navUrl,
            Domain = navDomain,
            OpenedAt = DateTime.UtcNow,
        };

        await _store.StoreAppSessionsAsync(new[] { session }, ct);
        await _store.StoreAppItemsAsync(new[] { rootItem, navigationItem }, ct);

        _tabSessionCache[tabCacheKey] = session.Id;
        _logger.LogDebug("Created new tab session: {TabId} → {Url}", msg.TabId, TruncateUrl(msg.Url, 80));

        // Limit cache size to prevent memory leaks
        if (_tabSessionCache.Count > 1000)
        {
            // Remove oldest entries
            var keysToRemove = _tabSessionCache.Keys.Take(200).ToList();
            foreach (var k in keysToRemove) _tabSessionCache.Remove(k);
        }
    }

    /// <summary>
    /// Resolve the installed_applications row for a browser process name.
    /// Returns (installedAppId, displayName) or (null, null) if not found.
    /// Caches both values so repeat lookups don't hit the DB.
    /// </summary>
    private async Task<(string? id, string? displayName)> ResolveBrowserAppAsync(BrowserMessage msg, CancellationToken ct)
    {
        var browserName = msg.Browser ?? "chrome";
        if (_browserAppCache.TryGetValue(browserName, out var cached))
            return (cached.id, cached.displayName);

        // Try to find the browser in installed_applications by fuzzy binary name match
        var app = await _store.GetInstalledAppByBinaryNameFuzzyAsync(browserName, ct);
        if (app != null)
        {
            _browserAppCache[browserName] = (app.Id, app.AppName);
            return (app.Id, app.AppName);
        }

        return (null, null);
    }

    private static string BuildTabCacheKey(BrowserMessage msg) =>
        $"browser:{msg.Browser ?? "unknown"}:sess:{msg.BrowserSessionId ?? "0"}:tab:{msg.TabId}";

    private static string GetPlatform()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsMacOS()) return "macOS";
        return "Linux";
    }

    private static string TruncateUrl(string? url, int maxLen)
    {
        if (string.IsNullOrEmpty(url)) return "";
        return url.Length <= maxLen ? url : url[..maxLen] + "...";
    }

    /// <summary>Extract URL and domain from a full URL string.</summary>
    private static (string url, string domain) ExtractUrlAndDomain(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return (string.Empty, string.Empty);

        try
        {
            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                return (uri.ToString().TrimEnd('/'), uri.Host.ToLowerInvariant());
            }
        }
        catch { }

        // Fallback: try to prefix https:// and parse
        try
        {
            var withScheme = rawUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? rawUrl
                : "https://" + rawUrl;
            if (Uri.TryCreate(withScheme, UriKind.Absolute, out var uri))
            {
                return (uri.ToString().TrimEnd('/'), uri.Host.ToLowerInvariant());
            }
        }
        catch { }

        return (rawUrl, string.Empty);
    }

    private sealed class BrowserMessage
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("tabId")]
        public int TabId { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("windowId")]
        public int WindowId { get; set; }

        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("browser")]
        public string? Browser { get; set; }

        [JsonPropertyName("browserSessionId")]
        public string? BrowserSessionId { get; set; }

        [JsonPropertyName("isWindowClosing")]
        public bool IsWindowClosing { get; set; }
    }
}
