using System.Net.Http.Headers;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Pulls versioned DLP policy bundles (GET /api/v1/dlp/policy) on a poll interval.
/// Parks when ALPHA_DLP_ENABLED=false or ServerUrl/DeviceToken are missing.
/// </summary>
public sealed class PolicyPullWorker : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<PolicyPullWorker> _logger;

    public PolicyPullWorker(
        DlpConfig cfg,
        PolicyStore store,
        IHttpClientFactory httpFactory,
        ILogger<PolicyPullWorker> logger)
    {
        _cfg = cfg;
        _store = store;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.Enabled)
        {
            _logger.LogInformation("PolicyPullWorker parked (ALPHA_DLP_ENABLED=false)");
            return;
        }
        if (string.IsNullOrWhiteSpace(_cfg.ServerUrl))
        {
            _logger.LogWarning("PolicyPullWorker parked — ALPHA_SERVER_URL not set");
            return;
        }
        if (string.IsNullOrWhiteSpace(_cfg.DeviceToken))
        {
            _logger.LogWarning(
                "PolicyPullWorker parked — ALPHA_DEVICE_TOKEN not set " +
                "(login tracker so SQLite has device_token, or set it in .env)");
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        var interval = TimeSpan.FromSeconds(Math.Max(30, _cfg.PolicyPollSec));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PullOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DLP policy pull failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PullOnceAsync(CancellationToken ct)
    {
        var (version, _, _) = await _store.GetAppliedAsync(ct);
        var client = _httpFactory.CreateClient("dlp");
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{_cfg.ServerUrl}/api/v1/dlp/policy?sinceVersion={version}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Device", _cfg.DeviceToken);
        req.Headers.TryAddWithoutValidation("X-Client-Platform", "windows");

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 401)
            {
                _logger.LogWarning(
                    "DLP policy pull HTTP 401 — device token invalid/revoked. " +
                    "Re-login the Alpha AI Tracker client (fresh device_token), or set ALPHA_DEVICE_TOKEN in .env and restart the service.");
            }
            else
            {
                _logger.LogWarning("DLP policy pull HTTP {Status}", (int)resp.StatusCode);
            }
            return;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        var unchanged = root.TryGetProperty("unchanged", out var u) && u.GetBoolean();
        var newVersion = root.TryGetProperty("policyVersion", out var pv) ? pv.GetInt64() : 0;
        var hash = root.TryGetProperty("hash", out var h) ? h.GetString() ?? "" : "";

        if (unchanged || newVersion <= version)
        {
            _logger.LogDebug("DLP policy unchanged at version {Version}", version);
            return;
        }

        var bundle = root.TryGetProperty("bundle", out var b) ? b.GetRawText() : "{}";
        await _store.SavePolicyAsync(newVersion, hash, bundle, ct);
        await _store.AuditAsync("policy_applied", $"version={newVersion}", ct);
    }
}
