using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Time and Attendance (Phase 1, finalplan section 2.7 / BUG-7 + BUG-12 fix):
/// measures the local clock's skew against the server's clock every 15 minutes
/// and stores the most recent measurement per server URL in the local_time_skew
/// SQLite table (schema added in A.5).
///
/// Why: T&A aggregates by "local 09:00", not UTC 03:30. A client clock 4 minutes
/// off flips "late" to "on time". The dashboard (Phase 2) reads the skew and
/// annotates any attendance row whose measurement exceeds tolerance.
///
/// NOT auto-corrected (finalplan section 2.7): silently changing the clock is a
/// forensic-evidence antipattern — the correction would itself be unlogged.
///
/// Clock source: the server's HTTP Date header (1-second granularity). The skew
/// is interpolated to the request midpoint to cancel round-trip latency, and
/// stored only when |skew| >= 2s (a real problem) or the 1h resample interval
/// elapses (keeps the row fresh). BUG-12: the first post-resume pass is skipped
/// (clock drift during sleep + NTP re-sync + network settle) — 10s stabilization.
///
/// Feature flag: parked when ALPHA_TA_ENABLED=false. Uses the shared HttpClient
/// singleton registered in Program.cs (no IHttpClientFactory in this project).
/// </summary>
public sealed class LocalTimeSkewService : BackgroundService
{
    private static readonly TimeSpan MeasureInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PostResumeStabilization = TimeSpan.FromSeconds(10);
    private const double StoreSkewToleranceSeconds = 2.0;
    private static readonly TimeSpan ResampleInterval = TimeSpan.FromHours(1);

    private readonly IEventRecorder _eventRecorder;
    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LocalTimeSkewService> _logger;

    private DateTime _lastStoredAt = DateTime.MinValue;
    private double? _lastStoredSkewSeconds;

    public LocalTimeSkewService(
        IEventRecorder eventRecorder,
        ILogStore store,
        AppConfig config,
        HttpClient httpClient,
        ILogger<LocalTimeSkewService> logger)
    {
        _eventRecorder = eventRecorder;
        _store = store;
        _config = config;
        _httpClient = httpClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.TaEnabled)
        {
            _logger.LogInformation("LocalTimeSkewService: ALPHA_TA_ENABLED=false - parked");
            return;
        }

        _logger.LogInformation("LocalTimeSkewService starting (measure every {Min} min)", MeasureInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MeasureAndStoreAsync(stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LocalTimeSkewService: measurement failed (server unreachable?)");
            }

            try
            {
                await Task.Delay(MeasureInterval, stoppingToken);
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task MeasureAndStoreAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.ServerUrl))
        {
            _logger.LogDebug("LocalTimeSkewService: no ALPHA_SERVER_URL configured - skip");
            return;
        }

        // Resample policy: store when |skew| >= tolerance (a real problem) or the
        // periodic resample interval has elapsed (keep the row fresh).
        var resampleDue = DateTime.UtcNow - _lastStoredAt >= ResampleInterval;
        if (_lastStoredSkewSeconds.HasValue && !resampleDue &&
            Math.Abs(_lastStoredSkewSeconds.Value) < StoreSkewToleranceSeconds)
        {
            return;
        }

        // HEAD on the server root: cheapest request that carries a Date header
        // (net/http's default response always emits Date).
        var beforeUtc = DateTime.UtcNow;
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, _config.ServerUrl);
        using var response = await _httpClient.SendAsync(request, ct);
        var afterUtc = DateTime.UtcNow;

        var dateHeader = response.Headers.Date;
        if (!dateHeader.HasValue)
        {
            _logger.LogDebug("LocalTimeSkewService: no Date header in response - skip");
            return;
        }

        // Interpolate to the request-window midpoint to cancel round-trip latency.
        var serverUtc = dateHeader.Value.UtcDateTime;
        var clientMidpoint = beforeUtc + (afterUtc - beforeUtc) / 2;
        var skewSeconds = (serverUtc - clientMidpoint).TotalSeconds;

        // 1s-granular header: round to whole seconds; only store meaningful
        // values or the periodic refresh.
        var rounded = Math.Round(skewSeconds, 0);
        var meaningful = Math.Abs(rounded) >= StoreSkewToleranceSeconds;
        if (!meaningful && !resampleDue) return;

        await _store.UpsertTimeSkewAsync(_config.ServerUrl, DateTime.UtcNow, rounded, ct);
        _lastStoredAt = DateTime.UtcNow;
        _lastStoredSkewSeconds = rounded;

        _logger.LogInformation(
            "LocalTimeSkewService: skew for {Server} = {Skew}s (header={Header})",
            _config.ServerUrl, rounded, serverUtc.ToString("O"));

        // High-water-mark key (finalplan §5 S3) for the diagnostics view.
        await _store.SetStatusAsync("ta_last_skew_measure_at", DateTime.UtcNow.ToString("O"), ct);
    }
}
