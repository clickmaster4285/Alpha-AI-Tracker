using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Named-pipe IPC (\\.\pipe\&lt;WINDOWS_DLP_PIPE_NAME from APP_IDENTIFIERS&gt;).
/// Ops: GetPolicyVersion, Health, AuthorizeTeardown (removal-code → unlock ACL + stop).
/// </summary>
public sealed class NamedPipeHost : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<NamedPipeHost> _logger;

    public NamedPipeHost(
        DlpConfig cfg,
        PolicyStore store,
        IHttpClientFactory httpFactory,
        IHostApplicationLifetime lifetime,
        ILogger<NamedPipeHost> logger)
    {
        _cfg = cfg;
        _store = store;
        _httpFactory = httpFactory;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.Enabled)
        {
            _logger.LogInformation("NamedPipeHost parked (ALPHA_DLP_ENABLED=false)");
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("NamedPipeHost parked (Windows-only)");
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        _logger.LogInformation("Named pipe listening on {Pipe}", _cfg.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _cfg.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(stoppingToken);
                await HandleClientAsync(server, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Named pipe accept/handle error");
                try { await Task.Delay(500, stoppingToken); } catch { /* ignore */ }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(ct);
        if (string.IsNullOrWhiteSpace(line)) return;

        string response;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var op = doc.RootElement.TryGetProperty("op", out var o) ? o.GetString() : "";
            response = op switch
            {
                "GetPolicyVersion" => await GetPolicyVersionJson(ct),
                "Health" => await HealthJson(ct),
                "AuthorizeTeardown" => await AuthorizeTeardownJson(doc.RootElement, ct),
                _ => JsonSerializer.Serialize(new { ok = false, error = "unknown_op" }),
            };
        }
        catch (Exception ex)
        {
            response = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        await writer.WriteLineAsync(response.AsMemory(), ct);
    }

    private async Task<string> GetPolicyVersionJson(CancellationToken ct)
    {
        var (version, hash, _) = await _store.GetAppliedAsync(ct);
        return JsonSerializer.Serialize(new { ok = true, policyVersion = version, hash });
    }

    private async Task<string> HealthJson(CancellationToken ct)
    {
        var (version, _, _) = await _store.GetAppliedAsync(ct);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            enabled = _cfg.Enabled,
            policyVersion = version,
            usbBlock = _cfg.UsbBlock,
            webBlock = _cfg.WebBlock,
        });
    }

    private async Task<string> AuthorizeTeardownJson(JsonElement root, CancellationToken ct)
    {
        var code = root.TryGetProperty("code", out var c) ? (c.GetString() ?? "").Trim() : "";
        var purpose = root.TryGetProperty("purpose", out var p) ? (p.GetString() ?? "both").Trim() : "both";
        if (string.IsNullOrEmpty(code))
            return JsonSerializer.Serialize(new { ok = false, error = "code_required" });

        _cfg.RefreshCredentials();
        if (string.IsNullOrWhiteSpace(_cfg.ServerUrl) || string.IsNullOrWhiteSpace(_cfg.DeviceToken))
            return JsonSerializer.Serialize(new { ok = false, error = "credentials_missing" });

        var client = _httpFactory.CreateClient("dlp");
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{_cfg.ServerUrl}/api/v1/devices/removal-verify")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { code, purpose }),
                Encoding.UTF8,
                "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Device", _cfg.DeviceToken);
        req.Headers.TryAddWithoutValidation("X-Client-Platform", "windows");

        using var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("AuthorizeTeardown verify failed HTTP {Status}: {Body}",
                (int)resp.StatusCode, body);
            return JsonSerializer.Serialize(new { ok = false, error = "invalid_code", status = (int)resp.StatusCode });
        }

        var valid = false;
        var storedPurpose = purpose;
        try
        {
            using var doc = JsonDocument.Parse(body);
            valid = doc.RootElement.TryGetProperty("valid", out var v) && v.GetBoolean();
            if (doc.RootElement.TryGetProperty("purpose", out var sp))
                storedPurpose = sp.GetString() ?? storedPurpose;
        }
        catch { /* treat as invalid */ }

        if (!valid)
            return JsonSerializer.Serialize(new { ok = false, error = "invalid_code" });

        TeardownGate.MarkAuthorized(_cfg.DataDir, storedPurpose);
        if (OperatingSystem.IsWindows())
            ServiceAclHardener.TryApplyOpen(_logger);

        _logger.LogWarning(
            "Authorized DLP service teardown (purpose={Purpose}) — stopping service",
            storedPurpose);

        // Reply first, then stop so the client gets ok=true.
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(400); } catch { /* ignore */ }
            _lifetime.StopApplication();
        });

        return JsonSerializer.Serialize(new { ok = true, purpose = storedPurpose, stopping = true });
    }
}
