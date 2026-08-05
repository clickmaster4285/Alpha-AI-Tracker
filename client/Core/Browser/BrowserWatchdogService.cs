using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Core.Browser;

/// <summary>
/// 2s tick driving the browser pipeline: reconcile process liveness and reconnect idle
/// connections. Also refreshes runtime detection every minute to pick up new browsers.
/// </summary>
public sealed class BrowserWatchdogService : BackgroundService
{
    private readonly BrowserRuntimeManager _runtimeManager;
    private readonly BrowserConnectionManager _connectionManager;
    private readonly BrowserJourneyEngine _journeyEngine;
    private readonly ILogger<BrowserWatchdogService> _logger;
    private int _tick;

    public BrowserWatchdogService(
        BrowserRuntimeManager runtimeManager,
        BrowserConnectionManager connectionManager,
        BrowserJourneyEngine journeyEngine,
        ILogger<BrowserWatchdogService> logger)
    {
        _runtimeManager = runtimeManager;
        _connectionManager = connectionManager;
        _journeyEngine = journeyEngine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _tick++;
                await _runtimeManager.ScanProcessStateAsync(stoppingToken);
                await _connectionManager.ReconnectIdleAsync(stoppingToken);
                if (_tick % 30 == 0) // every ~1 minute
                {
                    await _runtimeManager.RefreshAsync(stoppingToken);
                    // Close journeys idle past BrowserJourneyIdleMinutes (default 15).
                    await _journeyEngine.CloseIdleJourneysAsync(stoppingToken);
                }
                if (_tick % 300 == 0) // every ~10 minutes
                    await _runtimeManager.GarbageCollectEphemeralAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Browser watchdog tick failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
