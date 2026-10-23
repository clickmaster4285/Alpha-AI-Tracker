using System.IO.Pipes;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Named-pipe IPC (\\.\pipe\&lt;WINDOWS_DLP_PIPE_NAME from APP_IDENTIFIERS&gt;).
/// Ops: GetPolicyVersion, Health, AuthorizeTeardown (verify code), ApplyTeardownGate
/// (unlock from existing teardown.ok — no second code consume).
/// </summary>
public sealed class NamedPipeHost : BackgroundService
{
    /// <summary>Hard cap for service→server removal-verify (never inherit the 60s dlp client).</summary>
    private static readonly TimeSpan VerifyHttpBudget = TimeSpan.FromSeconds(10);

    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly WebBlockState _webBlock;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<NamedPipeHost> _logger;

    public NamedPipeHost(
        DlpConfig cfg,
        PolicyStore store,
        WebBlockState webBlock,
        IHttpClientFactory httpFactory,
        IHostApplicationLifetime lifetime,
        ILogger<NamedPipeHost> logger)
    {
        _cfg = cfg;
        _store = store;
        _webBlock = webBlock;
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

        // Concurrent accept: a stuck HandleClientAsync (abandoned CLI write, slow
        // AuthorizeTeardown HTTP) must not block ApplyTeardownGate / Health.
        const int maxConcurrent = 4;
        using var slot = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await slot.WaitAsync(stoppingToken);
                // LocalSystem default pipe ACL denies interactive users — CLI
                // AuthorizeTeardown / ApplyTeardownGate would get Access Denied.
                var server = CreatePipeServer(_cfg.PipeName, maxConcurrent);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await server.WaitForConnectionAsync(stoppingToken);
                        await HandleClientAsync(server, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        /* shut down */
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Named pipe accept/handle error");
                    }
                    finally
                    {
                        try { await server.DisposeAsync(); } catch { /* ignore */ }
                        try { slot.Release(); } catch { /* ignore */ }
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Named pipe accept loop error");
                try { slot.Release(); } catch { /* ignore */ }
                try { await Task.Delay(500, stoppingToken); } catch { /* ignore */ }
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer(string pipeName, int maxInstances)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, maxInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        var security = new PipeSecurity();
        // SYSTEM (service) + Administrators + interactive users (tracker CLI).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: maxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        // Bound read so an abandoned client cannot hold a pipe instance forever.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(20));
        string? line;
        try
        {
            line = await reader.ReadLineAsync(readCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
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
                "GetWebBlockStatus" => await GetWebBlockStatusJson(ct),
                "AuthorizeTeardown" => await AuthorizeTeardownJson(doc.RootElement, ct),
                "ApplyTeardownGate" => ApplyTeardownGateJson(doc.RootElement),
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
            blockPageEnabled = _cfg.BlockPageEnabled,
            blockPagePort = _cfg.BlockPagePort,
        });
    }

    private async Task<string> GetWebBlockStatusJson(CancellationToken ct)
    {
        // Rehydrate if EnforceWorker has not published yet (or fingerprint short-circuit).
        if (_webBlock.Domains.Count == 0 && _cfg.WebBlock && _cfg.Enabled)
        {
            try
            {
                var (ver, _, bundleJson) = await _store.GetAppliedAsync(ct);
                var bundle = PolicyBundleParser.Parse(bundleJson);
                var domains = PolicyBundleParser.ActiveWebBlockDomains(bundle, DateTimeOffset.UtcNow);
                if (domains.Count > 0)
                    _webBlock.Set(ver, domains);
            }
            catch { /* keep empty */ }
        }

        var baseUrl = $"http://127.0.0.1:{_cfg.BlockPagePort}/";
        var (version, liveDomains, updatedAt, pageUrl) = _webBlock.Snapshot(baseUrl);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            policyVersion = version,
            domains = liveDomains,
            updatedAt = updatedAt == DateTimeOffset.MinValue ? (string?)null : updatedAt.ToString("o"),
            blockPageUrl = pageUrl,
            blockPageEnabled = _cfg.BlockPageEnabled && _cfg.WebBlock && _cfg.Enabled,
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

        using var verifyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        verifyCts.CancelAfter(VerifyHttpBudget);

        try
        {
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

            using var resp = await client.SendAsync(req, verifyCts.Token);
            var body = await resp.Content.ReadAsStringAsync(verifyCts.Token);
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

            return CompleteTeardown(storedPurpose);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("AuthorizeTeardown removal-verify timed out after {Sec}s",
                VerifyHttpBudget.TotalSeconds);
            return JsonSerializer.Serialize(new { ok = false, error = "verify_timeout" });
        }
    }

    /// <summary>
    /// CLI already verified via DeviceAuth and wrote teardown.ok — unlock ACL + stop
    /// without consuming another removal code.
    /// </summary>
    private string ApplyTeardownGateJson(JsonElement root)
    {
        var purpose = root.TryGetProperty("purpose", out var p) ? (p.GetString() ?? "both").Trim() : "both";
        var gate = TeardownGate.PathIn(_cfg.DataDir);
        if (!File.Exists(gate))
            return JsonSerializer.Serialize(new { ok = false, error = "teardown_ok_missing" });

        try
        {
            var text = File.ReadAllText(gate);
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("purpose=", StringComparison.OrdinalIgnoreCase))
                    purpose = line["purpose=".Length..].Trim();
            }
        }
        catch { /* keep request purpose */ }

        return CompleteTeardown(purpose);
    }

    private string CompleteTeardown(string purpose)
    {
        TeardownGate.MarkAuthorized(_cfg.DataDir, purpose);

        // CRITICAL: return ok=true on the pipe FIRST. USBSTOR + sc sdset can take
        // seconds (or hang). Doing that before WriteLineAsync deadlocked the CLI:
        // client hit a read timeout, Dispose waited on the service, service held the
        // pipe while blocked in sc.exe — protected-uninstall froze on
        // "Unlocking DLP service..." and Apps & features then hit Error 32 (files locked).
        _ = Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var usb = UsbStorEnforcer.Apply(block: false);
                    _logger.LogWarning(
                        "Teardown restored USBSTOR (changed={Changed}, detail={Detail}, start={Start})",
                        usb.Changed, usb.Detail, usb.DesiredStart);
                    var web = DomainBlockEnforcer.Clear();
                    _webBlock.Set(0, Array.Empty<string>());
                    try { BlockPageCertStore.Uninstall(_cfg, _logger); } catch { /* ignore */ }
                    _logger.LogWarning(
                        "Teardown cleared AlphaAI-DLP hosts sink (changed={Changed}, detail={Detail})",
                        web.Changed, web.Detail);
                    ServiceAclHardener.TryApplyOpen(_logger);
                }

                _logger.LogWarning(
                    "Authorized DLP service teardown (purpose={Purpose}) — stopping service",
                    purpose);
                Thread.Sleep(300);
                _lifetime.StopApplication();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-reply teardown work failed");
                try { _lifetime.StopApplication(); } catch { /* ignore */ }
            }
        });

        return JsonSerializer.Serialize(new { ok = true, purpose, stopping = true });
    }
}
