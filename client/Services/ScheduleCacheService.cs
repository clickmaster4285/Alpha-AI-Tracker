using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using client.Configuration;
using client.Core.Abstractions;

namespace client.Services;

/// <summary>
/// Time and Attendance (Phase 1, finalplan section 2.2 / BUG-6 fix + section 5 S7):
/// pulls the logged-in employee's shift assignment + company holidays from the
/// server and mirrors them into the local SQLite tables (employee_schedule,
/// company_holidays - schema added in A.5). The AttendanceAggregator (A.8) reads
/// the local mirror to compute the daily "arrival" status without re-fetching.
///
/// Pull-once policy (S7): schedules change rarely, so the client PULLS on login
/// and every 6 hours - no push channel, no WebSocket.
///
/// Graceful no-op (finalplan section 6 SVR-1): GET /api/v1/schedules/me does NOT
/// exist yet (Phase 2). Until it does the server returns 404 and this service
/// logs at Debug and retries on the next 6h tick - Phase 1 client work never
/// blocks on Phase 2 server work.
///
/// Auth: a GET cannot carry a body, so the employee token rides in the
/// Authorization header. We send BOTH header styles (Bearer + Device) so the
/// endpoint works whichever middleware protects it in Phase 2.
/// </summary>
public sealed class ScheduleCacheService : BackgroundService
{
    private static readonly TimeSpan PullInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FirstPullDelay = TimeSpan.FromSeconds(45);

    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ScheduleCacheService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public ScheduleCacheService(
        ILogStore store,
        AppConfig config,
        HttpClient httpClient,
        ILogger<ScheduleCacheService> logger)
    {
        _store = store;
        _config = config;
        _httpClient = httpClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.TaEnabled)
        {
            _logger.LogInformation("ScheduleCacheService: ALPHA_TA_ENABLED=false - parked");
            return;
        }

        // Give the login flow time to persist the employee token before the first pull.
        try { await Task.Delay(FirstPullDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PullAsync(stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ScheduleCacheService: pull failed (server unreachable?)");
            }

            try
            {
                await Task.Delay(PullInterval, stoppingToken);
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task PullAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.ServerUrl)) return;

        var employee = await _store.GetEmployeeInfoAsync(ct);
        if (employee == null || string.IsNullOrWhiteSpace(employee.Token))
        {
            _logger.LogDebug("ScheduleCacheService: no logged-in employee - skip");
            return;
        }

        var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get,
            $"{_config.ServerUrl.TrimEnd('/')}/api/v1/schedules/me");
        // Both auth header styles: the endpoint's middleware is a Phase 2 decision.
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", employee.Token);
        if (!string.IsNullOrWhiteSpace(employee.DeviceToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Device {employee.DeviceToken}");
        }

        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Expected until the Phase 2 server ships SVR-1. Debug, not warning.
            _logger.LogDebug("ScheduleCacheService: /schedules/me not implemented server-side yet (404) - will retry");
            return;
        }
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("ScheduleCacheService: schedules pull failed with {Status}", response.StatusCode);
            return;
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var payload = await JsonSerializer.DeserializeAsync<SchedulePayload>(stream, JsonOpts, ct);
        if (payload == null || string.IsNullOrEmpty(payload.Timezone))
        {
            _logger.LogWarning("ScheduleCacheService: malformed schedules payload - skip");
            return;
        }

        await _store.UpsertEmployeeScheduleAsync(
            employee.EmployeeId,
            payload.Timezone,
            JsonSerializer.Serialize(payload.WeeklyPattern ?? new Dictionary<string, string>(), JsonOpts),
            payload.GraceMinutes,
            payload.ValidFrom,
            payload.ValidTo,
            payload.Id,
            ct);

        foreach (var holiday in payload.Holidays ?? new List<HolidayPayload>())
        {
            if (string.IsNullOrWhiteSpace(holiday.Date)) continue;
            await _store.UpsertCompanyHolidayAsync(holiday.Date, holiday.Label ?? string.Empty, payload.Id, ct);
        }

        _logger.LogInformation(
            "ScheduleCacheService: mirrored schedule (tz={Tz}, grace={Grace}m, holidays={Holidays})",
            payload.Timezone, payload.GraceMinutes, payload.Holidays?.Count ?? 0);

        // High-water-mark key (finalplan section 5 S3).
        await _store.SetStatusAsync("ta_last_schedule_pull_at", DateTime.UtcNow.ToString("O"), ct);
    }

    // ── Payload shapes (Phase 2 contract, finalplan section 6 SVR-1) ──

    private sealed class SchedulePayload
    {
        public string? Id { get; set; }
        public string? Timezone { get; set; }
        public int GraceMinutes { get; set; }
        public Dictionary<string, string>? WeeklyPattern { get; set; }
        public string? ValidFrom { get; set; }
        public string? ValidTo { get; set; }
        public List<HolidayPayload>? Holidays { get; set; }
    }

    private sealed class HolidayPayload
    {
        public string? Date { get; set; }
        public string? Label { get; set; }
    }
}
