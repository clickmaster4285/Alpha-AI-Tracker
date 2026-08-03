using System.Diagnostics;
using client.Core;
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

        _socketPath = NativeMessagingPaths.SocketPath;
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
    ///
    /// Phase 2: inclusion is driven entirely by OS-level http/https handler
    /// registration (BrowserDetector) — no hardcoded brand list. Each candidate
    /// is then classified by PROFILE SHAPE into Chromium / Gecko / Unknown
    /// (BrowserEngineDetector). The named-brand list survives only as the
    /// display-only BrandCatalog (nicer name + config-dir hint); it never gates
    /// scanning, inclusion, or classification.
    /// </summary>
    public async Task ScanAsync(CancellationToken ct)
    {
        var results = new List<DetectedBrowser>();
        var resolvedBinaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidates = BrowserDetector.DetectAll();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.BinaryPath) &&
                string.IsNullOrWhiteSpace(candidate.FlatpakAppId))
                continue;

            var resolved = ResolveBinaryPath(candidate.BinaryPath);
            if (!resolvedBinaries.Add(resolved)) continue; // skip duplicate (same canonical binary)

            var engine = BrowserEngineDetector.DetectFor(candidate);
            var (brandName, _) = BrandCatalog.Find(candidate);

            var nativeHostManifest = GetNativeHostManifestPath(candidate, engine);
            // Windows hosts live in HKCU (RegisterNativeHostWindows), not on disk —
            // the .config path would never exist there, so check the registry instead.
            var nativeHostInstalled = OperatingSystem.IsWindows()
                ? IsWindowsNativeHostRegistered()
                : File.Exists(nativeHostManifest);
            var status = nativeHostInstalled
                ? BrowserInstallStatus.NativeHostReady
                : BrowserInstallStatus.ReadyToInstall;

            if (await IsExtensionActiveAsync(ct))
                status = BrowserInstallStatus.ExtensionActive;

            var isChromeBased = engine == BrowserEngine.Chromium;
            var profileRoot = BrowserEngineDetector.FindProfileRoot(candidate);
            results.Add(new DetectedBrowser
            {
                Name = brandName ?? candidate.Name,
                BinaryPath = resolved,
                BinaryName = candidate.BinaryName,
                ExtensionDir = engine == BrowserEngine.Gecko ? _firefoxExtDir : _chromeExtDir,
                Status = status,
                IsChromeBased = isChromeBased,
                BrowserType = isChromeBased ? BrowserType.ChromeBased : BrowserType.Firefox,
                NativeHostInstalled = nativeHostInstalled,
                Engine = engine,
                IsDefault = candidate.IsDefault,
                DesktopId = candidate.DesktopId,
                Icon = candidate.Icon,
                FlatpakAppId = candidate.FlatpakAppId,
                ConfigDir = profileRoot?.Root ?? string.Empty,
                DefaultProfileDir = string.IsNullOrEmpty(profileRoot?.DefaultProfile)
                    ? string.Empty
                    : Path.Combine(profileRoot.Value.Root, profileRoot.Value.DefaultProfile),
                ManifestPath = nativeHostManifest,
            });
        }

        _detected = results;
        _logger.LogInformation("Browser scan: {Count} detected, {Active} active — engines: {Engines}",
            results.Count, results.Count(b => b.Status == BrowserInstallStatus.ExtensionActive),
            string.Join(", ", results.Select(b => $"{b.Name}={b.Engine}{(b.IsDefault ? "*" : "")}")));
    }

    /// <summary>
    /// Per-browser native-messaging manifest path. Firefox (Gecko) always reads
    /// ~/.mozilla/native-messaging-hosts; Chromium browsers read
    /// ~/.config/&lt;brand-config-dir&gt;/NativeMessagingHosts (Linux) or
    /// ~/Library/Application Support/&lt;brand&gt;/NativeMessagingHosts (macOS).
    /// Windows is registry-based (RegisterNativeHostWindows) and is not covered here.
    /// </summary>
    private static string GetNativeHostManifestPath(BrowserCandidate candidate, BrowserEngine engine)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var manifestName = "com.alphai.tracker.json";

        if (engine == BrowserEngine.Gecko)
            return Path.Combine(userHome, ".mozilla", "native-messaging-hosts", manifestName);

        if (OperatingSystem.IsMacOS())
        {
            var (_, configHint) = BrandCatalog.Find(candidate);
            var dir = string.IsNullOrEmpty(configHint)
                ? candidate.BinaryName
                : configHint.Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(userHome, "Library", "Application Support", dir,
                "NativeMessagingHosts", manifestName);
        }

        var (_, hint) = BrandCatalog.Find(candidate);
        var configDir = string.IsNullOrEmpty(hint)
            ? candidate.BinaryName
            : hint.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(userHome, ".config", configDir, "NativeMessagingHosts", manifestName);
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
                var injected = InjectExtensionIntoProfile(browser);
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
                var (launchFile2, launchArgs2) = BuildLaunch(browser, "--disable-gcm");
                var psi = new ProcessStartInfo
                {
                    FileName = launchFile2,
                    Arguments = launchArgs2,
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
                var (ffFile, ffArgs) = BuildLaunch(browser, string.Empty);
                var ffPsi = new ProcessStartInfo
                {
                    FileName = ffFile,
                    Arguments = ffArgs,
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

            var (launchFile, launchArgs) = BuildLaunch(browser, args);
            var psi = new ProcessStartInfo
            {
                FileName = launchFile,
                Arguments = launchArgs,
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
                // Phase 4: wmic is deprecated (removed on Windows 11 24H2+ / Server 2025).
                // PowerShell Get-CimInstance Win32_Process has no legacy dependency.
                // Match host processes by command line: the Python host (current pipeline,
                // native-host.py) OR the C# host mode markers (--native-host,
                // chrome-extension://, or the Gecko app id — the C# host shares the
                // tracker's exe name, so bare-name matching would hit the tracker itself).
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -Command " +
                                $"\"Get-CimInstance Win32_Process -Filter 'ParentProcessId={parentPid}' " +
                                "| ForEach-Object { $_.CommandLine }\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
                    proc.WaitForExit(2000);
                    if (output.Contains("native-host.py", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("--native-host", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("alpha-ai-tracker@alphai.com", StringComparison.OrdinalIgnoreCase))
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
    /// Inject the unpacked extension into a Chromium browser's Preferences JSON using
    /// pure C# (System.Text.Json) — replaces the former Python implementation.
    /// Computes the extension ID via <see cref="ExtensionIdCalculator"/>, then
    /// adds the <c>extensions.settings[&lt;id&gt;]</c> entry (location=4).
    ///
    /// Phase 3: generalized to ANY Chromium browser — the Preferences path comes from
    /// the per-browser resolved profile (browser.DefaultProfileDir), not a hardcoded
    /// google-chrome path. Idempotent: re-clicking never duplicates the entry when the
    /// existing entry already points at the same extension path.
    /// </summary>
    private bool InjectExtensionIntoProfile(DetectedBrowser browser)
    {
        try
        {
            EnsureProfilePaths(browser);

            var prefsPath = Path.Combine(browser.DefaultProfileDir, "Preferences");
            if (!File.Exists(prefsPath))
            {
                _logger.LogWarning("Preferences not found for {Name} at {Path}", browser.Name, prefsPath);
                return false;
            }

            var extensionPath = Path.GetFullPath(browser.ExtensionDir);
            var extId = ExtensionIdCalculator.Compute(extensionPath);
            var installTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var prefs = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(
                File.ReadAllText(prefsPath));
            if (prefs == null) prefs = new System.Text.Json.Nodes.JsonObject();

            var extensions = prefs["extensions"] as System.Text.Json.Nodes.JsonObject
                             ?? new System.Text.Json.Nodes.JsonObject();
            var settings = extensions["settings"] as System.Text.Json.Nodes.JsonObject
                           ?? new System.Text.Json.Nodes.JsonObject();

            // Idempotency: an existing entry for this extension ID pointing at the same
            // path means the injection is already in place — do not rewrite it.
            if (settings[extId] is System.Text.Json.Nodes.JsonObject existing &&
                existing["path"]?.GetValue<string>() == extensionPath)
            {
                _logger.LogInformation(
                    "Extension already injected for {Name} ({ExtId}) — skipping duplicate write",
                    browser.Name, extId);
                return true;
            }

            settings[extId] = new System.Text.Json.Nodes.JsonObject
            {
                ["from_webstore"] = false,
                ["state"] = 1,
                ["location"] = 4,
                ["install_time"] = installTime,
                ["path"] = extensionPath,
                ["manifest"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = "Alpha AI Tracker - Browser Journey",
                    ["version"] = "1.0.0",
                    ["manifest_version"] = 3,
                },
            };

            extensions["settings"] = settings;
            prefs["extensions"] = extensions;

            File.WriteAllText(prefsPath,
                prefs.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            _logger.LogInformation(
                "Extension injected (pure C#) for {Name}: {ExtId} → {Path}", browser.Name, extId, prefsPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inject extension into profile for {Name}", browser.Name);
            return false;
        }
    }

    /// <summary>
    /// Fill in ConfigDir/DefaultProfileDir when the scan hadn't resolved them yet
    /// (stale scan or direct install call). Reuses the same profile-shape probe.
    /// </summary>
    private static void EnsureProfilePaths(DetectedBrowser browser)
    {
        if (!string.IsNullOrEmpty(browser.DefaultProfileDir)) return;

        var candidate = new BrowserCandidate
        {
            Name = browser.Name,
            BinaryName = browser.BinaryName,
            DesktopId = browser.DesktopId,
            BinaryPath = browser.BinaryPath,
            FlatpakAppId = browser.FlatpakAppId,
        };
        var profileRoot = BrowserEngineDetector.FindProfileRoot(candidate);
        if (profileRoot == null) return;

        browser.ConfigDir = profileRoot.Value.Root;
        if (!string.IsNullOrEmpty(profileRoot.Value.DefaultProfile))
            browser.DefaultProfileDir = Path.Combine(profileRoot.Value.Root, profileRoot.Value.DefaultProfile);
    }

    /// <summary>
    /// Windows native-messaging hosts are registered in HKCU per vendor
    /// (RegisterNativeHostWindows). Returns true if any known vendor key exists.
    /// </summary>
    private static bool IsWindowsNativeHostRegistered()
    {
        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser;
            foreach (var root in new[]
            {
                @"Software\Google\Chrome\NativeMessagingHosts",
                @"Software\Microsoft\Edge\NativeMessagingHosts",
                @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
                @"Software\Vivaldi\NativeMessagingHosts",
                @"Software\Opera Software\Opera Stable\NativeMessagingHosts",
            })
            {
                using var key = hkcu.OpenSubKey(Path.Combine(root, "com.alphai.tracker"));
                if (key != null) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Build the launch command for a browser. Flatpak browsers must go through
    /// `flatpak run &lt;appid&gt;` — bare launching of the flatpak shim would ignore args.
    /// </summary>
    private static (string fileName, string arguments) BuildLaunch(DetectedBrowser browser, string args)
    {
        if (!string.IsNullOrEmpty(browser.FlatpakAppId))
        {
            var flatpakArgs = $"run {browser.FlatpakAppId}".Trim();
            if (!string.IsNullOrWhiteSpace(args))
                flatpakArgs += " " + args;
            return ("flatpak", flatpakArgs);
        }
        return (browser.BinaryPath, args);
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
    /// Install native messaging host manifests. Phase 1: calls the pure C#
    /// <see cref="InstallNativeHostManuallyAsync"/> directly — the
    /// install-extensions.sh shell-out path is dropped. The script itself stays
    /// in the bundle for parity/rollback until Phase 6 removes it.
    /// </summary>
    public async Task<bool> InstallNativeHostAsync(CancellationToken ct)
    {
        try
        {
            return await InstallNativeHostManuallyAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install native host");
            return false;
        }
    }

    // ─── Private helpers ───

    // (Phase 2) The former AddChromeBrowserAsync/AddFirefoxAsync/FindBinary
    // hardcoded-brand scan flow was replaced by BrowserDetector + BrowserEngineDetector.

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
            var extId = ExtensionIdCalculator.Compute(extensionDir);
            if (extId == "unknown")
            {
                _logger.LogWarning(
                    "Could not compute extension ID for {Path} — manifest allowed_origins will be empty",
                    extensionDir);
            }

            var written = new List<string>();

            // ─── Phase 3: per-browser manifests. Every detected browser gets a
            // manifest at its own path with the engine-appropriate allowed key
            // (allowed_origins for Chromium, allowed_extensions for Firefox/Gecko).
            // Always overwrite so newly detected browsers get manifests immediately.
            foreach (var browser in _detected.ToList())
            {
                foreach (var (targetPath, isGecko) in GetManifestTargetsFor(browser))
                {
                    await WriteNativeHostManifestAsync(targetPath, nativeHostPy, extId, isGecko, ct);
                    written.Add(targetPath);
                }
            }

            // Legacy fallback: no browsers detected (or scan not run yet) — keep the
            // two known dirs in sync so a single-browser setup still works.
            if (written.Count == 0)
            {
                await WriteNativeHostManifestAsync(
                    Path.Combine(_chromeNativeHostDir, "com.alphai.tracker.json"),
                    nativeHostPy, extId, isGecko: false, ct);
                await WriteNativeHostManifestAsync(
                    Path.Combine(_firefoxNativeHostDir, "com.alphai.tracker.json"),
                    nativeHostPy, extId, isGecko: true, ct);
                written.Add(_chromeNativeHostDir);
                written.Add(_firefoxNativeHostDir);
            }

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
                    await File.WriteAllTextAsync(sideBySideManifest,
                        BuildManifestJson(nativeHostPy, extId, isGecko: false), ct);
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
                "Native host manifests created for {Count} browsers (host: {Path}, extension ID: {ExtId})",
                _detected.Count, nativeHostPy, extId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create native host manifests manually");
            return false;
        }
    }

    /// <summary>
    /// Manifest targets for a browser. Firefox/Gecko reads ~/.mozilla/native-messaging-hosts
    /// (plus the snap-visible copy when the profile lives under ~/snap/...); Chromium reads
    /// the per-browser NativeMessagingHosts dir derived in ScanAsync (browser.ManifestPath).
    /// </summary>
    private List<(string Path, bool IsGecko)> GetManifestTargetsFor(DetectedBrowser browser)
    {
        var targets = new List<(string, bool)>();
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (browser.Engine == BrowserEngine.Gecko)
        {
            targets.Add((Path.Combine(_firefoxNativeHostDir, "com.alphai.tracker.json"), true));

            // Snap-packaged Firefox keeps its profile under ~/snap/... and needs the
            // manifest in the snap-visible .mozilla dir too.
            if (!string.IsNullOrEmpty(browser.ConfigDir) &&
                browser.ConfigDir.Contains($"{Path.DirectorySeparatorChar}snap{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                var mozillaDir = Directory.GetParent(browser.ConfigDir)?.FullName;
                if (!string.IsNullOrEmpty(mozillaDir))
                    targets.Add((Path.Combine(mozillaDir, "native-messaging-hosts", "com.alphai.tracker.json"), true));
            }
        }
        else if (!string.IsNullOrEmpty(browser.ManifestPath))
        {
            targets.Add((browser.ManifestPath, false));
        }
        else
        {
            targets.Add((Path.Combine(_chromeNativeHostDir, "com.alphai.tracker.json"), false));
        }

        return targets;
    }

    /// <summary>Serialize and write one native-messaging host manifest.</summary>
    private static async Task WriteNativeHostManifestAsync(
        string targetPath, string nativeHostPy, string extId, bool isGecko, CancellationToken ct)
    {
        var json = BuildManifestJson(nativeHostPy, extId, isGecko);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? string.Empty);
        await File.WriteAllTextAsync(targetPath, json, ct);
    }

    /// <summary>Build the manifest JSON for a specific engine family.</summary>
    private static string BuildManifestJson(string nativeHostPy, string extId, bool isGecko)
    {
        var manifest = new
        {
            name = "com.alphai.tracker",
            description = "Alpha AI Tracker — Native messaging bridge for browser tab/URL capture",
            path = nativeHostPy,
            type = "stdio",
            allowed_origins = isGecko
                ? Array.Empty<string>()
                : (extId == "unknown"
                    ? Array.Empty<string>()
                    : new[] { $"chrome-extension://{extId}/" }),
            allowed_extensions = isGecko && extId != "unknown"
                ? new[] { NativeMessagingPaths.GeckoApplicationId }
                : Array.Empty<string>(),
        };
        return System.Text.Json.JsonSerializer.Serialize(manifest,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
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

    /// <summary>
    /// Kill orphaned native messaging host processes left over from a previous
    /// crashed session.
    ///
    /// Phase 1: the host is now THIS exe in host mode (spawned by the browser),
    /// so matching by exe name would kill the running tracker itself. Match by
    /// COMMAND LINE instead — only processes whose argv contains a host-mode
    /// marker (--native-host, chrome-extension://&lt;id&gt;/, or the bare Gecko
    /// application id) are orphaned host processes.
    /// </summary>
    private static void KillOrphanedNativeHostProcesses()
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "pkill",
                    Arguments = "-f \"(--native-host|chrome-extension://|alpha-ai-tracker@alphai.com)\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                proc?.WaitForExit(2000);
            }
            else if (OperatingSystem.IsWindows())
            {
                // PowerShell: match processes by CommandLine (not image name) and
                // never kill the current tracker process itself.
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -Command \"Get-CimInstance Win32_Process | " +
                                "Where-Object { $_.CommandLine -match '(--native-host|chrome-extension://|alpha-ai-tracker@alphai.com)' " +
                                "-and $_.ProcessId -ne $PID } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }\"",
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

    /// <summary>Engine family classified from profile shape (never from the name).</summary>
    public BrowserEngine Engine { get; set; } = BrowserEngine.Unknown;

    /// <summary>True when the OS reports this browser as the system default.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Stable OS identity: .desktop basename / registry subkey / bundle id.</summary>
    public string DesktopId { get; set; } = string.Empty;

    /// <summary>Icon hint from the .desktop file (Linux).</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>Resolved user-data/config root for this browser (from profile shape).</summary>
    public string ConfigDir { get; set; } = string.Empty;

    /// <summary>Full path to the default profile directory (Chromium "Default", Gecko Default=1 profile).</summary>
    public string DefaultProfileDir { get; set; } = string.Empty;

    /// <summary>Per-browser native-messaging host manifest path.</summary>
    public string ManifestPath { get; set; } = string.Empty;

    /// <summary>Enterprise policy dir (Chromium /etc/opt/&lt;browser&gt;/policies/managed, Firefox /etc/firefox/policies). Phase 5.</summary>
    public string PolicyDir { get; set; } = string.Empty;

    /// <summary>Set when the browser runs through flatpak (launch via `flatpak run &lt;appid&gt;`).</summary>
    public string? FlatpakAppId { get; set; }

    public string StatusText => Status switch
    {
        BrowserInstallStatus.ReadyToInstall => "Ready",
        BrowserInstallStatus.NativeHostReady => "Host Ready",
        BrowserInstallStatus.Loading => "Connecting…",
        BrowserInstallStatus.ExtensionActive => "✅ Active",
        _ => "Not detected",
    };

    /// <summary>Engine family label (from profile shape, display only).</summary>
    public string EngineText => Engine switch
    {
        BrowserEngine.Chromium => "Chromium",
        BrowserEngine.Gecko => "Gecko",
        _ => "Engine unknown",
    };

    /// <summary>Combined status + engine line shown in the browser card.</summary>
    public string StatusLine =>
        $"{StatusText} · {EngineText}{(IsDefault ? " · System default" : "")}";

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
