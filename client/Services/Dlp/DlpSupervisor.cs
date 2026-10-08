using client.Configuration;
using client.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services.Dlp;

/// <summary>
/// Main-tracker hosted service: spawns/monitors <c>client --dlp</c> when enabled
/// and employee credentials exist. Does not evaluate DLP rules itself.
/// </summary>
public sealed class DlpSupervisor : BackgroundService
{
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly ILogger<DlpSupervisor> _logger;
    private System.Diagnostics.Process? _agent;

    public DlpSupervisor(AppConfig config, ILogStore store, ILogger<DlpSupervisor> logger)
    {
        _config = config;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.DlpEnabled)
        {
            _logger.LogInformation("DlpSupervisor: ALPHA_DLP_ENABLED=false — not spawning agent");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var employee = await _store.GetEmployeeInfoAsync(stoppingToken);
                var hasCreds = employee != null
                    && (!string.IsNullOrWhiteSpace(employee.DeviceToken) || !string.IsNullOrWhiteSpace(employee.Token));

                if (hasCreds)
                    EnsureAgentRunning();
                else
                    StopAgent();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DlpSupervisor: tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }

        StopAgent();
    }

    private void EnsureAgentRunning()
    {
        if (_agent != null && !_agent.HasExited)
            return;

        try { _agent?.Dispose(); } catch { }
        _agent = null;

        var exePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "client.exe" : "client");
        if (!File.Exists(exePath))
        {
            _logger.LogDebug("DlpSupervisor: exe not found at {Path}", exePath);
            return;
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "--dlp",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _agent = System.Diagnostics.Process.Start(psi);
        if (_agent == null)
        {
            _logger.LogWarning("DlpSupervisor: failed to start --dlp");
            return;
        }
        _logger.LogInformation("DlpSupervisor: spawned --dlp (pid={Pid})", _agent.Id);
    }

    private void StopAgent()
    {
        if (_agent == null) return;
        try
        {
            if (!_agent.HasExited)
            {
                _agent.Kill(entireProcessTree: true);
                _agent.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DlpSupervisor: stop agent failed");
        }
        finally
        {
            try { _agent.Dispose(); } catch { }
            _agent = null;
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        StopAgent();
        return base.StopAsync(cancellationToken);
    }
}
