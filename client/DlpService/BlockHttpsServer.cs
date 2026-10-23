using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AlphaAITracker.Service;

/// <summary>
/// Accepts HTTPS on 127.0.0.1:443 for hosts-sunk domains and returns the branded
/// block HTML in the same browser tab. Uses a local CA installed into Root
/// (loopback-only — no upstream MITM of the real site).
/// </summary>
public sealed class BlockHttpsServer : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly WebBlockState _state;
    private readonly ILogger<BlockHttpsServer> _logger;
    private TcpListener? _listener;

    public BlockHttpsServer(DlpConfig cfg, WebBlockState state, ILogger<BlockHttpsServer> logger)
    {
        _cfg = cfg;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.Enabled || !_cfg.WebBlock || !_cfg.BlockPageEnabled)
        {
            _logger.LogInformation("BlockHttpsServer parked");
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("BlockHttpsServer parked (Windows-only)");
            return;
        }

        try
        {
            BlockPageCertStore.EnsureCa(_cfg, _logger);
            _listener = new TcpListener(IPAddress.Loopback, 443);
            _listener.Start();
            _logger.LogInformation("Block HTTPS page listening on 127.0.0.1:443");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BlockHttpsServer failed to bind 127.0.0.1:443");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                var accept = _listener.AcceptTcpClientAsync(stoppingToken);
                client = await accept.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "BlockHttpsServer accept error");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client!, stoppingToken), CancellationToken.None);
        }

        try { _listener.Stop(); } catch { /* ignore */ }
        _listener = null;
    }

    public override void Dispose()
    {
        try { _listener?.Stop(); } catch { /* ignore */ }
        base.Dispose();
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                string? sni = null;
                var options = new SslServerAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false,
                    ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                    ServerCertificateSelectionCallback = (_, hostName) =>
                    {
                        sni = hostName;
                        return BlockPageCertStore.GetLeaf(hostName ?? "blocked.local", _cfg, _logger);
                    },
                };
                await ssl.AuthenticateAsServerAsync(options, ct).ConfigureAwait(false);

                // Drain request headers (ignore body).
                var buf = new byte[8192];
                var total = 0;
                while (total < buf.Length)
                {
                    var n = await ssl.ReadAsync(buf.AsMemory(total, buf.Length - total), ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    total += n;
                    if (IndexOfHeaderEnd(buf, total) >= 0) break;
                }

                var domain = (sni ?? "").Trim();
                if (domain.Length == 0)
                    domain = TryHostFromRequest(buf, total) ?? "this site";

                var html = BlockPageServer.BuildHtml(domain, _state.Domains);
                var body = Encoding.UTF8.GetBytes(html);
                var header =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    "Content-Length: " + body.Length + "\r\n" +
                    "Cache-Control: no-store\r\n" +
                    "Connection: close\r\n" +
                    "X-Content-Type-Options: nosniff\r\n" +
                    "\r\n";
                var hdrBytes = Encoding.ASCII.GetBytes(header);
                await ssl.WriteAsync(hdrBytes, ct).ConfigureAwait(false);
                await ssl.WriteAsync(body, ct).ConfigureAwait(false);
                await ssl.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "BlockHttpsServer client handler failed");
            }
        }
    }

    private static int IndexOfHeaderEnd(byte[] buf, int len)
    {
        for (var i = 0; i + 3 < len; i++)
        {
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static string? TryHostFromRequest(byte[] buf, int len)
    {
        var text = Encoding.ASCII.GetString(buf, 0, len);
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                var h = t["Host:".Length..].Trim();
                var c = h.IndexOf(':');
                if (c > 0) h = h[..c];
                return h;
            }
        }
        return null;
    }
}
