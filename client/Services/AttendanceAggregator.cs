using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Time and Attendance (Phase 1, finalplan A.8): background service that rolls up
/// "today's" session-idle activity for the logged-in employee into
/// daily_attendance_cache — the local ARRIVAL / DEPARTURE / ACTIVE / IDLE picture.
///
/// Runs every 5 minutes (finalplan S8): the source data is 30s-granular, so a
/// 5-minute tick means the dashboard lags at most 5 minutes — honest.
///
/// The cache is DERIVED (client-owned) and NEVER sent to the server (finalplan
/// section 2.2): the server has its own aggregator for the public T&A view in
/// Phase 2. The local cache is a presentation hint, not a source of truth.
///
/// Inputs (read via the read-only connection, finalplan R8):
///   - session_events: power_on / resume (presence), idle_start / idle_end (A.4),
///     screen_lock / unlock (A.3).
///
/// Status: present | late | absent | half_day | off_shift | unknown. When the
/// employee has a mirrored schedule (A.6) we compare first-active vs the shift
/// start + grace; otherwise status is "unknown" (honest — we can't assert
/// "on time" without an assigned shift).
/// </summary>
public sealed class AttendanceAggregator : BackgroundService
{
    private static readonly TimeSpan AggregateInterval = TimeSpan.FromMinutes(5);

    private readonly IEventRecorder _eventRecorder;
    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly ILogger<AttendanceAggregator> _logger;

    public AttendanceAggregator(
        IEventRecorder eventRecorder,
        ILogStore store,
        AppConfig config,
        ILogger<AttendanceAggregator> logger)
    {
        _eventRecorder = eventRecorder;
        _store = store;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.TaEnabled)
        {
            _logger.LogInformation("AttendanceAggregator: ALPHA_TA_ENABLED=false - parked");
            return;
        }

        _logger.LogInformation("AttendanceAggregator starting (every {Min} min)", AggregateInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AggregateTodayAsync(stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "AttendanceAggregator: pass failed");
            }

            try
            {
                await Task.Delay(AggregateInterval, stoppingToken);
            }
            catch (OperationCanceledException) { }
        }
    }

    private async Task AggregateTodayAsync(CancellationToken ct)
    {
        var employee = await _store.GetEmployeeInfoAsync(ct);
        if (employee == null || string.IsNullOrWhiteSpace(employee.EmployeeId))
        {
            _logger.LogDebug("AttendanceAggregator: no logged-in employee - skip");
            return;
        }

        // Local "today" in the employee's schedule timezone (default UTC).
        var schedules = await _store.ListEmployeeSchedulesAsync(ct);
        var hasSchedule = schedules.Any(s =>
            string.Equals(s.EmployeeId, employee.EmployeeId, StringComparison.OrdinalIgnoreCase));
        var schedule = hasSchedule
            ? schedules.First(s => string.Equals(s.EmployeeId, employee.EmployeeId, StringComparison.OrdinalIgnoreCase))
            : ((string EmployeeId, string Timezone, string WeeklyPattern, int GraceMinutes)?)null;
        var tz = string.IsNullOrEmpty(schedule?.Timezone)
            ? TimeZoneInfo.Utc : SafeTz(schedule.Value.Timezone);
        var localNow = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        var workDate = localNow.ToString("yyyy-MM-dd");

        // Day window in UTC (read-only connection).
        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(localNow.Date, tz);
        var from = dayStartUtc.AddHours(-1);   // small pre-window for events stamped just before midnight
        var to = dayStartUtc.AddDays(1);
        var events = await _store.GetSessionEventsInRangeAsync(from, to, ct);

        // ── Presence ──
        // power_on = earliest power/resume event of the day (the "arrival"); the
        // machine being on defines presence. last_seen = latest event (departure).
        var powerOnAt = events
            .Where(e => e.EventType == SessionEventTypes.PowerOn || e.EventType == SessionEventTypes.Resume)
            .Select(e => e.EventAt)
            .DefaultIfEmpty(dayStartUtc)
            .Min();
        var lastSeenAt = events
            .OrderByDescending(e => e.EventAt)
            .Select(e => e.EventAt)
            .DefaultIfEmpty(dayStartUtc)
            .First();

        // ── Idle from A.4's idle_start/idle_end pairs ──
        double idleSeconds = 0;
        DateTime? idleOpenStart = null;
        foreach (var e in events.OrderBy(e => e.EventAt))
        {
            if (e.EventType == SessionEventTypes.IdleStart)
            {
                idleOpenStart = e.EventAt;
            }
            else if (e.EventType == SessionEventTypes.IdleEnd && idleOpenStart.HasValue)
            {
                idleSeconds += (e.EventAt - idleOpenStart.Value).TotalSeconds;
                idleOpenStart = null;
            }
        }
        // A still-open idle (employee walked away) counts to now.
        if (idleOpenStart.HasValue)
        {
            idleSeconds += (DateTime.UtcNow - idleOpenStart.Value).TotalSeconds;
        }

        var presenceSeconds = (lastSeenAt - powerOnAt).TotalSeconds;
        if (presenceSeconds < 0) presenceSeconds = 0;
        var activeSeconds = Math.Max(0, presenceSeconds - idleSeconds);

        // ── Status ──
        var status = "unknown";
        var lateMinutes = 0;
        if (schedule.HasValue)
        {
            var weekDay = localNow.DayOfWeek.ToString().ToLowerInvariant();
            var (shiftStart, _) = ParseShift(schedule.Value.WeeklyPattern, weekDay);
            if (!shiftStart.HasValue)
            {
                status = "off_shift";
            }
            else
            {
                var graceMinutes = schedule.Value.GraceMinutes;
                // Shift start on the employee's local today + grace, as a full
                // DateTime so we can compare against the first-active timestamp.
                var shiftStartToday = localNow.Date.Add(shiftStart.Value);
                var startWithGrace = shiftStartToday.AddMinutes(graceMinutes);
                var firstActiveLocal = TimeZoneInfo.ConvertTime(powerOnAt, tz);
                if (firstActiveLocal > startWithGrace)
                {
                    status = "late";
                    lateMinutes = (int)(firstActiveLocal - startWithGrace).TotalMinutes;
                }
                else
                {
                    status = "present";
                }
            }
        }

        await _store.UpsertDailyAttendanceAsync(
            employee.EmployeeId,
            workDate,
            powerOnAt,
            lastSeenAt,
            (int)Math.Round(activeSeconds),
            (int)Math.Round(idleSeconds),
            0,
            status,
            lateMinutes,
            ct);

        _logger.LogDebug(
            "AttendanceAggregator: {Date} {Emp} active={A}s idle={I}s status={Status} late={Late}m",
            workDate, employee.EmployeeId, (int)activeSeconds, (int)idleSeconds, status, lateMinutes);

        // High-water-mark key (finalplan section 5 S3).
        await _store.SetStatusAsync("ta_last_aggregator_run_at", DateTime.UtcNow.ToString("O"), ct);
    }

    private static TimeZoneInfo SafeTz(string iana)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Parses the mirrored schedule's weekly_pattern JSON for today's shift start.
    /// Returns (start, null) when the day has a shift, else (null, null).
    /// </summary>
    private static (TimeSpan? Start, TimeSpan? End) ParseShift(string weeklyPatternJson, string weekDay)
    {
        if (string.IsNullOrWhiteSpace(weeklyPatternJson)) return (null, null);
        try
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                weeklyPatternJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (map == null || !map.TryGetValue(weekDay, out var range) || string.IsNullOrWhiteSpace(range))
                return (null, null);

            // "09:00-18:00"
            var parts = range.Split('-');
            if (parts.Length != 2) return (null, null);
            if (!TimeSpan.TryParse(parts[0], out var start)) return (null, null);
            if (!TimeSpan.TryParse(parts[1], out var end)) return (null, null);
            return (start, end);
        }
        catch
        {
            return (null, null);
        }
    }
}
