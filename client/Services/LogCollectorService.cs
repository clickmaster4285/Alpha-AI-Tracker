using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

public class LogCollectorService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IActivityCollector _collector;
    private readonly ILogStore _store;
    private readonly ILogger<LogCollectorService> _logger;
    private int _cycleCount;

    public LogCollectorService(
        AppConfig config,
        IActivityCollector collector,
        ILogStore store,
        ILogger<LogCollectorService> logger)
    {
        _config = config;
        _collector = collector;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "LogCollectorService starting (machine={MachineId}, interval={Interval}s, session={SessionId})",
            _config.ClientId, _config.CollectIntervalSec, SessionInfo.SessionId);

        await _store.InitializeAsync(stoppingToken);
        await StorePermissionStatus(stoppingToken);

        var interval = TimeSpan.FromSeconds(Math.Max(5, _config.CollectIntervalSec));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                var logs = (await _collector.CollectAsync(stoppingToken))
                    .Where(l => !string.IsNullOrWhiteSpace(l.WindowTitle))
                    .ToList();

                if (logs.Count > 0)
                {
                    await _store.StoreAsync(logs, stoppingToken);
                    var total = await _store.GetCountAsync(stoppingToken);
                    _logger.LogDebug(
                        "Collected {Count} logs (total in db: {Total}) in {Elapsed}ms",
                        logs.Count, total, sw.ElapsedMilliseconds);
                }

                _cycleCount++;
                if (_cycleCount % 10 == 0)
                {
                    await StorePermissionStatus(stoppingToken);
                }
                if (_cycleCount % 100 == 0)
                {
                    await _store.CleanupAsync(TimeSpan.FromDays(30), stoppingToken);
                    _logger.LogDebug("Cleaned up logs older than 30 days");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during log collection cycle");
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

        _logger.LogInformation("LogCollectorService stopped");
    }

    private async Task StorePermissionStatus(CancellationToken ct)
    {
        try
        {
            var sessionType = "unknown";
            if (OperatingSystem.IsLinux())
            {
                sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown";
                var perms = client.Platform.Linux.ProcessCollector.GetPermissionStatus();
                await _store.SetPermissionStatusAsync(perms, sessionType, ct);
            }
            else if (OperatingSystem.IsWindows())
            {
                sessionType = "windows";
                var perms = client.Platform.Windows.ProcessCollector.GetPermissionStatus();
                await _store.SetPermissionStatusAsync(perms, sessionType, ct);
            }
            else if (OperatingSystem.IsMacOS())
            {
                sessionType = "macos";
                var perms = client.Platform.MacOS.ProcessCollector.GetPermissionStatus();
                await _store.SetPermissionStatusAsync(perms, sessionType, ct);
            }

            _logger.LogDebug("Permission status stored (session_type={SessionType})", sessionType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store permission status");
        }
    }
}
