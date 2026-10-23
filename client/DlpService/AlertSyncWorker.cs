using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Drains unsynced local DLP alerts to POST /api/v1/dlp/alerts/sync.
/// Waits for credentials instead of parking forever (late tracker login).
/// </summary>
public sealed class AlertSyncWorker : BackgroundService
{
    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AlertSyncWorker> _logger;
    private bool _loggedWaiting;

    public AlertSyncWorker(
        DlpConfig cfg,
        PolicyStore store,
        IHttpClientFactory httpFactory,
        ILogger<AlertSyncWorker> logger)
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
            _logger.LogInformation("AlertSyncWorker parked (ALPHA_DLP_ENABLED=false)");
            return;
        }

        await _store.InitializeAsync(stoppingToken);
        var interval = TimeSpan.FromSeconds(Math.Max(15, _cfg.AlertSyncSec));
        var waitInterval = TimeSpan.FromSeconds(15);

        while (!stoppingToken.IsCancellationRequested)
        {
            _cfg.RefreshCredentials();
            if (string.IsNullOrWhiteSpace(_cfg.ServerUrl) || string.IsNullOrWhiteSpace(_cfg.DeviceToken))
            {
                if (!_loggedWaiting)
                {
                    _logger.LogWarning(
                        "AlertSyncWorker waiting for credentials " +
                        "(login Alpha AI Tracker so device.auth is written)");
                    _loggedWaiting = true;
                }
                try { await Task.Delay(waitInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (_loggedWaiting)
            {
                _logger.LogInformation("AlertSyncWorker credentials ready — draining alerts");
                _loggedWaiting = false;
            }

            try
            {
                await DrainOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DLP alert sync failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        var batch = await _store.GetUnsyncedAlertsAsync(100, ct);
        if (batch.Count == 0) return;

        var payload = new
        {
            alerts = batch.Select(a => new
            {
                id = a.Id,
                ruleId = a.RuleId,
                policyVersion = a.PolicyVersion,
                type = a.Type,
                severity = a.Severity,
                actionTaken = a.ActionTaken,
                occurredAt = a.OccurredAt.UtcDateTime,
                evidence = a.Evidence ?? new Dictionary<string, string>(),
            }).ToArray(),
        };

        var json = JsonSerializer.Serialize(payload);
        var client = _httpFactory.CreateClient("dlp");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.ServerUrl}/api/v1/dlp/alerts/sync")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Device", _cfg.DeviceToken);
        req.Headers.TryAddWithoutValidation("X-Client-Platform", "windows");

        using var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 401)
            {
                _logger.LogWarning(
                    "DLP alert sync HTTP 401 — device token invalid/revoked. Re-login the tracker or set ALPHA_DEVICE_TOKEN.");
            }
            else
            {
                _logger.LogWarning("DLP alert sync HTTP {Status}: {Body}", (int)resp.StatusCode, body);
            }
            return;
        }

        using var doc = JsonDocument.Parse(body);
        var accepted = new List<string>();
        if (doc.RootElement.TryGetProperty("acceptedIds", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                var id = el.GetString();
                if (!string.IsNullOrEmpty(id)) accepted.Add(id);
            }
        }
        // Absent/unparseable acceptedIds → mark all (idempotent upserts on server).
        if (accepted.Count == 0)
            accepted.AddRange(batch.Select(a => a.Id));

        await _store.MarkAlertsSyncedAsync(accepted, ct);
        _logger.LogInformation("Synced {Count} DLP alerts", accepted.Count);
    }
}
