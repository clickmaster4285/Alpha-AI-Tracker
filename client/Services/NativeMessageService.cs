using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    // Cache: browser process name → installed_app entry (looked up on first event)
    private readonly Dictionary<string, string> _browserAppCache = new(StringComparer.OrdinalIgnoreCase);
    // Cache: browser process name + tabId → current AppSession.Id
    private readonly Dictionary<string, string> _tabSessionCache = new(StringComparer.OrdinalIgnoreCase);

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
                        using var handler = await listener.AcceptAsync(stoppingToken);
                        await HandleConnectionAsync(handler, stoppingToken);
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
        try
        {
            var buffer = new byte[8192];
            var bytesRead = await handler.ReceiveAsync(buffer, SocketFlags.None, ct);
            if (bytesRead == 0) return;

            var json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var msg = JsonSerializer.Deserialize<BrowserMessage>(json);
            if (msg == null) return;

            _logger.LogDebug("Browser event: {Action} tab={TabId} url={Url}",
                msg.Action, msg.TabId, TruncateUrl(msg.Url, 80));

            await ProcessMessageAsync(msg, ct);

            // Send acknowledgment
            var response = JsonSerializer.Serialize(new { status = "ok", tabId = msg.TabId });
            var responseBytes = Encoding.UTF8.GetBytes(response);
            await handler.SendAsync(responseBytes, SocketFlags.None, ct);
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

    private async Task ProcessMessageAsync(BrowserMessage msg, CancellationToken ct)
    {
        // Ping messages — just log at debug level
        if (msg.Action == "ping") return;

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

        // Resolve browser app_id
        var browserAppId = await ResolveBrowserAppIdAsync(msg, ct);

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
                OpenedAt = DateTime.UtcNow,
            };
            await _store.StoreAppItemsAsync(new[] { navItem }, ct);
            return;
        }

        // New tab — create a new session + browser_tab + navigation item
        var session = new AppSession
        {
            ProcessName = msg.Browser ?? "browser",
            AppDisplayName = browserAppId ?? msg.Browser ?? "Browser",
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
            OpenedAt = DateTime.UtcNow,
        };

        var navigationItem = new AppItem
        {
            AppSessionId = session.Id,
            ParentItemId = rootItem.Id,
            ItemType = "browser_navigation",
            Title = msg.Title ?? msg.Url,
            Identifier = msg.Url,
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

    private async Task<string?> ResolveBrowserAppIdAsync(BrowserMessage msg, CancellationToken ct)
    {
        var browserName = msg.Browser ?? "chrome";
        if (_browserAppCache.TryGetValue(browserName, out var cachedId))
            return cachedId;

        // Try to find the browser in installed_applications by fuzzy binary name match
        var app = await _store.GetInstalledAppByBinaryNameFuzzyAsync(browserName, ct);
        if (app != null)
        {
            _browserAppCache[browserName] = app.Id;
            return app.Id;
        }

        return null;
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
