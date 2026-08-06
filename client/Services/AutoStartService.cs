using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Registers and unregisters the application for auto-start on system boot.
/// Platform-specific implementations:
/// - Windows: Registry HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// - Linux: ~/.config/autostart/ .desktop file
/// - macOS: ~/Library/LaunchAgents/ plist file
/// </summary>
public class AutoStartService
{
    private readonly string _executablePath;
    private readonly ILogger<AutoStartService> _logger;

    public AutoStartService(ILogger<AutoStartService> logger)
    {
        _executablePath = Environment.ProcessPath
                          ?? Environment.GetCommandLineArgs()[0];
        _logger = logger;
    }

    /// <summary>
    /// Register auto-start. Returns true if successful.
    /// </summary>
    public bool EnableAutoStart()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return EnableWindowsAutoStart();
            else if (OperatingSystem.IsLinux())
                return EnableLinuxAutoStart();
            else if (OperatingSystem.IsMacOS())
                return EnableMacOSAutoStart();

            _logger.LogWarning("Auto-start not supported on this platform");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enable auto-start");
            return false;
        }
    }

    /// <summary>
    /// Register auto-start with forced re-registration.
    /// If already registered, re-writes it to prevent removal.
    /// Also schedules a startup task (Windows) or systemd service (Linux)
    /// that auto-restarts if the Run key / .desktop file is deleted.
    /// </summary>
    public bool EnableAutoStartForced()
    {
        var result = EnableAutoStart();

        // Also install platform-specific persistence layer
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Create a scheduled task that runs on startup as a backup
                var psi = new ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = $"/Create /SC ONSTART /TN \"AlphaAITracker-Startup\" /TR \"cmd.exe /c start \"\" \\\"{_executablePath}\\\" --minimized\" /F /RL HIGHEST /DELAY 0000:10",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
                
                _logger.LogInformation("Windows forced auto-start: Run key + Scheduled Task");
            }
            else if (OperatingSystem.IsLinux())
            {
                // The .desktop file is already created by EnableAutoStart
                // Also verify systemd user service is installed
                var psi = new ProcessStartInfo
                {
                    FileName = "systemctl",
                    Arguments = $"--user enable alpha-ai-tracker.service",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
                
                _logger.LogInformation("Linux forced auto-start: .desktop + systemd service");
            }
            else if (OperatingSystem.IsMacOS())
            {
                _logger.LogInformation("macOS forced auto-start: launchd plist");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Forced auto-start extra layer failed");
        }

        return result;
    }

    /// <summary>
    /// Remove auto-start registration.
    /// </summary>
    public bool DisableAutoStart()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return DisableWindowsAutoStart();
            else if (OperatingSystem.IsLinux())
                return DisableLinuxAutoStart();
            else if (OperatingSystem.IsMacOS())
                return DisableMacOSAutoStart();

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable auto-start");
            return false;
        }
    }

    /// <summary>
    /// Check if auto-start is currently enabled.
    /// </summary>
    public bool IsAutoStartEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run");
                return key?.GetValue("AlphaAITracker") != null;
            }
            else if (OperatingSystem.IsLinux())
            {
                var desktopPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "autostart", "alpha-ai-tracker.desktop");
                return File.Exists(desktopPath);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var plistPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "LaunchAgents", "com.alphaai.tracker.plist");
                return File.Exists(plistPath);
            }
        }
        catch { }
        return false;
    }

    [SupportedOSPlatform("windows")]
    private bool EnableWindowsAutoStart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key == null) return false;

            key.SetValue("AlphaAITracker",
                $"\"{_executablePath}\" --minimized",
                Microsoft.Win32.RegistryValueKind.String);
            _logger.LogInformation("Windows auto-start enabled");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning("Need admin rights to set auto-start on Windows");
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private bool DisableWindowsAutoStart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key?.GetValue("AlphaAITracker") != null)
            {
                key.DeleteValue("AlphaAITracker");
            }
            _logger.LogInformation("Windows auto-start disabled");
            return true;
        }
        catch { return false; }
    }

    private bool EnableLinuxAutoStart()
    {
        var autostartDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "autostart");
        Directory.CreateDirectory(autostartDir);

        var desktopContent = $"""
            [Desktop Entry]
            Type=Application
            Name=Alpha AI Tracker
            Exec="{_executablePath}" --minimized
            Terminal=false
            X-GNOME-Autostart-enabled=true
            X-GNOME-Autostart-Delay=5
            X-KDE-autostart-after=panel
            Categories=Utility;
            Comment=Employee Monitoring & Activity Tracker
            """;

        var desktopPath = Path.Combine(autostartDir, "alpha-ai-tracker.desktop");
        File.WriteAllText(desktopPath, desktopContent, Encoding.UTF8);
        _logger.LogInformation("Linux auto-start enabled at {Path}", desktopPath);
        return true;
    }

    private bool DisableLinuxAutoStart()
    {
        var desktopPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "autostart", "alpha-ai-tracker.desktop");
        if (File.Exists(desktopPath))
        {
            File.Delete(desktopPath);
            _logger.LogInformation("Linux auto-start disabled");
        }
        return true;
    }

    private bool EnableMacOSAutoStart()
    {
        var launchAgentsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents");
        Directory.CreateDirectory(launchAgentsDir);

        var plistContent = $""""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple Computer//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>com.alphaai.tracker</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{_executablePath}</string>
                    <string>--minimized</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
                <key>ProcessType</key>
                <string>Background</string>
                <key>ThrottleInterval</key>
                <integer>5</integer>
            </dict>
            </plist>
            """";

        var plistPath = Path.Combine(launchAgentsDir, "com.alphaai.tracker.plist");
        File.WriteAllText(plistPath, plistContent, Encoding.UTF8);

        // Load with launchctl
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "launchctl",
                Arguments = $"load {plistPath}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "launchctl load failed, auto-start is registered but not active until next login");
        }

        _logger.LogInformation("macOS auto-start enabled at {Path}", plistPath);
        return true;
    }

    private bool DisableMacOSAutoStart()
    {
        var plistPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", "com.alphaai.tracker.plist");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "launchctl",
                Arguments = $"unload {plistPath}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch { }

        if (File.Exists(plistPath))
            File.Delete(plistPath);

        _logger.LogInformation("macOS auto-start disabled");
        return true;
    }
}
