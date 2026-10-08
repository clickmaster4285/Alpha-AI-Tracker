using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services.Dlp;

/// <summary>Drains local dlp_alerts to POST /dlp-alerts/sync (DeviceAuth). Agent-only.</summary>
public sealed class DlpAlertSync : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly HttpClient _http;
    private readonly ILogger<DlpAlertSync> _logger;

    public DlpAlertSync(AppConfig config, ILogStore store, HttpClient http, ILogger<DlpAlertSync> logger)
    {
        _config = config;
        _store = store;
        _http = http;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DlpAlertSync: drain failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        var employee = await _store.GetEmployeeInfoAsync(ct);
        if (employee == null || (string.IsNullOrWhiteSpace(employee.DeviceToken) && string.IsNullOrWhiteSpace(employee.Token)))
            return;

        var batch = await _store.GetUnsentDlpAlertsAsync(100, ct);
        if (batch.Count == 0) return;

        var body = new
        {
            employeeId = employee.EmployeeId,
            token = employee.Token ?? "",
            entries = batch.Select(a => new
            {
                id = a.Id,
                ruleId = a.RuleId,
                trigger = a.Trigger,
                severity = a.Severity,
                fileOrUrl = a.FileOrUrl,
                detailJson = a.DetailJson,
                eventAt = a.EventAt.ToUniversalTime().ToString("O"),
            }).ToList(),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_config.ServerUrl?.TrimEnd('/')}/api/v1/dlp-alerts/sync")
        {
            Content = JsonContent.Create(body, options: JsonOpts),
        };
        if (!string.IsNullOrWhiteSpace(employee.DeviceToken))
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Device", employee.DeviceToken);
        else
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", employee.Token);
        client.Services.ClientIdentityHeaders.Apply(request);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("DlpAlertSync: sync => {Status}", (int)response.StatusCode);
            return;
        }

        await _store.MarkDlpAlertsSentAsync(batch.Select(a => a.Id).ToList(), ct);
        _logger.LogInformation("DlpAlertSync: synced {Count} alert(s)", batch.Count);
    }
}
