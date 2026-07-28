using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Detects installed browsers, manages extension auto-installation via --load-extension,
/// and tracks which browsers have the native messaging bridge connected.
/// Chrome-based browsers: launched with --load-extension flag (works on Chrome 150+, Chromium, Brave, Edge).
/// Firefox: shows step-by-step instructions (unsigned .xpi cannot auto-install).
/// </summary>
public class BrowserExtensionService
{
    private readonly ILogger<BrowserExtensionService> _logger;
    private readonly string _chromeExtDir;
    private readonly string _firefoxExtDir;
    private readonly string _socketPath;
    private readonly string _chromeNativeHostDir;
    private readonly string _firefoxNativeHostDir;
    private readonly string _extensionsRoot;

    /// <summary>Cached browser detection results.</summary>
    public IReadOnlyList<DetectedBrowser> DetectedBrowsers => _detected;
    private List<DetectedBrowser> _detected = new();

    /// <summary>True if at least one browser has the extension active (connected via socket).</summary>
    public bool IsAnyExtensionActive => _detected.Any(b => b.Status == BrowserInstallStatus.ExtensionActive);

    public BrowserExtensionService(ILogger<BrowserExtensionService> logger)
    {
        _logger = logger;
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Resolve extension directories — walk up from build output to repo root
        _extensionsRoot = ResolveExtensionsRoot();
        _chromeExtDir = Path.Combine(_extensionsRoot, "chrome");
        _firefoxExtDir = Path.Combine(_extensionsRoot, "firefox");

        _socketPath = Path.Combine(userHome, ".local", "share", "alpha-ai-tracker", "native-messaging.sock");
        _chromeNativeHostDir = Path.Combine(userHome, ".config", "google-chrome", "NativeMessagingHosts");
        _firefoxNativeHostDir = Path.Combine(userHome, ".mozilla", "native-messaging-hosts");
    }

    /// <summary>
    /// Scan the system for installed browsers and check their extension status.
    /// </summary>
    public async Task ScanAsync(CancellationToken ct)
    {
        var results = new List<DetectedBrowser>();
        var resolvedBinaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Chrome / Chromium-based browsers
        async Task AddUniqueChromeAsync(string displayName, string binaryName)
        {
            var binaryPath = FindBinary(binaryName);
            if (binaryPath == null) return;
            // Resolve symlinks to detect duplicates (google-chrome vs google-chrome-stable)
            var resolved = ResolveBinaryPath(binaryPath);
            if (!resolvedBinaries.Add(resolved)) return; // skip duplicate
            await AddChromeBrowserAsync(results, displayName, binaryName, resolved, ct);
        }

        await AddUniqueChromeAsync("Google Chrome", "google-chrome");
        await AddUniqueChromeAsync("Google Chrome (Stable)", "google-chrome-stable");
        await AddUniqueChromeAsync("Chromium", "chromium-browser");
        await AddUniqueChromeAsync("Chromium", "chromium");
        await AddUniqueChromeAsync("Brave", "brave-browser");
        await AddUniqueChromeAsync("Microsoft Edge", "microsoft-edge-stable");
        await AddUniqueChromeAsync("Vivaldi", "vivaldi");
        await AddUniqueChromeAsync("Opera", "opera");

        // Firefox
        await AddFirefoxAsync(results, ct);

        _detected = results;
        _logger.LogInformation("Browser scan: {Count} detected, {Active} active",
            results.Count, results.Count(b => b.Status == BrowserInstallStatus.ExtensionActive));
    }

    /// <summary>Resolve a binary path to its real path (follow symlinks).</summary>
    private static string ResolveBinaryPath(string binaryPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "readlink",
                Arguments = $"-f \"{binaryPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(1000);
                if (!string.IsNullOrEmpty(output))
                    return output;
            }
        }
        catch { }
        return binaryPath;
    }

    /// <summary>
    /// Inject the extension into Chrome's profile by editing the Preferences JSON file.
    /// This is the only reliable method on Chrome 150+ (--load-extension is ignored).
    /// Uses Python via bash for safe JSON manipulation (avoids C# JSON corruption risk).
    /// Extension ID is computed from SHA256 of the absolute path (Chrome's algorithm).
    /// </summary>
    public BrowserInstallResult InstallExtension(DetectedBrowser browser)
    {
        if (!browser.CanInstall)
            return new BrowserInstallResult(false, false, "Browser is not in a state that can be installed.");

        try
        {
            var alreadyRunning = IsChromeRunning(browser);

            if (alreadyRunning)
            {
                _logger.LogInformation("{Name} is running — closing first", browser.Name);
                KillChromeProcesses();
                for (int i = 0; i < 10 && IsChromeRunning(browser); i++)
                    Thread.Sleep(500);
            }

            if (browser.IsChromeBased)
            {
                // Inject extension into Chrome's Preferences using Python
                var injected = InjectExtensionViaPython(browser);
                if (!injected)
                {
                    _logger.LogWarning("Failed to inject extension via Python");
                    return new BrowserInstallResult(false, false, "Could not inject extension into Chrome profile.");
                }
            }

            // Launch Chrome normally (no flags needed — extension loads from profile)
            var psi = new ProcessStartInfo
            {
                FileName = browser.BinaryPath,
                Arguments = browser.IsChromeBased ? "--no-first-run" : "",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                _logger.LogInformation("Launched {Name} with extension from profile: PID {Pid}", browser.Name, proc.Id);
                return new BrowserInstallResult(true, alreadyRunning, "");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install extension for {Name}", browser.Name);
            return new BrowserInstallResult(false, false, $"Error: {ex.Message}");
        }

        return new BrowserInstallResult(false, false, "Could not start the browser process.");
    }

    /// <summary>
    /// Use Python script to safely edit Chrome's Preferences JSON file.
    /// Computes the extension ID via SHA256 of the path, then adds the entry.
    /// Uses location=4 (EXTERNAL_PREF) with path pointing to the source directory.
    /// Writes the Python script to a temp file to avoid shell quoting issues.
    /// </summary>
    private bool InjectExtensionViaPython(DetectedBrowser browser)
    {
        var pyScript = Path.Combine(Path.GetTempPath(), "alpha_ai_inject_ext.py");
        var argsFile = Path.Combine(Path.GetTempPath(), "alpha_ai_inject_args.json");

        try
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var prefsPath = Path.Combine(userHome, ".config", "google-chrome", "Default", "Preferences");
            var extensionPath = Path.GetFullPath(browser.ExtensionDir);

            if (!File.Exists(prefsPath))
            {
                _logger.LogWarning("Chrome Preferences not found at {Path}", prefsPath);
                return false;
            }

            // Write the Python script to a temp file to avoid shell quoting issues
            File.WriteAllText(pyScript, @"import json, hashlib, os, sys, tempfile

# Read arguments from the JSON file passed as argv[1]
with open(sys.argv[1], 'r') as f:
    args = json.load(f)

prefs_path = args['prefs_path']
ext_path = args['ext_path']
install_time = int(args['install_time'])

# Compute extension ID from path (SHA256 -> first 128 bits -> a-p alphabet)
path_hash = hashlib.sha256(ext_path.encode('utf-8')).hexdigest()
alphabet = 'abcdefghijklmnop'
ext_id = ''
for i in range(16):
    byte_val = int(path_hash[i*2:i*2+2], 16)
    ext_id += alphabet[(byte_val >> 4) & 0xf]
    ext_id += alphabet[byte_val & 0xf]

# Read Preferences
with open(prefs_path, 'r') as f:
    prefs = json.load(f)

# Ensure extensions.settings exists
if 'extensions' not in prefs:
    prefs['extensions'] = {}
if 'settings' not in prefs['extensions']:
    prefs['extensions']['settings'] = {}

# Add our extension entry (same format as the autoFill extension that works)
prefs['extensions']['settings'][ext_id] = {
    'from_webstore': False,
    'state': 1,
    'location': 4,
    'install_time': install_time,
    'path': ext_path,
    'manifest': {
        'name': 'Alpha AI Tracker - Browser Journey',
        'version': '1.0.0',
        'manifest_version': 3
    }
}

# Write back
with open(prefs_path, 'w') as f:
    json.dump(prefs, f, indent=2)

print(f'Injected extension {ext_id} for path {ext_path}')
");

            // Write arguments as a separate JSON file — install_time as a number, not string
            var argsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                prefs_path = prefsPath,
                ext_path = extensionPath,
                install_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            File.WriteAllText(argsFile, argsJson);

            var psi = new ProcessStartInfo
            {
                FileName = "python3",
                Arguments = $"\"{pyScript}\" \"{argsFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("Python injection failed: {Error}", error);
                return false;
            }

            _logger.LogInformation("Chrome extension injected: {Output}", output.Trim());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject extension via Python");
            return false;
        }
        finally
        {
            // Always clean up temp files, even on failure
            try { File.Delete(argsFile); } catch { }
            try { File.Delete(pyScript); } catch { }
        }
    }
    /// <summary>Check if Chrome/Chromium-based process is currently running.
    /// Uses partial name matching ("chrome") because Chrome's process name
    /// is "chrome" not "google-chrome".</summary>
    private static bool IsChromeRunning(DetectedBrowser browser)
    {
        if (!browser.IsChromeBased) return false;
        return IsChromeProcessRunning();
    }

    /// <summary>Check if any chrome process is running.</summary>
    private static bool IsChromeProcessRunning()
    {
        var searchName = "chrome";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pgrep",
                Arguments = searchName,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);
            return proc.ExitCode == 0 && !string.IsNullOrEmpty(output);
        }
        catch
        {
            try
            {
                var fallback = new ProcessStartInfo
                {
                    FileName = "ps",
                    Arguments = "aux",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(fallback);
                if (p == null) return false;
                var allProcs = p.StandardOutput.ReadToEnd();
                p.WaitForExit(1000);
                return allProcs.Contains(searchName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Gracefully kill all Chrome/chromium-based processes.</summary>
    private static void KillChromeProcesses()
    {
        try
        {
            // First try graceful SIGTERM
            var psi = new ProcessStartInfo
            {
                FileName = "pkill",
                Arguments = "chrome",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);

            // Wait a moment then force kill remaining
            Thread.Sleep(500);
            var killPsi = new ProcessStartInfo
            {
                FileName = "pkill",
                Arguments = "-9 chrome",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var killProc = Process.Start(killPsi);
            killProc?.WaitForExit(1000);
        }
        catch
        {
            // pkill might not be available
        }
    }

    /// <summary>
    /// Install native messaging host manifests via install-extensions.sh.
    /// Must run once before the extension can communicate with the tracker.
    /// </summary>
    public async Task<bool> InstallNativeHostAsync(CancellationToken ct)
    {
        try
        {
            var scriptPath = FindInstallScript();
            if (string.IsNullOrEmpty(scriptPath))
            {
                // If script not found, manually create the native host manifest
                return await InstallNativeHostManuallyAsync(ct);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            var error = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("install-extensions.sh failed: {Error}", error);
                return false;
            }

            _logger.LogInformation("Native host installed: {Output}", output);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install native host");
            return false;
        }
    }

    // ─── Private helpers ───

    private async Task AddChromeBrowserAsync(List<DetectedBrowser> results,
        string displayName, string binaryName, string resolvedBinaryPath, CancellationToken ct)
    {
        var nativeHostManifest = Path.Combine(_chromeNativeHostDir, "com.alphai.tracker.json");
        var nativeHostInstalled = File.Exists(nativeHostManifest);
        var status = nativeHostInstalled
            ? BrowserInstallStatus.NativeHostReady
            : BrowserInstallStatus.ReadyToInstall;

        // Check if the extension is already injected in Chrome's profile
        if (nativeHostInstalled && IsExtensionInjectedInProfile())
            status = BrowserInstallStatus.ExtensionActive;

        if (await IsExtensionConnectedAsync(binaryName, ct))
            status = BrowserInstallStatus.ExtensionActive;

        results.Add(new DetectedBrowser
        {
            Name = displayName,
            BinaryPath = resolvedBinaryPath,
            BinaryName = binaryName,
            ExtensionDir = _chromeExtDir,
            Status = status,
            IsChromeBased = true,
            BrowserType = BrowserType.ChromeBased,
            NativeHostInstalled = nativeHostInstalled,
        });
    }

    /// <summary>Check if the extension is already injected in Chrome's Preferences file.</summary>
    private bool IsExtensionInjectedInProfile()
    {
        try
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var prefsPath = Path.Combine(userHome, ".config", "google-chrome", "Default", "Preferences");
            if (!File.Exists(prefsPath)) return false;

            var pyScript = Path.Combine(Path.GetTempPath(), "alpha_ai_check_ext.py");
            try
            {
                File.WriteAllText(pyScript, @"import json, sys

prefs_path = sys.argv[1]
ext_dir = sys.argv[2]

with open(prefs_path, 'r') as f:
    prefs = json.load(f)

settings = prefs.get('extensions', {}).get('settings', {})
for ext_id, ext_data in settings.items():
    path = ext_data.get('path', '')
    if path.startswith(ext_dir):
        print(ext_id)
");

                var psi = new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = $"\"{pyScript}\" \"{prefsPath}\" \"{_chromeExtDir}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(3000);
                return !string.IsNullOrEmpty(output);
            }
            finally
            {
                try { File.Delete(pyScript); } catch { }
            }
        }
        catch
        {
            return false;
        }
    }

    private async Task AddFirefoxAsync(List<DetectedBrowser> results, CancellationToken ct)
    {
        var binaryPath = FindBinary("firefox") ?? FindBinary("firefox-esr");
        if (binaryPath == null) return;

        var nativeHostManifest = Path.Combine(_firefoxNativeHostDir, "com.alphai.tracker.json");
        var nativeHostInstalled = File.Exists(nativeHostManifest);
        var status = nativeHostInstalled
            ? BrowserInstallStatus.NativeHostReady
            : BrowserInstallStatus.ReadyToInstall;

        if (await IsExtensionConnectedAsync("firefox", ct))
            status = BrowserInstallStatus.ExtensionActive;

        results.Add(new DetectedBrowser
        {
            Name = "Firefox",
            BinaryPath = binaryPath,
            BinaryName = "firefox",
            ExtensionDir = _firefoxExtDir,
            Status = status,
            IsChromeBased = false,
            BrowserType = BrowserType.Firefox,
            NativeHostInstalled = nativeHostInstalled,
        });
    }

    private async Task<bool> IsExtensionConnectedAsync(string binaryName, CancellationToken ct)
    {
        if (!File.Exists(_socketPath)) return false;

        try
        {
            // Use lsof to check if any process has the socket open
            // Filter by the browser's binary name to match
            var psi = new ProcessStartInfo
            {
                FileName = "bash",
                Arguments = $"-c \"fuser '{_socketPath}' 2>/dev/null | xargs -I{{}} ps -p {{}} -o comm= --no-headers 2>/dev/null | grep -qi '{binaryName}' && echo 'connected'\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = await proc.StandardOutput.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                return output.Trim() == "connected";
            }
        }
        catch
        {
            // fuser might not be available
        }

        return false;
    }

    private async Task<bool> InstallNativeHostManuallyAsync(CancellationToken ct)
    {
        try
        {
            var nativeHostPy = FindNativeHostPy();
            if (string.IsNullOrEmpty(nativeHostPy)) return false;

            var manifest = new
            {
                name = "com.alphai.tracker",
                description = "Alpha AI Tracker — Native messaging bridge for browser tab/URL capture",
                path = nativeHostPy,
                type = "stdio",
                allowed_origins = Array.Empty<string>(),
            };

            var json = System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });

            // Write to Chrome location
            Directory.CreateDirectory(_chromeNativeHostDir);
            await File.WriteAllTextAsync(
                Path.Combine(_chromeNativeHostDir, "com.alphai.tracker.json"), json, ct);

            // Write to Firefox location
            Directory.CreateDirectory(_firefoxNativeHostDir);
            await File.WriteAllTextAsync(
                Path.Combine(_firefoxNativeHostDir, "com.alphai.tracker.json"), json, ct);

            // Make native-host.py executable
            if (File.Exists(nativeHostPy))
            {
                var chmodPsi = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{nativeHostPy}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                Process.Start(chmodPsi);
            }

            _logger.LogInformation("Native host manifests created manually");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create native host manifests manually");
            return false;
        }
    }

    /// <summary>Resolve the extensions/ directory from the repo root.</summary>
    private static string ResolveExtensionsRoot()
    {
        // Strategy: walk up from BaseDirectory to find extensions/
        var dir = AppDomain.CurrentDomain.BaseDirectory;

        // Try up to 6 levels up (bin/Debug/net10.0/ → client/ → repo root)
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "extensions");
            if (Directory.Exists(candidate))
                return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null) break;
            dir = parent;
        }

        // Last resort: check alongside the executable
        var fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extensions");
        return Directory.Exists(fallback) ? fallback : AppDomain.CurrentDomain.BaseDirectory;
    }

    private static string? FindBinary(string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = name,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);
            return proc.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindNativeHostPy()
    {
        var candidates = new[]
        {
            Path.Combine(ResolveExtensionsRoot(), "..", "native-host.py"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extensions", "native-host.py"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native-host.py"),
        };
        // Normalize paths and check if they exist
        foreach (var c in candidates)
        {
            var normalized = Path.GetFullPath(c);
            if (File.Exists(normalized)) return normalized;
        }
        return null;
    }

    private static string? FindInstallScript()
    {
        var candidates = new[]
        {
            Path.Combine(ResolveExtensionsRoot(), "..", "publish", "install-extensions.sh"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "publish", "install-extensions.sh"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "install-extensions.sh"),
        };
        foreach (var c in candidates)
        {
            var normalized = Path.GetFullPath(c);
            if (File.Exists(normalized)) return normalized;
        }
        return null;
    }
}

// ─── Models ───

public enum BrowserType { ChromeBased, Firefox }

public enum BrowserInstallStatus
{
    /// <summary>Browser binary not found on this system</summary>
    NotInstalled,
    /// <summary>Browser found, extension is ready to be loaded via --load-extension</summary>
    ReadyToInstall,
    /// <summary>Native messaging host installed, extension needs to be loaded in browser</summary>
    NativeHostReady,
    /// <summary>Extension is loaded in browser and actively communicating via socket</summary>
    ExtensionActive,
}

public class DetectedBrowser
{
    public string Name { get; set; } = string.Empty;
    public string BinaryPath { get; set; } = string.Empty;
    public string BinaryName { get; set; } = string.Empty;
    public string ExtensionDir { get; set; } = string.Empty;
    public BrowserInstallStatus Status { get; set; } = BrowserInstallStatus.NotInstalled;
    public BrowserType BrowserType { get; set; } = BrowserType.ChromeBased;
    public bool IsChromeBased { get; set; } = true;
    public bool NativeHostInstalled { get; set; }
    public bool IsInstalled => !string.IsNullOrEmpty(BinaryPath);

    public string StatusText => Status switch
    {
        BrowserInstallStatus.ReadyToInstall => "Ready",
        BrowserInstallStatus.NativeHostReady => "Host Ready",
        BrowserInstallStatus.ExtensionActive => "✅ Active",
        _ => "Not detected",
    };

    public string ActionButtonText => Status switch
    {
        BrowserInstallStatus.ReadyToInstall => "Add Extension",
        BrowserInstallStatus.NativeHostReady => "Launch Browser",
        BrowserInstallStatus.ExtensionActive => "Connected",
        _ => "—",
    };

    public bool CanInstall => Status is BrowserInstallStatus.ReadyToInstall or BrowserInstallStatus.NativeHostReady;
}

/// <summary>Result of a browser extension install attempt.</summary>
public class BrowserInstallResult
{
    public bool Success { get; }
    public bool WasRestarted { get; }
    public string Message { get; }

    public BrowserInstallResult(bool success, bool wasRestarted, string message)
    {
        Success = success;
        WasRestarted = wasRestarted;
        Message = message;
    }
}
