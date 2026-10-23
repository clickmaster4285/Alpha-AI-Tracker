using System.Net;
using System.Text;

namespace AlphaAITracker.Service;

/// <summary>
/// Localhost branded interstitial for web blocks (no TLS MITM).
/// Listens on <c>127.0.0.1:{port}</c> and <c>127.0.0.1:80</c> so hosts sink to
/// loopback can show this HTML for HTTP navigations (Host header = blocked domain).
/// HTTPS still needs the tracker to open this page (or a MITM CA — not used here).
/// </summary>
public sealed class BlockPageServer : BackgroundService
{
    public const int DefaultPort = 47832;

    private readonly DlpConfig _cfg;
    private readonly WebBlockState _state;
    private readonly ILogger<BlockPageServer> _logger;
    private HttpListener? _listener;

    public BlockPageServer(DlpConfig cfg, WebBlockState state, ILogger<BlockPageServer> logger)
    {
        _cfg = cfg;
        _state = state;
        _logger = logger;
    }

    public string BaseUrl => $"http://127.0.0.1:{_cfg.BlockPagePort}/";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.Enabled || !_cfg.WebBlock || !_cfg.BlockPageEnabled)
        {
            _logger.LogInformation(
                "BlockPageServer parked (enabled={Enabled}, webBlock={Web}, blockPage={Page})",
                _cfg.Enabled, _cfg.WebBlock, _cfg.BlockPageEnabled);
            return;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            _logger.LogInformation("BlockPageServer parked (unsupported OS)");
            return;
        }

        var prefixes = new List<string>
        {
            $"http://127.0.0.1:{_cfg.BlockPagePort}/",
            "http://127.0.0.1:80/",
        };
        try
        {
            _listener = new HttpListener();
            foreach (var p in prefixes)
            {
                try
                {
                    _listener.Prefixes.Add(p);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skip prefix {Prefix}", p);
                }
            }
            _listener.Start();
            _logger.LogInformation("Block page listening on {Urls}", string.Join(", ", _listener.Prefixes.Cast<string>()));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BlockPageServer failed to bind");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext? ctx = null;
            try
            {
                var get = _listener.GetContextAsync();
                var done = await Task.WhenAny(get, Task.Delay(Timeout.Infinite, stoppingToken));
                if (done != get) break;
                ctx = await get;
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "BlockPageServer accept error");
                continue;
            }

            try
            {
                await WriteResponseAsync(ctx!, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "BlockPageServer write error");
                try { ctx!.Response.Abort(); } catch { /* ignore */ }
            }
        }

        try { _listener.Stop(); } catch { /* ignore */ }
        try { _listener.Close(); } catch { /* ignore */ }
        _listener = null;
    }

    public override void Dispose()
    {
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        base.Dispose();
    }

    private Task WriteResponseAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers["Cache-Control"] = "no-store";
        res.Headers["X-Content-Type-Options"] = "nosniff";

        if (!string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            res.StatusCode = 405;
            res.Close();
            return Task.CompletedTask;
        }

        var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
        if (path.Length == 0) path = "/";

        if (path is "/health" or "/healthz")
        {
            var bytes = Encoding.UTF8.GetBytes("ok");
            res.StatusCode = 200;
            res.ContentType = "text/plain; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            if (!string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
                res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
            return Task.CompletedTask;
        }

        var domain = (req.QueryString["domain"] ?? "").Trim();
        if (domain.Length == 0)
        {
            // Hosts sink → 127.0.0.1:80 — browser sends Host: youtube.com
            var host = (req.UserHostName ?? req.Headers["Host"] ?? "").Trim();
            var colon = host.IndexOf(':');
            if (colon > 0) host = host[..colon];
            if (!string.IsNullOrWhiteSpace(host) &&
                !host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                domain = host;
        }
        var domains = _state.Domains;
        var html = BuildHtml(domain, domains);
        var body = Encoding.UTF8.GetBytes(html);
        res.StatusCode = 200;
        res.ContentType = "text/html; charset=utf-8";
        res.ContentLength64 = body.Length;
        if (!string.Equals(req.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            res.OutputStream.Write(body, 0, body.Length);
        res.Close();
        return Task.CompletedTask;
    }

    internal static string BuildHtml(string attemptedDomain, IReadOnlyList<string> blockedDomains)
    {
        var titleDomain = string.IsNullOrWhiteSpace(attemptedDomain) ? "this site" : attemptedDomain.Trim();
        var esc = (string s) => WebUtility.HtmlEncode(s);
        var list = blockedDomains.Count == 0
            ? "<li class=\"muted\">No domains are currently in the active block set.</li>"
            : string.Join("\n", blockedDomains.Take(40).Select(d => $"<li><code>{esc(d)}</code></li>"))
              + (blockedDomains.Count > 40 ? $"\n<li class=\"muted\">…and {blockedDomains.Count - 40} more</li>" : "");

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Access blocked — Alpha AI Tracker</title>
  <style>
    :root {
      --bg: #0f1419;
      --panel: #1a222c;
      --text: #e8eef4;
      --muted: #8b9aab;
      --accent: #c45c26;
      --line: #2a3542;
      --ok: #3d8b6e;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0; min-height: 100vh;
      font-family: "Segoe UI", system-ui, sans-serif;
      background:
        radial-gradient(1200px 600px at 10% -10%, #243041 0%, transparent 55%),
        radial-gradient(900px 500px at 100% 0%, #2a1f18 0%, transparent 50%),
        var(--bg);
      color: var(--text);
      display: flex; align-items: center; justify-content: center;
      padding: 2rem 1.25rem;
    }
    main {
      width: min(560px, 100%);
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 12px;
      padding: 2rem 1.75rem 1.5rem;
      box-shadow: 0 24px 48px rgba(0,0,0,.35);
    }
    .brand {
      font-size: .75rem; letter-spacing: .14em; text-transform: uppercase;
      color: var(--accent); font-weight: 650; margin-bottom: .75rem;
    }
    h1 { font-size: 1.55rem; line-height: 1.25; margin: 0 0 .75rem; font-weight: 650; }
    p { margin: 0 0 1rem; color: var(--muted); line-height: 1.55; font-size: .98rem; }
    .domain {
      display: inline-block; margin: .25rem 0 1rem;
      padding: .35rem .65rem; border-radius: 6px;
      background: #121820; border: 1px solid var(--line);
      font-family: ui-monospace, Consolas, monospace; font-size: .92rem; color: var(--text);
    }
    h2 { font-size: .8rem; text-transform: uppercase; letter-spacing: .08em; color: var(--muted); margin: 1.25rem 0 .5rem; }
    ul { margin: 0; padding-left: 1.1rem; max-height: 180px; overflow: auto; color: var(--muted); font-size: .9rem; }
    li { margin: .2rem 0; }
    li.muted { list-style: none; margin-left: -1.1rem; }
    code { color: var(--text); font-size: .88rem; }
    footer {
      margin-top: 1.5rem; padding-top: 1rem; border-top: 1px solid var(--line);
      font-size: .8rem; color: var(--muted);
    }
    .badge {
      display: inline-flex; align-items: center; gap: .4rem;
      font-size: .75rem; color: var(--ok); margin-bottom: 1rem;
    }
    .badge::before {
      content: ""; width: .5rem; height: .5rem; border-radius: 50%; background: var(--ok);
    }
  </style>
</head>
<body>
  <main>
    <div class="brand">Alpha AI Tracker · Security &amp; DLP</div>
    <div class="badge">Policy enforcement active</div>
    <h1>Unauthorized access</h1>
    <p>
      Access to <strong>{{esc(titleDomain)}}</strong> has been blocked by your organization
      through <strong>Alpha AI Tracker</strong>. This site is not available on this device
      under the current web-blocker policy.
    </p>
    <div class="domain">{{esc(titleDomain)}}</div>
    <p>
      If you believe you need access, contact your administrator. Closing this tab does not
      remove the block — the destination remains unreachable while the policy is published.
    </p>
    <h2>Active blocked domains (sample)</h2>
    <ul>
      {{list}}
    </ul>
    <footer>
      Unauthorized by Alpha AI Tracker. Do not attempt to bypass company security controls.
    </footer>
  </main>
</body>
</html>
""";
    }
}
