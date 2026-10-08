using System.Text.Json;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Dlp;
using client.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services.Dlp;

/// <summary>
/// Runs only inside <c>client --dlp</c>: pulls active rules, listens on IPC, writes alerts.
/// </summary>
public sealed class DlpEngine : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly HttpClient _http;
    private readonly ILogger<DlpEngine> _logger;
    private readonly object _rulesLock = new();
    private List<DlpRule> _rules = new();
    private DlpIpcListener? _listener;

    public DlpEngine(AppConfig config, ILogStore store, HttpClient http, ILogger<DlpEngine> logger)
    {
        _config = config;
        _store = store;
        _http = http;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DlpEngine: starting agent (IPC + rule pull)");
        _listener = new DlpIpcListener(_config.DlpIpcName, _logger);
        _listener.EventReceived += OnEvent;
        _listener.Start();

        // Initial pull + periodic refresh
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshRulesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DlpEngine: rule pull failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_listener != null)
        {
            _listener.EventReceived -= OnEvent;
            await _listener.DisposeAsync();
            _listener = null;
        }
        await base.StopAsync(cancellationToken);
    }

    private void OnEvent(DlpIpcEvent evt)
    {
        _ = Task.Run(async () =>
        {
            try { await HandleEventAsync(evt, CancellationToken.None); }
            catch (Exception ex) { _logger.LogDebug(ex, "DlpEngine: handle event failed"); }
        });
    }

    private async Task HandleEventAsync(DlpIpcEvent evt, CancellationToken ct)
    {
        var triggers = MapTriggers(evt.Type);
        if (triggers.Length == 0) return;

        List<DlpRule> snapshot;
        lock (_rulesLock) snapshot = _rules.ToList();

        if (snapshot.Count == 0)
        {
            _logger.LogWarning(
                "DlpEngine: event {Type} ignored — no rules cached yet (pull /dlp-rules/active failed or empty)",
                evt.Type);
            return;
        }

        foreach (var rule in snapshot.Where(r =>
                     r.Enabled &&
                     triggers.Any(t => string.Equals(r.Trigger, t, StringComparison.OrdinalIgnoreCase))))
        {
            if (!DlpPatternMatcher.Matches(rule.Pattern, evt.Subject)
                && !DlpPatternMatcher.Matches(rule.Pattern, evt.Detail))
                continue;

            // Persist the rule's trigger (admin vocabulary), not the sensor type.
            var ruleTrigger = string.IsNullOrWhiteSpace(rule.Trigger)
                ? triggers[0]
                : rule.Trigger.Trim().ToLowerInvariant();

            // v1: alert_only only (ignore block actions)
            var alert = new DlpAlert
            {
                Id = Guid.NewGuid().ToString(),
                RuleId = rule.Id,
                Trigger = ruleTrigger,
                Severity = string.IsNullOrWhiteSpace(rule.Severity) ? "medium" : rule.Severity.ToLowerInvariant(),
                FileOrUrl = evt.Subject ?? "",
                DetailJson = JsonSerializer.Serialize(new
                {
                    type = evt.Type,
                    detail = evt.Detail,
                    ruleName = rule.Name,
                    action = rule.Action,
                }, JsonOpts),
                EventAt = evt.At.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(evt.At, DateTimeKind.Utc)
                    : evt.At.ToUniversalTime(),
            };
            await _store.StoreDlpAlertsAsync(new[] { alert }, ct);
            _logger.LogInformation("DlpEngine: alert {Id} trigger={Trigger} rule={Rule}", alert.Id, ruleTrigger, rule.Name);
        }
    }

    /// <summary>
    /// Sensor → admin trigger(s). Removable file ops match both file_transfer and usb
    /// so a "usb not allowed" rule also catches copies onto a stick.
    /// </summary>
    private static string[] MapTriggers(string? type) => type?.ToLowerInvariant() switch
    {
        "usb_plugged" => new[] { "usb" },
        "file_on_removable" => new[] { "file_transfer", "usb" },
        "browser_url" => new[] { "cloud_upload" },
        _ => Array.Empty<string>(),
    };

    private async Task RefreshRulesAsync(CancellationToken ct)
    {
        var employee = await _store.GetEmployeeInfoAsync(ct);
        if (employee == null || (string.IsNullOrWhiteSpace(employee.DeviceToken) && string.IsNullOrWhiteSpace(employee.Token)))
        {
            _logger.LogDebug("DlpEngine: no credentials — skipping rule pull");
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{_config.ServerUrl?.TrimEnd('/')}/api/v1/dlp-rules/active");
        ApplyAuth(request, employee);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("DlpEngine: GET /dlp-rules/active => {Status}", (int)response.StatusCode);
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<ActiveRulesPayload>(stream, JsonOpts, ct);
        var rules = payload?.Rules ?? new List<DlpRule>();
        lock (_rulesLock) _rules = rules;
        _logger.LogInformation("DlpEngine: cached {Count} active rule(s)", rules.Count);
    }

    private static void ApplyAuth(HttpRequestMessage request, EmployeeInfo employee)
    {
        if (!string.IsNullOrWhiteSpace(employee.DeviceToken))
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Device", employee.DeviceToken);
        else
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", employee.Token);
        client.Services.ClientIdentityHeaders.Apply(request);
    }

    private sealed class ActiveRulesPayload
    {
        public List<DlpRule>? Rules { get; set; }
    }
}
