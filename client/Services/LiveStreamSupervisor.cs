using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;

namespace client.Services;

/// <summary>
/// Main-tracker host only: spawns <c>client.exe --live-stream</c> as a sibling process
/// that owns screen capture + WebRTC/JPEG push. The tracker never loads SIPSorcery /
/// vpxmd.dll — a native AV in the worker kills only the worker; this supervisor restarts
/// it. Same pattern as the standalone <c>--terms</c> agent.
/// </summary>
public sealed class LiveStreamSupervisor : BackgroundService
{
    /// <summary>Mutex name for the live-stream worker (must match Program.RunLiveStreamWorkerAsync).</summary>
    public static string WorkerMutexName => $@"Global\{AppInfo.AppMutex}-live-stream";

    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly ILogger<LiveStreamSupervisor> _logger;

    private Process? _worker;
    private readonly object _gate = new();

    public LiveStreamSupervisor(
        ILogStore store,
        AppConfig config,
        ILogger<LiveStreamSupervisor> logger)
    {
        _store = store;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.StreamEnabled)
        {
            _logger.LogInformation("LiveStreamSupervisor parked (ALPHA_STREAM_ENABLED=false)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        _logger.LogInformation(
            "LiveStreamSupervisor active — stream runs in sibling process (--live-stream)");

        var backoffSec = 2;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await HasLoggedInEmployeeAsync(stoppingToken))
                {
                    await DelayQuietAsync(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                if (IsWorkerAlive() || IsWorkerMutexHeld())
                {
                    await DelayQuietAsync(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }

                if (!TrySpawnWorker())
                {
                    await DelayQuietAsync(TimeSpan.FromSeconds(backoffSec), stoppingToken);
                    backoffSec = Math.Min(backoffSec * 2, 60);
                    continue;
                }

                backoffSec = 2;
                await WaitForWorkerExitAsync(stoppingToken);

                if (stoppingToken.IsCancellationRequested) break;

                _logger.LogWarning(
                    "LiveStreamSupervisor: worker exited (code={Code}); restart in {Sec}s",
                    _worker?.ExitCode, backoffSec);
                ClearWorkerHandle();
                await DelayQuietAsync(TimeSpan.FromSeconds(backoffSec), stoppingToken);
                backoffSec = Math.Min(backoffSec * 2, 60);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LiveStreamSupervisor loop error; retry in {Sec}s", backoffSec);
                await DelayQuietAsync(TimeSpan.FromSeconds(backoffSec), stoppingToken);
                backoffSec = Math.Min(backoffSec * 2, 60);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        KillWorker();
        await base.StopAsync(cancellationToken);
    }

    private async Task<bool> HasLoggedInEmployeeAsync(CancellationToken ct)
    {
        try
        {
            var employee = await _store.GetEmployeeInfoAsync(ct);
            return employee is not null &&
                   (!string.IsNullOrWhiteSpace(employee.DeviceToken) ||
                    !string.IsNullOrWhiteSpace(employee.Token));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LiveStreamSupervisor: employee lookup failed");
            return false;
        }
    }

    private bool TrySpawnWorker()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var exeName = OperatingSystem.IsWindows() ? "client.exe" : "client";
            var exePath = Path.Combine(exeDir, exeName);

            ProcessStartInfo psi;
            if (File.Exists(exePath))
            {
                psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--live-stream",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = exeDir,
                };
            }
            else
            {
                // `dotnet run` / IDE: host is dotnet, assembly is client.dll beside us.
                var dllPath = Path.Combine(exeDir, "client.dll");
                if (!File.Exists(dllPath))
                {
                    _logger.LogWarning(
                        "LiveStreamSupervisor: neither {Exe} nor client.dll found under {Dir}",
                        exeName, exeDir);
                    return false;
                }

                psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{dllPath}\" --live-stream",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = exeDir,
                };
            }

            var proc = Process.Start(psi);
            if (proc is null)
            {
                _logger.LogWarning("LiveStreamSupervisor: Process.Start returned null");
                return false;
            }

            lock (_gate)
            {
                _worker?.Dispose();
                _worker = proc;
            }

            _logger.LogInformation(
                "LiveStreamSupervisor: spawned live-stream worker pid={Pid}",
                proc.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LiveStreamSupervisor: failed to spawn --live-stream worker");
            return false;
        }
    }

    private async Task WaitForWorkerExitAsync(CancellationToken ct)
    {
        Process? proc;
        lock (_gate) proc = _worker;
        if (proc is null) return;

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            KillWorker();
            throw;
        }
    }

    private bool IsWorkerAlive()
    {
        lock (_gate)
        {
            try
            {
                return _worker is { HasExited: false };
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool IsWorkerMutexHeld()
    {
        try
        {
            using var existing = Mutex.OpenExisting(WorkerMutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Exists but we can't open it — treat as running.
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void KillWorker()
    {
        Process? proc;
        lock (_gate)
        {
            proc = _worker;
            _worker = null;
        }

        if (proc is null) return;

        try
        {
            if (!proc.HasExited)
            {
                _logger.LogInformation(
                    "LiveStreamSupervisor: stopping live-stream worker pid={Pid}",
                    proc.Id);
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    try { proc.Kill(); } catch { /* best-effort */ }
                }

                try { proc.WaitForExit(3000); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LiveStreamSupervisor: kill worker");
        }
        finally
        {
            try { proc.Dispose(); } catch { }
        }
    }

    private void ClearWorkerHandle()
    {
        lock (_gate)
        {
            try { _worker?.Dispose(); } catch { }
            _worker = null;
        }
    }

    private static async Task DelayQuietAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { }
    }
}
