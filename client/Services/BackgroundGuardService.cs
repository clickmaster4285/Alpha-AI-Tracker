using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Background guard service that provides resilience for already-configured permissions.
/// 
/// **Design principle**: This service NEVER creates auto-start or systemd services on its own.
/// Initial setup is done exclusively through the permission step UI (MainViewModel).
/// This service only WATCHDOGS — if something that was previously configured gets removed,
/// it re-installs it immediately.
/// 
/// Flow:
/// 1. User completes permission steps → auto-start + systemd are created
/// 2. BackgroundGuardService detects these exist → starts protecting them
/// 3. If user/process removes them → guard re-installs within 60 seconds
/// </summary>
public class BackgroundGuardService : BackgroundService, IDisposable
{
    private readonly ILogger<BackgroundGuardService> _logger;
    private readonly AutoStartService _autoStart;

    // Track what's currently installed so we know what to watchdog
    private bool _autoStartWasEnabled;
    private bool _systemdWasInstalled;
    private string? _systemdServiceName;

    // Windows: self-restart capability
    private static readonly string RestartScriptPath =
        Path.Combine(
            Path.GetTempPath(),
            "AlphaAITracker_Restart.cmd");

    public BackgroundGuardService(
        ILogger<BackgroundGuardService> logger,
        AutoStartService autoStart)
    {
        _logger = logger;
        _autoStart = autoStart;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackgroundGuardService starting...");

        // ─── Initial State Detection ───
        // Detect what's already configured (may have been set up by permission UI
        // in a previous session). We do NOT install anything here — only observe.
        _autoStartWasEnabled = _autoStart.IsAutoStartEnabled();
        _systemdWasInstalled = DetectSystemdService();
        _systemdServiceName = "alpha-ai-tracker.service";

        if (_autoStartWasEnabled)
            _logger.LogInformation("Auto-start already configured — will watchdog");
        if (_systemdWasInstalled)
            _logger.LogInformation("Systemd service already installed — will watchdog");

        // ─── Watchdog Loop ───
        // Only protects things that were previously set up. Never creates new ones.
        var healthCheckInterval = TimeSpan.FromSeconds(60);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(healthCheckInterval, stoppingToken);

                _logger.LogTrace("Guard watchdog heartbeat OK");

                // Watchdog: auto-start — detect if present, protect if previously seen
                if (_autoStart.IsAutoStartEnabled())
                {
                    _autoStartWasEnabled = true; // Seen at least once → start protecting
                }
                else if (_autoStartWasEnabled)
                {
                    // Was previously seen as enabled, now missing — re-install
                    _logger.LogWarning("Auto-start was removed! Re-installing immediately...");
                    _autoStart.EnableAutoStartForced();
                }

                // Watchdog: systemd service — only if it was previously installed
                if (_systemdWasInstalled && _systemdServiceName != null)
                {
                    if (!IsSystemdActive(_systemdServiceName))
                    {
                        _logger.LogWarning("Systemd service inactive or missing, reinstalling...");
                        await InstallSystemdServiceAsync(stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Guard watchdog error");
            }
        }

        _logger.LogInformation("BackgroundGuardService stopped");
    }

    // ─── State Detection ───

    private static bool DetectSystemdService()
    {
        try
        {
            var svcPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "systemd", "user", "alpha-ai-tracker.service");
            return File.Exists(svcPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSystemdActive(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = $"--user is-active {serviceName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            return output.Trim().Equals("active", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ─── Linux Systemd Integration ───

    private async Task InstallSystemdServiceAsync(CancellationToken ct)
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            var serviceContent = $"""
                [Unit]
                Description=Alpha AI Tracker - Employee Monitoring Service

                [Service]
                Type=simple
                ExecStart={exePath} --background
                Restart=always
                RestartSec=10
                StartLimitBurst=0
                StartLimitIntervalSec=0
                Environment=DISPLAY=:0
                Environment=XAUTHORITY={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/.Xauthority

                [Install]
                WantedBy=default.target
                """;

            var systemdDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", "systemd", "user");
            Directory.CreateDirectory(systemdDir);

            var servicePath = Path.Combine(systemdDir, "alpha-ai-tracker.service");
            await File.WriteAllTextAsync(servicePath, serviceContent, ct);

            // Enable the service
            var enablePsi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = $"--user enable {servicePath}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var enableProc = Process.Start(enablePsi))
            {
                if (enableProc != null)
                {
                    await enableProc.WaitForExitAsync(ct);
                    if (enableProc.ExitCode == 0)
                    {
                        _systemdWasInstalled = true;
                        _logger.LogInformation("Systemd user service reinstalled");
                    }
                    else
                    {
                        var err = await enableProc.StandardError.ReadToEndAsync(ct);
                        _logger.LogWarning("Systemd enable failed: {Error}", err.Trim());
                    }
                }
            }

            // Start the service
            var startPsi = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = "--user start alpha-ai-tracker.service",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var startProc = Process.Start(startPsi))
            {
                if (startProc != null)
                {
                    await startProc.WaitForExitAsync(ct);
                    if (startProc.ExitCode == 0)
                    {
                        _logger.LogInformation("Systemd user service started");
                    }
                    else
                    {
                        var err = await startProc.StandardError.ReadToEndAsync(ct);
                        _logger.LogWarning("Systemd start warning: {Error}", err.Trim());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install systemd user service");
        }
    }

    #region Windows Persistence

    private void InstallWindowsRestartScript()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            var script = $"""
                @echo off
                :loop
                tasklist /FI "IMAGENAME eq {Path.GetFileName(exePath)}" 2>NUL | find /I "{Path.GetFileName(exePath)}" >NUL
                if errorlevel 1 (
                    start "" "{exePath}" --background
                )
                timeout /t 30 /nobreak >NUL
                goto loop
                """;
            File.WriteAllText(RestartScriptPath, script);
            _logger.LogInformation("Windows restart script at {Path}", RestartScriptPath);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start /MIN \"\" \"{RestartScriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install Windows restart script");
        }
    }

    private void TrySetCriticalProcess()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = $"/Create /SC ONLOGON /TN \"AlphaAITracker\" /TR \"\\\"{Environment.ProcessPath}\\\" --background\" /F /RL HIGHEST /DELAY 0000:05",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(10000);
                if (proc.ExitCode == 0)
                    _logger.LogInformation("Windows Scheduled Task installed");
                else
                    _logger.LogWarning("Scheduled Task failed (exit {Code})", proc.ExitCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install Windows Scheduled Task");
        }
    }

    #endregion

    public override void Dispose()
    {
        base.Dispose();
    }
}
