using client.Services.Watchers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services.Dlp;

/// <summary>
/// When file-journey is OFF but DLP is ON, start <see cref="FileSystemEventWatcher"/>
/// so removable-drive create/rename/change events still reach the DLP agent.
/// </summary>
public sealed class DlpFileWatchHost : BackgroundService
{
    private readonly FileSystemEventWatcher _fs;
    private readonly ILogger<DlpFileWatchHost> _logger;

    public DlpFileWatchHost(FileSystemEventWatcher fs, ILogger<DlpFileWatchHost> logger)
    {
        _fs = fs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DlpFileWatchHost: starting removable FS watch (file-journey off)");
        await _fs.StartAsync(stoppingToken);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _fs.Stop();
        }
    }
}
