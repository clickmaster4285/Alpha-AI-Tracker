using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Detects installed browsers, manages extension auto-installation,
/// and checks extension connectivity via NativeMessageService heartbeat.
///
/// Strategy (cross-browser, cross-platform):
///   Chrome-based (Chrome, Chromium, Edge, Brave, Opera, Vivaldi):
///     1. Kill running instance
///     2. Try --load-extension=<dir> --enable-automation (works on all Chromium, blocked on branded Chrome 150+)
///     3. Fallback: inject extension into Chrome's Preferences JSON (profile injection)
///   Firefox:
///     Native host installed, show step-by-step instructions (unsigned .xpi cannot auto-install)
/// </summary>
public class BrowserExtensionService
{
    private readonly ILogger<BrowserExtensionService> _logger;
    private readonly NativeMessageService _nativeMessageService;
    private readonly string _chromeExtDir;
    private readonly string _firefoxExtDir;
    private readonly string _socketPath;
    private readonly string _chromeNativeHostDir;
    private readonly string _firefoxNativeHostDir;
    private readonly string _extensionsRoot;

    /// <summary>Cached browser detection results.</summary>
    public IReadOnlyList<DetectedBrowser> DetectedBrowsers => _detected;
    private List<DetectedBrowser> _detected = new();

    /// <summary>True if at least one browser has the extension active (confirmed via socket heartbeat).</summary>
    public bool IsAnyExtensionActive => _detected.Any(b => b.Status == BrowserInstallStatus.ExtensionActive);

    /// <summary>
    /// Raised when the NativeMessageService heartbeat flips. Args:
    ///   (browserName, isActive). Subscribers (MainViewModel) update DetectedBrowsers
    ///   in real time so the user sees "Connected" the moment the first ping arrives
    ///   — not on the next manual refresh.
    /// </summary>
    public event Action<string, bool>? ExtensionConnectionChanged;

    // Polls the heartbeat at 2s intervals so the UI updates within ~2s of the
    // first ping, instead of waiting for the next user-triggered refresh.
    private readonly System.Threading.Timer _heartbeatTimer;
    private bool _lastHeartbeatState;

    public BrowserExtensionService(ILogger<BrowserExtensionService> logger, NativeMessageService nativeMessageService)
    {
        _logger = logger;
        _nativeMessageService = nativeMessageService;
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Resolve extension directories — walk up from build output to repo root
        _extensionsRoot = ResolveExtensionsRoot();
        _chromeExtDir = Path.Combine(_extensionsRoot, "chrome");
        _firefoxExtDir = Path.Combine(_extensionsRoot, "firefox");

        _socketPath = Path.Combine(userHome, ".local", "share", "alpha-ai-tracker", "native-messaging.sock");
        _chromeNativeHostDir = Path.Combine(userHome, ".config", "google-chrome", "NativeMessagingHosts");
        _firefoxNativeHostDir = Path.Combine(userHome, ".mozilla", "native-messaging-hosts");

        _heartbeatTimer = new System.Threading.Timer(_ => PollHeartbeat(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        // Clean up any orphaned native-host.py from a previous crashed session.
        // Stale processes would cause false positives in process-based detection;
        // with heartbeat-based detection they are harmless but wasteful.
        KillOrphanedNativeHostProcesses();
    }

    /// <summary>
    /// Polls NativeMessageService.IsExtensionConnected() and emits ExtensionConnectionChanged
    /// when the state flips. Updates _detected in-place so existing DetectedBrowser
    /// instances (the same references the ViewModel is already rendering) flip status.
    /// </summary>
    private void PollHeartbeat()
    {
        try
        {
            var active = _nativeMessageService.IsExtensionConnected();
            if (active == _lastHeartbeatState) return;
            _lastHeartbeatState = active;

            // Flip every browser to the new state. We don't know which browser
            // specifically sent the heartbeat (the socket is shared), so apply to
            // all browsers currently in NativeHostReady or Loading state.
            foreach (var b in _detected)
            {
                if (active)
                {
                    if (b.Status == BrowserInstallStatus.Loading ||
                        b.Status == BrowserInstallStatus.NativeHostReady)
                    {
                        b.Status = BrowserInstallStatus.ExtensionActive;
                    }
                }
                else
                {
                    if (b.Status == BrowserInstallStatus.ExtensionActive)
                    {
                        b.Status = BrowserInstallStatus.NativeHostReady;
                    }
                }
            }

            // Fire-and-forget: callers handle on UI thread if they need to.
            try { ExtensionConnectionChanged?.Invoke("all", active); }
            catch (Exception ex) { _logger.LogDebug(ex, "ExtensionConnectionChanged handler threw"); }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Heartbeat poll failed");
        }
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
    /// Install (inject/load) the extension into the browser.
    /// Async to avoid freezing the UI thread (Strategy 1 polls for native-host.py).
    ///
    /// Strategy (Chrome-based):
    ///   1. Kill existing browser process
    ///   2. Try launching with --load-extension + --enable-automation
    ///      (Works on: Chromium, Brave, Edge, Opera, Vivaldi, and Chrome with automation flag)
    ///   3. If that fails (branded Chrome 150+ blocks --load-extension),
    ///      fall back to profile injection (Preferences JSON edit)
    /// </summary>
    public async Task<BrowserInstallResult> InstallExtensionAsync(DetectedBrowser browser)
    {
        if (!browser.CanInstall)
            return new BrowserInstallResult(false, false, "Browser is not in a state that can be installed.");

        // Re-resolve the extension dir at install time so we never launch Chrome
        // pointing at the dev-tree path when the user is running the installed binary.
        if (browser.IsChromeBased)
        {
            var resolvedDir = ResolveChromeExtensionDir();
            if (Directory.Exists(resolvedDir))
                browser.ExtensionDir = resolvedDir;
        }

        try
        {
            var alreadyRunning = IsChromeRunning(browser);

            if (alreadyRunning)
            {
                _logger.LogInformation("{Name} is running — closing first", browser.Name);
                KillBrowserProcesses(browser.IsChromeBased ? "chrome" : "firefox");
                for (int i = 0; i < 10 && IsChromeRunning(browser); i++)
                    await Task.Delay(500);
            }

            if (browser.IsChromeBased)
            {
                // ─── Strategy 1: Try --load-extension (works on all Chromium browsers) ───
                bool launchedWithFlag = await TryLaunchWithLoadExtensionAsync(browser);

                if (launchedWithFlag)
                {
                    _logger.LogInformation(
                        "Launched {Name} with --load-extension (Strategy 1)", browser.Name);
                    return new BrowserInstallResult(true, alreadyRunning, "");
                }

                _logger.LogInformation(
                    "--load-extension did not work for {Name}, trying profile injection (Strategy 2)",
                    browser.Name);

                // ─── Strategy 2: Fallback — inject into Preferences JSON ───
                var injected = InjectExtensionViaPython(browser);
                if (!injected)
                {
                    _logger.LogWarning("Profile injection also failed for {Name}", browser.Name);
                    return new BrowserInstallResult(false, false,
                        "Could not inject extension into browser profile.");
                }

                // Launch browser — extension should load from profile.
                // Strategy 1 may have left a Chrome process behind (started but without
                // --load-extension honoring it); kill that before relaunching so the
                // profile-injected extension is the one actually loaded.
                if (IsChromeRunning(browser))
                {
                    _logger.LogInformation("Killing leftover Chrome from Strategy 1 before profile-injected launch");
                    KillBrowserProcesses("chrome");
                    for (int i = 0; i < 10 && IsChromeRunning(browser); i++)
                        await Task.Delay(500);
                }

                // 🟡 FIX 2026-07-28: Removed --no-first-run (causes profile corruption on Chrome 150+).
                // Added --disable-gcm to suppress GCM DEPRECATED_ENDPOINT noise from Chrome's internal
                // push notification system (not needed by our extension).
                var psi = new ProcessStartInfo
                {
                    FileName = browser.BinaryPath,
                    Arguments = "--disable-gcm",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    _logger.LogInformation(
                        "Launched {Name} with extension from profile (Strategy 2): PID {Pid}",
                        browser.Name, proc.Id);
                    return new BrowserInstallResult(true, alreadyRunning, "");
                }
            }
            else
            {
                // Firefox — launch normally, show instructions
                var ffPsi = new ProcessStartInfo
                {
                    FileName = browser.BinaryPath,
                    Arguments = "",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var ffProc = Process.Start(ffPsi);
                if (ffProc != null)
                {
                    _logger.LogInformation("Launched {Name}: PID {Pid}", browser.Name, ffProc.Id);
                    return new BrowserInstallResult(true, alreadyRunning, "");
                }
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
    /// Try launching the browser with --load-extension and --enable-automation flags.
    /// Polls for native-host.py up to 7.5s to verify the extension actually loaded.
    ///
    /// Works on all Chromium-based browsers:
    ///   - Chromium, Brave, Edge, Opera, Vivaldi: always works
    ///   - Branded Google Chrome 150+: blocked, Chrome silently ignores --load-extension
    ///
    /// Detection: after launching, check if native-host.py was spawned by the new Chrome process.
    /// If not detected within 7.5s, kills the Chrome instance and returns false for fallback.
    /// </summary>
    private async Task<bool> TryLaunchWithLoadExtensionAsync(DetectedBrowser browser)
    {
        try
        {
            var extDir = Path.GetFullPath(browser.ExtensionDir);
                // 🟡 FIX 2026-07-28: Removed --no-first-run which caused "incorrect profile type"
                // on Chrome 150+, triggering cascade of GCM DEPRECATED_ENDPOINT errors and
                // eventually Mojo IPC crashes (WidgetHost) that kill the extension service worker.
                // Added --disable-gcm to suppress GCM noise from Chrome's internal push system.
                var args = $"--load-extension=\"{extDir}\" --disable-gcm";

            var psi = new ProcessStartInfo
            {
                FileName = browser.BinaryPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var proc = Process.Start(psi);
            if (proc == null) return false;

            _logger.LogInformation(
                "Launched {Name} with --load-extension (Strategy 1): PID {Pid}",
                browser.Name, proc.Id);

            // Poll for up to 7.5s to detect native-host.py
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(500);

                if (await IsNativeHostRunningForPidAsync(proc.Id))
                {
                    _logger.LogInformation(
                        "Extension confirmed loaded for {Name} (native-host detected)", browser.Name);
                    return true;
                }
            }

            _logger.LogInformation(
                "Extension was NOT loaded via --load-extension (Chrome blocked it). " +
                "Killing instance and falling back to profile injection.");
            KillBrowserProcesses("chrome");
            await Task.Delay(1000);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TryLaunchWithLoadExtension failed");
            return false;
        }
    }

    /// <summary>
    /// Check if native-host.py is running as a child (or descendant) of the given Chrome PID.
    /// Uses pgrep on Linux, ps on macOS, wmic on Windows.
    /// Returns false if the detection tool is unavailable (assume not loaded).
    /// </summary>
    private async Task<bool> IsNativeHostRunningForPidAsync(int parentPid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // pgrep -P <pid> shows child processes
                var psi = new ProcessStartInfo
                {
                    FileName = "pgrep",
                    Arguments = $"-P {parentPid}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var children = (await proc.StandardOutput.ReadToEndAsync()).Trim();
                    proc.WaitForExit(1000);

                    if (!string.IsNullOrEmpty(children))
                    {
                        foreach (var childPid in children.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (int.TryParse(childPid.Trim(), out var pid))
                            {
                                var checkPsi = new ProcessStartInfo
                                {
                                    FileName = "ps",
                                    Arguments = $"-p {pid} -o comm= --no-headers",
                                    RedirectStandardOutput = true,
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                };
                                using var checkProc = Process.Start(checkPsi);
                                if (checkProc != null)
                                {
                                    var comm = (await checkProc.StandardOutput.ReadToEndAsync()).Trim();
                                    checkProc.WaitForExit(500);
                                    if (comm.Contains("python3", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var cmdPsi = new ProcessStartInfo
                                        {
                                            FileName = "ps",
                                            Arguments = $"-p {pid} -o args= --no-headers",
                                            RedirectStandardOutput = true,
                                            UseShellExecute = false,
                                            CreateNoWindow = true,
                                        };
                                        using var cmdProc = Process.Start(cmdPsi);
                                        if (cmdProc != null)
                                        {
                                            var cmdline = (await cmdProc.StandardOutput.ReadToEndAsync()).Trim();
                                            cmdProc.WaitForExit(500);
                                            if (cmdline.Contains("native-host.py", StringComparison.OrdinalIgnoreCase))
                                                return true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ps",
                    Arguments = $"-o pid,comm -p {parentPid}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
                    proc.WaitForExit(1000);
                    if (output.Contains("native-host", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "wmic",
                    Arguments = $"process where (ParentProcessId={parentPid}) get ProcessId,Name",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
                    proc.WaitForExit(2000);
                    if (output.Contains("python", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("native-host", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// Use Python script to safely edit Chrome's Preferences JSON file.
    /// Computes the extension ID via SHA256 of the path, then adds the entry.
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

            File.WriteAllText(pyScript, @"import json, hashlib, os, sys, tempfile

with open(sys.argv[1], 'r') as f:
    args = json.load(f)

prefs_path = args['prefs_path']
ext_path = args['ext_path']
install_time = int(args['install_time'])

path_hash = hashlib.sha256(ext_path.encode('utf-8')).hexdigest()
alphabet = 'abcdefghijklmnop'
ext_id = ''
for i in range(16):
    byte_val = int(path_hash[i*2:i*2+2], 16)
    ext_id += alphabet[(byte_val >> 4) & 0xf]
    ext_id += alphabet[byte_val & 0xf]

with open(prefs_path, 'r') as f:
    prefs = json.load(f)

if 'extensions' not in prefs:
    prefs['extensions'] = {}
if 'settings' not in prefs['extensions']:
    prefs['extensions']['settings'] = {}

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

with open(prefs_path, 'w') as f:
    json.dump(prefs, f, indent=2)

print(f'Injected extension {ext_id} for path {ext_path}')
");

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
            try { File.Delete(argsFile); } catch { }
            try { File.Delete(pyScript); } catch { }
        }
    }

    /// <summary>Check if Chrome/Chromium-based process is currently running.</summary>
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

    /// <summary>Gracefully kill browser processes.</summary>
    private static void KillBrowserProcesses(string processName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pkill",
                Arguments = processName,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);

            Thread.Sleep(500);
            var killPsi = new ProcessStartInfo
            {
                FileName = "pkill",
                Arguments = $"-9 {processName}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var killProc = Process.Start(killPsi);
            killProc?.WaitForExit(1000);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Install native messaging host manifests via install-extensions.sh.
    /// </summary>
    public async Task<bool> InstallNativeHostAsync(CancellationToken ct)
    {
        try
        {
            var scriptPath = FindInstallScript();
            if (string.IsNullOrEmpty(scriptPath))
            {
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

        // Check if extension is active by detecting native-host.py + Chrome running
        if (await IsExtensionActiveAsync(ct))
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

    private async Task AddFirefoxAsync(List<DetectedBrowser> results, CancellationToken ct)
    {
        var binaryPath = FindBinary("firefox") ?? FindBinary("firefox-esr");
        if (binaryPath == null) return;

        var nativeHostManifest = Path.Combine(_firefoxNativeHostDir, "com.alphai.tracker.json");
        var nativeHostInstalled = File.Exists(nativeHostManifest);
        var status = nativeHostInstalled
            ? BrowserInstallStatus.NativeHostReady
            : BrowserInstallStatus.ReadyToInstall;

        if (await IsExtensionActiveAsync(ct))
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

    /// <summary>
    /// Check if the browser extension is actively connected by querying the
    /// NativeMessageService heartbeat — the extension's background.js sends a
    /// ping every ~27s via chrome.alarms. If we've received one within 60s the
    /// full pipeline (extension → native-host.py → Unix socket) is confirmed alive.
    ///
    /// This is reliable because native-host.py forwards ping messages to the
    /// tracker socket, and NativeMessageService records a heartbeat timestamp.
    /// Unlike the previous process-based heuristic (pgrep native-host.py + pgrep chrome),
    /// this cannot false-positive on orphaned processes or unrelated browser instances.
    /// </summary>
    private Task<bool> IsExtensionActiveAsync(CancellationToken ct)
    {
        var connected = _nativeMessageService.IsExtensionConnected();
        return Task.FromResult(connected);
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

    private async Task<bool> InstallNativeHostManuallyAsync(CancellationToken ct)
    {
        try
        {
            // ─── Resolve at install-time, not at service construction time. ───
            // The static _chromeExtDir / FindNativeHostPy can point at the dev tree
            // (e.g. when running from /usr/share/alpha-ai-tracker/client but the manifest
            // was last written by install-extensions.sh from /media/devteam/...).
            // The manifest MUST point at the install paths that the running binary uses,
            // otherwise Chrome launches native-host.py from a path that doesn't exist
            // and silently drops every native messaging call.
            var nativeHostPy = ResolveNativeHostPyPath();
            if (!File.Exists(nativeHostPy))
            {
                _logger.LogWarning(
                    "native-host.py not found at {Path} — cannot install native host", nativeHostPy);
                return false;
            }

            var extensionDir = ResolveChromeExtensionDir();
            var extId = ComputeExtensionId(extensionDir);
            if (extId == "unknown")
            {
                _logger.LogWarning(
                    "Could not compute extension ID for {Path} — manifest allowed_origins will be empty",
                    extensionDir);
            }

            var manifest = new
            {
                name = "com.alphai.tracker",
                description = "Alpha AI Tracker — Native messaging bridge for browser tab/URL capture",
                path = nativeHostPy,
                type = "stdio",
                allowed_origins = extId == "unknown"
                    ? Array.Empty<string>()
                    : new[] { $"chrome-extension://{extId}/" }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });

            // Write JSON-file manifest (Linux/macOS look here; Windows ignores it but
            // it's harmless and lets reg-lookup failures fall back to file inspection).
            Directory.CreateDirectory(_chromeNativeHostDir);
            await File.WriteAllTextAsync(
                Path.Combine(_chromeNativeHostDir, "com.alphai.tracker.json"), json, ct);

            Directory.CreateDirectory(_firefoxNativeHostDir);
            await File.WriteAllTextAsync(
                Path.Combine(_firefoxNativeHostDir, "com.alphai.tracker.json"), json, ct);

            // Windows: Chrome/Edge/Brave look in the registry, not a file. Write a
            // manifest next to native-host.py (so the registered path exists) and
            // register the registry keys pointing at it.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var sideBySideManifest = Path.Combine(
                        Path.GetDirectoryName(nativeHostPy) ?? AppDomain.CurrentDomain.BaseDirectory,
                        "com.alphai.tracker.json");
                    await File.WriteAllTextAsync(sideBySideManifest, json, ct);
                    RegisterNativeHostWindows(sideBySideManifest, extId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to register Windows native messaging host");
                }
            }

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

            _logger.LogInformation(
                "Native host manifests created (path: {Path}, extension ID: {ExtId})",
                nativeHostPy, extId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create native host manifests manually");
            return false;
        }
    }

    /// <summary>
    /// Resolve the absolute path to native-host.py from the running executable.
    ///
    /// Order (first hit wins):
    ///   1. <code>{BaseDirectory}/extensions/native-host.py</code> — install bundle
    ///   2. <code>{BaseDirectory}/publish/extensions/native-host.py</code> — install bundle variant
    ///   3. <code>{BaseDirectory}/native-host.py</code> — flat bundle
    ///   4. Walk up to 6 levels from BaseDirectory and from Environment.ProcessPath looking
    ///      for any sibling native-host.py — handles the dev layout where extensions live at
    ///      the repo root, not next to the binary.
    ///
    /// Unlike the static _chromeExtDir, this is safe to call at install time because it
    /// re-walks the filesystem rather than caching a path captured at process start.
    /// </summary>
    public static string ResolveNativeHostPyPath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var sameDir = Path.Combine(baseDir, "native-host.py");

        var candidates = new[]
        {
            Path.Combine(baseDir, "extensions", "native-host.py"),
            Path.Combine(baseDir, "publish", "extensions", "native-host.py"),
            sameDir,
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        // Walk up from BaseDirectory
        var dir = baseDir;
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            var c = Path.Combine(dir, "native-host.py");
            if (File.Exists(c)) return Path.GetFullPath(c);
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        // Walk up from the actual executable (handles Windows apphost shims)
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                var exeDir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    var walk = exeDir;
                    for (int i = 0; i < 6 && !string.IsNullOrEmpty(walk); i++)
                    {
                        var c = Path.Combine(walk, "native-host.py");
                        if (File.Exists(c)) return Path.GetFullPath(c);
                        var parent = Path.GetDirectoryName(walk);
                        if (parent == null || parent == walk) break;
                        walk = parent;
                    }
                }
            }
        }
        catch { /* best-effort */ }

        // Fall back to sameDir — non-existent, but downstream code already handles that
        // (returns false from InstallNativeHostManuallyAsync).
        return Path.GetFullPath(sameDir);
    }

    /// <summary>
    /// Resolve the absolute path to the chrome/ extension directory.
    /// Same multi-strategy walk as <see cref="ResolveNativeHostPyPath"/> but for the
    /// chrome/ subdirectory. Use this anywhere a browser launch needs the extension dir
    /// at install time, not the cached _chromeExtDir captured at construction.
    /// </summary>
    public static string ResolveChromeExtensionDir()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "extensions", "chrome"),
            Path.Combine(baseDir, "publish", "extensions", "chrome"),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c)) return Path.GetFullPath(c);
        }

        // Walk up from BaseDirectory
        var dir = baseDir;
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            var c = Path.Combine(dir, "extensions", "chrome");
            if (Directory.Exists(c)) return Path.GetFullPath(c);
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        // Walk up from the executable path (Windows apphost shims)
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                var exeDir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    var walk = exeDir;
                    for (int i = 0; i < 6 && !string.IsNullOrEmpty(walk); i++)
                    {
                        var c = Path.Combine(walk, "extensions", "chrome");
                        if (Directory.Exists(c)) return Path.GetFullPath(c);
                        var parent = Path.GetDirectoryName(walk);
                        if (parent == null || parent == walk) break;
                        walk = parent;
                    }
                }
            }
        }
        catch { /* best-effort */ }

        return Path.Combine(baseDir, "extensions", "chrome");
    }

    /// <summary>
    /// Register the native messaging host in the Windows registry for every Chromium
    /// browser we know about. Chrome on Windows reads HKCU\Software\&lt;Vendor&gt;\&lt;Browser&gt;\NativeMessagingHosts\&lt;name&gt;
    /// instead of a filesystem path. The (Default) value must be the absolute path to a
    /// manifest.json file that already exists on disk.
    ///
    /// No-op on non-Windows platforms.
    /// </summary>
    private void RegisterNativeHostWindows(string manifestPath, string extensionId)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!File.Exists(manifestPath))
        {
            _logger.LogWarning("Manifest file does not exist at {Path} — skipping registry write", manifestPath);
            return;
        }

        // Each browser reads its own vendor key. Path format:
        //   HKCU\Software\<Vendor>\<Browser>\NativeMessagingHosts\<host-name>
        //   (Default) REG_SZ = <absolute path to manifest.json>
        var registryRoots = new[]
        {
            @"Software\Google\Chrome\NativeMessagingHosts",
            @"Software\Microsoft\Edge\NativeMessagingHosts",
            @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
            @"Software\Vivaldi\NativeMessagingHosts",
            @"Software\Opera Software\Opera Stable\NativeMessagingHosts",
        };
        var hostName = "com.alphai.tracker";

        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser;
            foreach (var root in registryRoots)
            {
                try
                {
                    using var rootKey = hkcu.CreateSubKey(root, writable: true);
                    if (rootKey == null) continue;

                    using var hostKey = rootKey.CreateSubKey(hostName, writable: true);
                    if (hostKey == null) continue;

                    // (Default) value name is null in RegistryKey.SetValue on .NET 5+.
                    hostKey.SetValue(null, manifestPath, Microsoft.Win32.RegistryValueKind.String);

                    _logger.LogInformation(
                        "Registered native messaging host in Windows registry: {Root}\\{Name}",
                        root, hostName);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to register under {Root}", root);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows registry registration failed");
        }
    }

    /// <summary>
    /// Compute Chrome extension ID from an absolute directory path.
    /// Chrome uses SHA256 of the path, takes the first 128 bits,
    /// and maps each 4-bit nibble to letters a-p.
    /// </summary>
    private static string ComputeExtensionId(string extensionPath)
    {
        try
        {
            var pyScript = Path.Combine(Path.GetTempPath(), "alpha_ai_compute_id.py");
            try
            {
                File.WriteAllText(pyScript, @"import hashlib, sys

ext_path = sys.argv[1]
path_hash = hashlib.sha256(ext_path.encode('utf-8')).hexdigest()
alphabet = 'abcdefghijklmnop'
ext_id = ''
for i in range(16):
    byte_val = int(path_hash[i*2:i*2+2], 16)
    ext_id += alphabet[(byte_val >> 4) & 0xf]
    ext_id += alphabet[byte_val & 0xf]
print(ext_id)
");

                var psi = new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = $"\"{pyScript}\" \"{extensionPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return "unknown";
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(3000);
                if (!string.IsNullOrEmpty(output))
                    return output;
            }
            finally
            {
                try { File.Delete(pyScript); } catch { }
            }
        }
        catch { }

        return "unknown";
    }

    /// <summary>Resolve the extensions/ directory from the running executable.
    /// Order (first hit wins):
    ///   1. <code>{BaseDirectory}/extensions</code> — install bundle, portabledir, or `dotnet run`
    ///   2. Walk up to 6 levels looking for <code>extensions/</code> — repo dev layout
    ///   3. Walk up to 6 levels looking for <code>extensions/</code> relative to the executable
    ///      (handles apphost shims where BaseDirectory differs from the actual exe dir)
    ///   4. Fall back to <code>{BaseDirectory}/extensions</code> regardless of existence so
    ///      downstream path-joining still produces a usable absolute path (the browser launch
    ///      will simply fail and the user can click "Show instructions" to see the path).
    ///
    /// This is intentionally lenient: a non-existent path is preferable to a wrong one, and
    /// the browser-extension install flow already handles the "extension dir not found" case
    /// by showing manual-installation instructions.
    /// </summary>
    private static string ResolveExtensionsRoot()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 1. Same-directory extensions/ — primary path for both install and `dotnet run`.
        var sameDir = Path.Combine(baseDir, "extensions");
        if (Directory.Exists(sameDir))
            return sameDir;

        // 2. Walk up from BaseDirectory (handles odd test runners / multi-level bin/...).
        var dir = baseDir;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "extensions");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null) break;
            dir = parent;
        }

        // 3. Walk up from the actual executable path. On Windows the apphost can have a
        //    BaseDirectory different from the executable directory when the .exe is shimmed.
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                var exeDir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    var walkDir = exeDir;
                    for (int i = 0; i < 6; i++)
                    {
                        var candidate = Path.Combine(walkDir, "extensions");
                        if (Directory.Exists(candidate)) return candidate;
                        var parent = Path.GetDirectoryName(walkDir);
                        if (parent == null) break;
                        walkDir = parent;
                    }
                }
            }
        }
        catch { /* best-effort */ }

        // 4. Last-resort fallback — return the best-guess path even if it doesn't exist,
        //    so call sites that just want to *display* the path can still do so.
        return sameDir;
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
        var extRoot = ResolveExtensionsRoot();
        var candidates = new[]
        {
            Path.Combine(extRoot, "native-host.py"),
            Path.Combine(extRoot, "..", "native-host.py"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extensions", "native-host.py"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "native-host.py"),
        };
        foreach (var c in candidates)
        {
            var normalized = Path.GetFullPath(c);
            if (File.Exists(normalized)) return normalized;
        }
        return null;
    }

    private static string? FindInstallScript()
    {
        var extRoot = ResolveExtensionsRoot();
        var candidates = new[]
        {
            Path.Combine(extRoot, "..", "publish", "install-extensions.sh"),
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

    /// <summary>Kill orphaned native-host.py processes left over from a previous crashed session.
    /// This prevents stale processes from consuming resources or confusing any fallback checks.</summary>
    private static void KillOrphanedNativeHostProcesses()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "pkill",
                    Arguments = "-f native-host.py",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                proc?.WaitForExit(2000);
            }
            else if (OperatingSystem.IsMacOS())
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "pkill",
                    Arguments = "-f native-host.py",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                proc?.WaitForExit(2000);
            }
            else if (OperatingSystem.IsWindows())
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/F /IM python.exe /FI \"WINDOWTITLE eq native-host*\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                proc?.WaitForExit(2000);
            }
        }
        catch
        {
            // Best-effort cleanup — ignore failures
        }
    }
}

// ─── Models ───

public enum BrowserType { ChromeBased, Firefox }

public enum BrowserInstallStatus
{
    NotInstalled,
    ReadyToInstall,
    NativeHostReady,
    Loading,
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
        BrowserInstallStatus.Loading => "Connecting…",
        BrowserInstallStatus.ExtensionActive => "✅ Active",
        _ => "Not detected",
    };

    public string ActionButtonText => Status switch
    {
        BrowserInstallStatus.ReadyToInstall => "Add Extension",
        BrowserInstallStatus.NativeHostReady => "Launch Browser",
        BrowserInstallStatus.Loading => "Connecting…",
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
