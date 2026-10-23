using System.Diagnostics;
using System.Text.Json;
using client.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// User-session web-block UX: refresh denied domains from the DLP pipe and open the
/// localhost branded page when browser accessibility sees a matching URL/title.
/// Does not MITM HTTPS — the real site remains hard-blocked (hosts + DoH-off + FW).
/// </summary>
public sealed class WebBlockUxService : BackgroundService, IWebBlockHitSink
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HitCooldown = TimeSpan.FromMinutes(5);

    private readonly ILogger<WebBlockUxService> _logger;
    private readonly object _gate = new();
    private string[] _domains = Array.Empty<string>();
    private string _blockPageUrl = $"http://127.0.0.1:{47832}/";
    private bool _pageEnabled;
    private readonly Dictionary<string, DateTimeOffset> _lastShown = new(StringComparer.OrdinalIgnoreCase);

    public WebBlockUxService(ILogger<WebBlockUxService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("WebBlockUxService parked (Windows-only UX)");
            return;
        }

        _logger.LogInformation("WebBlockUxService running (pipe poll {Sec}s)", RefreshInterval.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshFromPipeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebBlockUxService refresh failed");
            }

            try
            {
                await Task.Delay(RefreshInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void OnBrowserUrl(string? url, string? processName)
        => OnBrowserNavigation(url, windowTitle: null, processName);

    public void OnBrowserNavigation(string? url, string? windowTitle, string? processName)
    {
        string[] domains;
        string pageBase;
        bool enabled;
        lock (_gate)
        {
            enabled = _pageEnabled;
            domains = _domains;
            pageBase = _blockPageUrl;
        }

        if (!enabled || domains.Length == 0) return;

        string? matched = null;
        if (!string.IsNullOrWhiteSpace(url) &&
            Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" &&
            !uri.IsLoopback)
        {
            var host = uri.IdnHost;
            if (!string.IsNullOrWhiteSpace(host) && HostMatches(host, domains, out var m))
                matched = m;
        }

        // Chrome error pages often leave Url empty / chrome-error:// — scan title + raw url text.
        if (matched is null)
            matched = FindDomainInText(url, domains) ?? FindDomainInText(windowTitle, domains);

        if (matched is null) return;

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_lastShown.TryGetValue(matched, out var prev) && now - prev < HitCooldown)
                return;
            _lastShown[matched] = now;
            if (_lastShown.Count > 200)
            {
                foreach (var old in _lastShown.Where(kv => now - kv.Value > HitCooldown).Select(kv => kv.Key).ToList())
                    _lastShown.Remove(old);
            }
        }

        var target = pageBase.TrimEnd('/') + "/?domain=" + Uri.EscapeDataString(matched);
        _logger.LogInformation(
            "Web block hit: {Host} via {Proc} → opening {Page}",
            matched, processName ?? "?", target);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open web block page for {Host}", matched);
        }
    }

    private async Task RefreshFromPipeAsync(CancellationToken ct)
    {
        var (ok, body) = await DlpPipeClient.SendAsync(
            new { op = "GetWebBlockStatus" },
            responseTimeoutMs: 3_000,
            ct: ct).ConfigureAwait(false);
        if (!ok || string.IsNullOrWhiteSpace(body)) return;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean()) return;

            var enabled = root.TryGetProperty("blockPageEnabled", out var pe) && pe.GetBoolean();
            var pageUrl = root.TryGetProperty("blockPageUrl", out var pu)
                ? (pu.GetString() ?? "").Trim()
                : "";
            var list = new List<string>();
            if (root.TryGetProperty("domains", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var d = el.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(d)) list.Add(d);
                }
            }

            lock (_gate)
            {
                _pageEnabled = enabled;
                _domains = list.ToArray();
                if (!string.IsNullOrWhiteSpace(pageUrl))
                    _blockPageUrl = pageUrl;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetWebBlockStatus parse failed");
        }
    }

    internal static string? FindDomainInText(string? text, IReadOnlyList<string> domains)
    {
        if (string.IsNullOrWhiteSpace(text) || domains.Count == 0) return null;
        var t = text.ToLowerInvariant();
        foreach (var raw in domains)
        {
            var d = (raw ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (d.StartsWith("*.", StringComparison.Ordinal)) d = d[2..];
            if (d.StartsWith("www.", StringComparison.Ordinal)) d = d[4..];
            if (d.Length < 3) continue;
            if (t.Contains(d, StringComparison.Ordinal))
                return d;
        }
        return null;
    }

    internal static bool HostMatches(string host, IReadOnlyList<string> domains, out string matched)
    {
        matched = "";
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.StartsWith("www.", StringComparison.Ordinal)) h = h[4..];

        foreach (var raw in domains)
        {
            var d = (raw ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (d.Length == 0) continue;
            if (d.StartsWith("*.", StringComparison.Ordinal)) d = d[2..];
            if (d.StartsWith("www.", StringComparison.Ordinal)) d = d[4..];

            if (h == d || h.EndsWith("." + d, StringComparison.Ordinal))
            {
                matched = d;
                return true;
            }
        }

        return false;
    }
}
