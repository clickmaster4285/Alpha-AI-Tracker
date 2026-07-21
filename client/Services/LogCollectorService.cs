using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core.Abstractions;

namespace client.Services;

public class LogCollectorService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IActivityCollector _collector;
    private readonly ILogStore _store;
    private readonly ILogger<LogCollectorService> _logger;

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
            "LogCollectorService starting (machine={MachineId}, interval={Interval}s)",
            _config.ClientId, _config.CollectIntervalSec);

        await _store.InitializeAsync(stoppingToken);

        var interval = TimeSpan.FromSeconds(Math.Max(5, _config.CollectIntervalSec));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                var logs = await _collector.CollectAsync(stoppingToken);

                if (logs.Count > 0)
                {
                    await _store.StoreAsync(logs, stoppingToken);
                    var total = await _store.GetCountAsync(stoppingToken);
                    _logger.LogDebug(
                        "Collected {Count} logs (total in db: {Total}) in {Elapsed}ms",
                        logs.Count, total, sw.ElapsedMilliseconds);
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
}
