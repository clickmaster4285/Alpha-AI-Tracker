using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Core.Browser;

/// <summary>
/// Boots the browser journey pipeline once the host starts:
/// 1. initialize state store (tables + lease reuse),
/// 2. recover persisted open journeys,
/// 3. wire Coordinator → JourneyEngine and RuntimeManager.RuntimeGone → journey close,
/// 4. start the runtime manager (initial detection + connection).
/// </summary>
public sealed class BrowserRuntimeHostedService : BackgroundService
{
    private readonly BrowserRuntimeManager _runtimeManager;
    private readonly BrowserJourneyEngine _journeyEngine;
    private readonly BrowserEventCoordinator _coordinator;
    private readonly BrowserRuntimeStateStore _store;
    private readonly string _dbPath;
    private readonly ILogger<BrowserRuntimeHostedService> _logger;

    public BrowserRuntimeHostedService(
        BrowserRuntimeManager runtimeManager,
        BrowserJourneyEngine journeyEngine,
        BrowserEventCoordinator coordinator,
        BrowserRuntimeStateStore store,
        string dbPath,
        ILogger<BrowserRuntimeHostedService> logger)
    {
        _runtimeManager = runtimeManager;
        _journeyEngine = journeyEngine;
        _coordinator = coordinator;
        _store = store;
        _dbPath = dbPath;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store.Initialize(_dbPath);

        _journeyEngine.Attach(_coordinator);
        _runtimeManager.RuntimeGone += async (_, runtimeId) =>
        {
            try
            {
                await _journeyEngine.CloseRuntimeTabsAsync(runtimeId, DateTime.UtcNow, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to close journeys for gone runtime {Runtime}", runtimeId);
            }
        };

        try
        {
            await _journeyEngine.RecoverAsync(stoppingToken);
            await _runtimeManager.StartAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Browser journey pipeline failed to start");
        }

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await _store.CloseAllOpenJourneysAsync(DateTime.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close open journeys at shutdown");
        }
    }
}
