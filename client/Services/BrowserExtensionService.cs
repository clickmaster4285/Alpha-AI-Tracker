using System.Diagnostics;
using System.Text.Json;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;
using Microsoft.Extensions.Logging;

namespace client.Services;

/// <summary>
/// Detects installed browsers (OS http/https handlers × installed_applications.is_browser),
/// manages engine-based extension auto-install (chromium / gecko; webkit unsupported),
/// and checks extension connectivity via NativeMessageService heartbeat.
///
/// Native messaging host is the tracker exe itself in --native-host / chrome-extension://
/// / gecko-id mode (pure C# — no Python).
///
/// Chromium: --load-extension → profile Preferences injection → optional policy forcelist.
/// Gecko: native host + launch; unsigned XPI still needs a one-time temporary add-on load
///   on first install (browser security policy).
/// WebKit: listed as NotSupported — no WebExtensions native-messaging bridge.
/// </summary>
public class BrowserExtensionService
{
    private readonly ILogger<BrowserExtensionService> _logger;
    private readonly NativeMessageService _nativeMessageService;
    private readonly IInstalledAppDetector _appDetector;
    private readonly AppConfig _config;
    private readonly string _chromiumExtDir;
    private readonly string _geckoExtDir;
    private readonly string _socketPath;
    private readonly string _fallbackChromiumNativeHostDir;
    private readonly string _geckoNativeHostDir;
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

    public BrowserExtensionService(
        ILogger<BrowserExtensionService> logger,
        NativeMessageService nativeMessageService,
        IInstalledAppDetector appDetector,
        AppConfig config)
    {
        _logger = logger;
        _nativeMessageService = nativeMessageService;
        _appDetector = appDetector;
        _config = config;
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _extensionsRoot = ResolveExtensionsRoot();
        _chromiumExtDir = Path.Combine(_extensionsRoot, "chromium");
        // Dev/legacy fallback: older trees shipped as extensions/chrome/
        if (!Directory.Exists(_chromiumExtDir))
            _chromiumExtDir = Path.Combine(_extensionsRoot, "chrome");
        _geckoExtDir = Path.Combine(_extensionsRoot, "gecko");
        if (!Directory.Exists(_geckoExtDir))
            _geckoExtDir = Path.Combine(_extensionsRoot, "firefox");

        _socketPath = NativeMessagingPaths.SocketPath;
        _fallbackChromiumNativeHostDir = Path.Combine(userHome, ".config", "chromium", "NativeMessagingHosts");
        _geckoNativeHostDir = Path.Combine(userHome, ".mozilla", "native-messaging-hosts");

        _heartbeatTimer = new System.Threading.Timer(_ => PollHeartbeat(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

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
    /// Inclusion: OS-level http/https handler registration (BrowserDetector), enriched
    /// with installed_applications rows where is_browser=1 (display name + confirmation).
    /// Engine: profile-shape classification (Chromium / Gecko / WebKit / Unknown) — never
    /// from the binary brand name.
    /// </summary>
    public async Task ScanAsync(CancellationToken ct)
    {
        var results = new List<DetectedBrowser>();
        var resolvedBinaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Catalog browsers: IsBrowser apps from the live OS detector (same source that
        // feeds installed_applications). Used for display names + to surface browsers
        // that registered as WebBrowser but weren't caught as http handlers yet.
        var catalogBrowsers = _appDetector.GetAllInstalledApplications()
            .Where(a => a.IsBrowser)
            .ToList();

        var candidates = BrowserDetector.DetectAll();

        // Merge catalog-only browsers that BrowserDetector missed (still need a path).
        foreach (var app in catalogBrowsers)
        {
            if (string.IsNullOrWhiteSpace(app.InstallPath) && string.IsNullOrWhiteSpace(app.BinaryName))
                continue;
            var already = candidates.Any(c =>
                (!string.IsNullOrEmpty(app.BinaryName) &&
                 string.Equals(c.BinaryName, app.BinaryName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(app.DesktopId) &&
                 string.Equals(c.DesktopId, app.DesktopId, StringComparison.OrdinalIgnoreCase)));
            if (already) continue;

            candidates.Add(new BrowserCandidate
            {
                Name = app.AppName,
                BinaryPath = app.InstallPath ?? string.Empty,
                BinaryName = app.BinaryName ?? string.Empty,
                DesktopId = app.DesktopId ?? string.Empty,
                Icon = string.Empty,
            });
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.BinaryPath) &&
                string.IsNullOrWhiteSpace(candidate.FlatpakAppId))
                continue;

            var resolved = ResolveBinaryPath(candidate.BinaryPath);
            if (!resolvedBinaries.Add(resolved)) continue;

            var engine = BrowserEngineDetector.DetectFor(candidate);
            var displayName = ResolveDisplayName(candidate, catalogBrowsers);

            var nativeHostManifest = GetNativeHostManifestPath(candidate, engine);
            var nativeHostInstalled = engine is BrowserEngine.WebKit or BrowserEngine.Unknown
                ? false
                : OperatingSystem.IsWindows()
                    ? IsWindowsNativeHostRegistered(candidate)
                    : File.Exists(nativeHostManifest);

            var status = engine switch
            {
                BrowserEngine.WebKit => BrowserInstallStatus.NotSupported,
                BrowserEngine.Unknown => BrowserInstallStatus.NotSupported,
                _ when nativeHostInstalled => BrowserInstallStatus.NativeHostReady,
                _ => BrowserInstallStatus.ReadyToInstall,
            };

            if (status != BrowserInstallStatus.NotSupported && await IsExtensionActiveAsync(ct))
                status = BrowserInstallStatus.ExtensionActive;

            var isChromium = engine == BrowserEngine.Chromium;
            var profileRoot = BrowserEngineDetector.FindProfileRoot(candidate);
            results.Add(new DetectedBrowser
            {
                Name = displayName,
                BinaryPath = resolved,
                BinaryName = candidate.BinaryName,
                ExtensionDir = ResolveExtensionDirForEngine(engine),
                Status = status,
                IsChromeBased = isChromium,
                BrowserType = engine == BrowserEngine.Gecko ? BrowserType.Gecko : BrowserType.Chromium,
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
                PolicyDir = isChromium
                    ? GetChromiumPolicyDir(new DetectedBrowser
                    {
                        BinaryName = candidate.BinaryName,
                        DesktopId = candidate.DesktopId,
                        ConfigDir = profileRoot?.Root ?? string.Empty,
                    })
                    : null,
            });
        }

        _detected = results;
        _logger.LogInformation("Browser scan: {Count} detected, {Active} active — engines: {Engines}",
            results.Count, results.Count(b => b.Status == BrowserInstallStatus.ExtensionActive),
            string.Join(", ", results.Select(b => $"{b.Name}={b.Engine}{(b.IsDefault ? "*" : "")}")));
    }

    /// <summary>
    /// Prefer installed_applications.app_name (is_browser) over the .desktop/registry
    /// Name= — never a hardcoded brand table.
    /// </summary>
    private static string ResolveDisplayName(BrowserCandidate candidate, List<InstalledApplication> catalog)
    {
        foreach (var app in catalog)
        {
            if (!string.IsNullOrEmpty(candidate.BinaryName) &&
                string.Equals(app.BinaryName, candidate.BinaryName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(app.AppName))
                return app.AppName;
            if (!string.IsNullOrEmpty(candidate.DesktopId) &&
                string.Equals(app.DesktopId, candidate.DesktopId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(app.AppName))
                return app.AppName;
        }
        return candidate.Name;
    }

    private string ResolveExtensionDirForEngine(BrowserEngine engine) => engine switch
    {
        BrowserEngine.Gecko => _geckoExtDir,
        BrowserEngine.Chromium => _chromiumExtDir,
        _ => string.Empty,
    };

    /// <summary>
    /// Per-browser native-messaging manifest path, derived from the ACTUAL
    /// profile root (via BrowserEngineDetector.FindProfileRoot). Firefox (Gecko)
    /// always reads ~/.mozilla/native-messaging-hosts; Chromium browsers read
    /// ~/.config/&lt;probed-root-basename&gt;/NativeMessagingHosts (Linux) or
    /// ~/Library/Application Support/&lt;probed-root-basename&gt;/NativeMessagingHosts (macOS).
    /// Windows is registry-based (RegisterNativeHostWindows) and not covered here.
    ///
    /// By using FindProfileRoot we guarantee the manifest lands in the same
    /// directory whose profile file (Preferences / profiles.ini) is actually
    /// present — no guessed dictionary hint.
    /// </summary>
    private static string GetNativeHostManifestPath(BrowserCandidate candidate, BrowserEngine engine)
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var manifestName = "com.alphai.tracker.json";

        if (engine == BrowserEngine.Gecko)
            return Path.Combine(userHome, ".mozilla", "native-messaging-hosts", manifestName);

        // Chromium (and Unknown): resolve the manifest path from the ACTUAL
        // probed profile root, not from a guessed name.
        var profileRoot = BrowserEngineDetector.FindProfileRoot(candidate);
        if (profileRoot != null)
        {
            var rootDir = profileRoot.Value.Root;
            if (!string.IsNullOrEmpty(rootDir))
            {
                if (OperatingSystem.IsMacOS())
                {
                    return Path.Combine(userHome, "Library", "Application Support",
                        Path.GetFileName(rootDir),
                        "NativeMessagingHosts", manifestName);
                }
                return Path.Combine(rootDir, "NativeMessagingHosts", manifestName);
            }
        }

        // Fallback (no profile yet): use generic name derivation.
        var configDir = GetConfigDirName(candidate);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(userHome, "Library", "Application Support", configDir,
                "NativeMessagingHosts", manifestName);
        return Path.Combine(userHome, ".config", configDir, "NativeMessagingHosts", manifestName);
    }

    /// <summary>
    /// Generic config-directory name for a browser candidate, derived entirely
    /// from BinaryName/DesktopId with no brand-keyed dictionary. Mirrors the
    /// multi-candidate probe in BrowserEngineDetector.DeriveRoots.
    /// </summary>
    private static string GetConfigDirName(BrowserCandidate candidate)
    {
        // Use DesktopId if set and different from BinaryName (desktop files
        // sometimes have the more specific name, e.g. BraveSoftware/Brave-Browser
        // vs the binary name "brave-browser").
        if (!string.IsNullOrEmpty(candidate.DesktopId) &&
            !string.Equals(candidate.DesktopId, candidate.BinaryName, StringComparison.OrdinalIgnoreCase))
            return candidate.DesktopId;
        if (!string.IsNullOrEmpty(candidate.BinaryName))
            return candidate.BinaryName;
        return "unknown";
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
    /// Engine-based attach entry point (no brand names). Prefer this from the UI.
    /// <list type="number">
    /// <item>Chromium + CRX configured → elevated ExtensionInstallForcelist (persistent).</item>
    /// <item>Else Chromium → --load-extension (re-enabled via DisableLoadExtension… flag)
    ///       then Preferences injection.</item>
    /// <item>Gecko → launch + temporary-addon path (signed XPI required for permanent).</item>
    /// <item>WebKit / Unknown → fail safe (no WebExtensions NM bridge / no profile shape).</item>
    /// </list>
    /// Status may be <see cref="BrowserInstallStatus.Loading"/> while the UI shows progress —
    /// that must not block attach (previous CanInstall gate was a bug).
    /// </summary>
    public async Task<BrowserInstallResult> AttachExtensionAsync(DetectedBrowser browser)
    {
        if (!browser.MayAttach)
            return new BrowserInstallResult(false, false, "Browser is not in a state that can be installed.");

        if (browser.Engine is BrowserEngine.Unknown or BrowserEngine.WebKit)
        {
            return new BrowserInstallResult(false, false,
                browser.Engine == BrowserEngine.WebKit
                    ? "WebKit has no WebExtensions native-messaging bridge."
                    : "Engine not auto-detected. Launch the browser once, then Refresh.");
        }

        if (browser.Engine == BrowserEngine.Chromium &&
            !string.IsNullOrWhiteSpace(_config.CrxExtensionId) &&
            !string.IsNullOrWhiteSpace(_config.ServerUrl))
        {
            var policy = await InstallPolicyForcelistAsync(browser);
            if (policy.Success)
                return policy;

            _logger.LogInformation(
                "Policy attach for {Name} did not succeed ({Msg}) — falling through to launch ladder",
                browser.Name, policy.Message);
        }

        return await InstallExtensionAsync(browser);
    }

    /// <summary>
    /// Launch/inject the unpacked engine pack. Used as the no-CRX path and as
    /// fallback when policy write is unavailable (e.g. Windows without CRX, elevation declined).
    /// Chromium: kill → --load-extension → Preferences injection.
    /// Gecko: launch (temporary add-on / signed XPI for permanent).
    /// </summary>
    public async Task<BrowserInstallResult> InstallExtensionAsync(DetectedBrowser browser)
    {
        if (!browser.MayAttach)
            return new BrowserInstallResult(false, false, "Browser is not in a state that can be installed.");

        // Engine=Unknown: don't guess which ladder to run.
        if (browser.Engine == BrowserEngine.Unknown)
        {
            _logger.LogInformation(
                "{Name} has unknown engine — skipping auto-attach, showing manual instructions",
                browser.Name);
            return new BrowserInstallResult(false, false,
                "Engine not auto-detected. Follow the manual steps shown in the extension card.");
        }

        // Re-resolve the extension dir at install time so we never launch pointing
        // at a stale path when the user is running the installed binary.
        if (browser.Engine == BrowserEngine.Chromium)
        {
            var resolvedDir = ResolveEngineExtensionDir(BrowserEngine.Chromium);
            if (Directory.Exists(resolvedDir))
                browser.ExtensionDir = resolvedDir;
        }
        else if (browser.Engine == BrowserEngine.Gecko)
        {
            var resolvedDir = ResolveEngineExtensionDir(BrowserEngine.Gecko);
            if (Directory.Exists(resolvedDir))
                browser.ExtensionDir = resolvedDir;
        }

        try
        {
            var alreadyRunning = IsBrowserProcessRunning(browser);

            if (alreadyRunning)
            {
                _logger.LogInformation("{Name} is running — closing first", browser.Name);
                KillBrowserProcesses(browser);
                await WaitForBrowserExitAsync(browser);
            }

            if (browser.Engine == BrowserEngine.Chromium)
            {
                // 1) Stage CRX/unpacked + shortcut flags + best-effort registry/policy.
                //    Never forge Secure Preferences / developer_mode here.
                var persist = ChromiumPersistentInstaller.Install(
                    browser, browser.ExtensionDir, _logger);
                _logger.LogInformation(
                    "Chromium stage for {Name}: external={Ext} policy={Pol} shortcuts={S} id={Id} — {Msg}",
                    browser.Name, persist.ExternalRegistered, persist.PolicyRegistered,
                    persist.ShortcutsPatched, persist.ExtensionId, persist.Message);

                var stagedDir = Directory.Exists(ChromiumPersistentInstaller.StagedExtDir)
                    ? ChromiumPersistentInstaller.StagedExtDir
                    : browser.ExtensionDir;
                browser.ExtensionDir = stagedDir;

                EnsureProfilePaths(browser);
                var userData = browser.ConfigDir;
                if (string.IsNullOrEmpty(userData) || !Directory.Exists(userData))
                {
                    return new BrowserInstallResult(false, false,
                        "Browser user-data directory not resolved — cannot seed profiles.");
                }

                // 2) Idempotent seed: re-read info_cache; --load-extension only for
                //    profiles missing the extension. Already-seeded profiles are skipped
                //    so newly created profiles attach on the next Install / Setup All.
                async Task KillWait()
                {
                    KillBrowserProcesses(browser);
                    await WaitForBrowserExitAsync(browser, clearLocks: true);
                }

                var seed = await ChromiumProfileSeeder.SeedAllProfilesAsync(
                    browser.BinaryPath,
                    browser.FlatpakAppId,
                    userData,
                    stagedDir,
                    KillWait,
                    _logger);

                // 3) Backup propagate — Preferences only (never forge Secure Preferences).
                await KillWait();
                var propagated = ChromiumProfilePropagator.PropagateFromSeed(
                    userData, stagedDir, _logger);

                // 4) Native-host allowed_origins: path-id + key-id + whatever seed found.
                var originIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(persist.ExtensionId)) originIds.Add(persist.ExtensionId);
                if (!string.IsNullOrEmpty(seed.FirstExtensionId)) originIds.Add(seed.FirstExtensionId!);
                if (propagated != null) originIds.Add(propagated.ExtensionId);
                originIds.Add(ExtensionIdCalculator.Compute(Path.GetFullPath(stagedDir)));
                await RewriteChromiumManifestsWithExtensionIdsAsync(originIds);
                EnsureChromiumNativeHostRegistry(browser);

                var profileSummary = string.Join("; ",
                    seed.Outcomes.Select(o => $"{o.Profile}:{o.Status}"));

                // Require at least one profile seeded or already attached — never succeed
                // on shortcut patch / process start alone (false "Connected" mask).
                var profilesOk = seed.ProfilesSeeded + seed.ProfilesSkipped > 0;
                if (!profilesOk)
                {
                    return new BrowserInstallResult(false, false,
                        "Could not seed any Chromium profile with --load-extension. " +
                        $"Outcomes: [{profileSummary}]. {persist.Message} " +
                        "Check logs for bucket=seed_timeout|launch|staged_path|singleton.");
                }

                // 5) Relaunch the user's last-used profile WITH --load-extension so the
                //    window they see immediately has the extension + developer mode.
                var profileArg = string.IsNullOrEmpty(seed.LastUsedProfile)
                    ? ""
                    : $"--profile-directory=\"{seed.LastUsedProfile}\" ";
                var (launchFile, launchArgs) = BuildLaunch(
                    browser,
                    $"--user-data-dir=\"{userData}\" {profileArg}" +
                    $"--load-extension=\"{Path.GetFullPath(stagedDir)}\" " +
                    "--disable-features=DisableLoadExtensionCommandLineSwitch --no-first-run --disable-gcm");
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = launchFile,
                    Arguments = launchArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                var msg =
                    $"Seeded {seed.ProfilesSeeded}, skipped {seed.ProfilesSkipped} already attached" +
                    (seed.ProfilesFailed > 0 ? $", failed {seed.ProfilesFailed}" : "") +
                    (string.IsNullOrEmpty(seed.LastUsedProfile) ? "" : $" (reopened {seed.LastUsedProfile})") +
                    $". Propagated to {propagated?.ProfilesUpdated ?? 0} others. " +
                    $"Profiles: [{profileSummary}]. " +
                    $"IDs: {string.Join(", ", originIds)}. {persist.Message}" +
                    " New browser profiles created later need Install / Setup All again.";

                if (proc == null)
                    _logger.LogWarning("Final relaunch of {Name} returned null process", browser.Name);

                return new BrowserInstallResult(true, alreadyRunning, msg);
            }
            else if (browser.Engine == BrowserEngine.Gecko)
            {
                // Gecko — launch with temporary addon when possible; otherwise normal launch.
                var manifest = Path.Combine(browser.ExtensionDir, "manifest.json");
                var geckoArgs = File.Exists(manifest)
                    ? $"\"{manifest}\""
                    : string.Empty;
                // Firefox ignores random args; just launch so the user can load Temporary Add-on.
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
                    return new BrowserInstallResult(true, alreadyRunning,
                        "Gecko launched. If the extension is not yet connected, open about:debugging → This Firefox → Load Temporary Add-on and select the gecko extension manifest.");
                }
            }
            else
            {
                return new BrowserInstallResult(false, false,
                    $"{browser.Engine} engine does not support WebExtensions native messaging.");
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
    /// PHASE 5 (rev-3 default): install the extension via the browser's
    /// enterprise policy — ExtensionInstallForcelist pointing at the self-hosted
    /// signed CRX + update manifest (sub-phase 5A). This is THE default
    /// persistent mechanism for Chromium-engine browsers; the no-elevation
    /// Preferences-injection and --load-extension paths are dev fallbacks only
    /// (proven rejected by branded Chrome 137+/150 Secure Preferences MAC
    /// validation).
    ///
    /// Per-click flow (one elevation prompt, inserted at the front — the
    /// employee is actively engaged, this is not a silent background op):
    ///   1. Elevate once (pkexec on Linux / runas on Windows) and write the
    ///      managed-policy JSON (ExtensionInstallForcelist).
    ///   2. Rewrite the native-messaging manifest so its allowed_origins uses the
    ///      CRX-derived extension ID (the ID Chrome assigns for policy installs).
    ///   3. Kill + relaunch that browser (existing machinery) — policy picks up
    ///      the CRX on launch.
    ///   4. Heartbeat flips to "✅ Active" via the existing poller.
    ///
    /// Engine != Chromium → fail safe (policy forcelist is Chromium-only).
    /// </summary>
    public async Task<BrowserInstallResult> InstallPolicyForcelistAsync(DetectedBrowser browser)
    {
        if (!browser.MayAttach)
            return new BrowserInstallResult(false, false, "Browser is not in a state that can be installed.");
        if (browser.Engine != BrowserEngine.Chromium)
            return new BrowserInstallResult(false, false,
                "Policy force-install is Chromium-only; this browser uses the Gecko path.");
        if (string.IsNullOrWhiteSpace(_config.CrxExtensionId))
            return new BrowserInstallResult(false, false,
                "CRX extension ID not configured (ALPHA_CRX_EXTENSION_ID). " +
                "Run server/cmd/crxsign and bake the printed ID into the client env.");
        if (string.IsNullOrWhiteSpace(_config.ServerUrl))
            return new BrowserInstallResult(false, false,
                "Server URL not configured (ALPHA_SERVER_URL) — cannot point policy at the update manifest.");

        // Windows Chromium policy is HKLM registry (not /etc file paths). When CRX
        // is configured we attempt registry forcelist; otherwise AttachExtensionAsync
        // never reaches here. Failure returns soft so the launch ladder can run.
        if (OperatingSystem.IsWindows())
            return await TryWindowsRegistryForcelistAsync(browser);

        var policyDir = GetChromiumPolicyDir(browser);
        if (string.IsNullOrEmpty(policyDir))
            return new BrowserInstallResult(false, false,
                $"No managed-policy directory known for {browser.Name} on this platform.");

        var updateUrl = $"{_config.ServerUrl.TrimEnd('/')}/api/v1/extensions/{_config.CrxExtensionId}/update.xml";
        var policyValue = $"{_config.CrxExtensionId};{updateUrl}";
        var policyJson = JsonSerializer.Serialize(new
        {
            ExtensionInstallForcelist = new[] { policyValue },
        }, new JsonSerializerOptions { WriteIndented = true });

        _logger.LogInformation(
            "[{Name}] policy force-install: {Value} → {Dir}",
            browser.Name, policyValue, policyDir);

        try
        {
            var alreadyRunning = IsBrowserProcessRunning(browser);

            // Step 1: elevated policy write (pkexec on Linux, runas on Windows).
            var wrote = await WriteManagedPolicyAsync(policyDir, "alpha-ai-tracker.json", policyJson);
            if (!wrote)
            {
                _logger.LogWarning("Policy write failed for {Name} — elevation denied or script error", browser.Name);
                return new BrowserInstallResult(false, false,
                    "Could not write the managed policy — the elevation prompt was declined or failed.\n\n" +
                    "Write it manually as root, then click this button again (or restart the browser):\n\n" +
                    $"  sudo mkdir -p '{policyDir}'\n" +
                    $"  sudo tee '{policyDir}/alpha-ai-tracker.json' > /dev/null <<'AAT_EOF'\n" +
                    policyJson + "\n" +
                    "AAT_EOF");
            }

            // Step 2: rewrite the native-messaging manifest with the CRX id in
            // allowed_origins (Chrome assigns the key-derived id for CRX installs).
            await RewriteManifestsWithCrxIdAsync();

            // Step 3: kill + relaunch so the policy is picked up.
            if (alreadyRunning)
            {
                _logger.LogInformation("{Name} is running — closing first for policy pickup", browser.Name);
                KillBrowserProcesses(browser);
                await WaitForBrowserExitAsync(browser);
            }

            var (launchFile, launchArgs) = BuildLaunch(browser, "--disable-gcm");
            var psi = new ProcessStartInfo
            {
                FileName = launchFile,
                Arguments = launchArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var proc = Process.Start(psi);
            if (proc != null)
            {
                _logger.LogInformation(
                    "Launched {Name} with policy force-install (Phase 5): PID {Pid}",
                    browser.Name, proc.Id);
                return new BrowserInstallResult(true, alreadyRunning,
                    "Policy written; browser relaunched. Heartbeat flips to Active within ~30s.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Policy force-install failed for {Name}", browser.Name);
            return new BrowserInstallResult(false, false, $"Error: {ex.Message}");
        }

        return new BrowserInstallResult(false, false, "Could not start the browser process.");
    }

    /// <summary>
    /// Rewrite Chromium native-messaging manifests so allowed_origins includes
    /// every candidate extension ID (path-derived and/or key-derived).
    /// </summary>
    private async Task RewriteChromiumManifestsWithExtensionIdsAsync(IEnumerable<string> extensionIds)
    {
        var ids = extensionIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && id != "unknown")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0) return;

        var hostPath = ResolveNativeHostBinaryPath();
        foreach (var browser in _detected.ToList())
        {
            if (browser.Engine != BrowserEngine.Chromium) continue;
            foreach (var (targetPath, isGecko) in GetManifestTargetsFor(browser))
            {
                if (isGecko) continue;
                await WriteNativeHostManifestAsync(
                    targetPath, hostPath, ids, isGecko: false, CancellationToken.None);
            }
        }

        // Side-by-side + Windows registry host registration.
        try
        {
            var sideBySide = Path.Combine(
                Path.GetDirectoryName(hostPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                "com.alphai.tracker.json");
            await WriteNativeHostManifestAsync(sideBySide, hostPath, ids, isGecko: false, CancellationToken.None);
            RegisterNativeHostWindows(sideBySide);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Side-by-side native-host rewrite failed");
        }
    }

    private async Task RewriteChromiumManifestsWithExtensionIdAsync(string extensionId) =>
        await RewriteChromiumManifestsWithExtensionIdsAsync(new[] { extensionId });

    /// <summary>
    /// Rewrite every Chromium native-messaging manifest so allowed_origins uses
    /// the CRX-derived extension ID (the ID Chrome assigns to a policy-installed
    /// CRX), not the path-derived ID used by the dev/unpacked flow. Gecko
    /// manifests keep allowed_extensions (browser-id based, unchanged).
    /// </summary>
    private async Task RewriteManifestsWithCrxIdAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.CrxExtensionId)) return;
        await RewriteChromiumManifestsWithExtensionIdAsync(_config.CrxExtensionId!);
    }

    /// <summary>
    /// Resolve the managed-policy directory for a Chromium browser.
    /// Linux: /etc/opt/&lt;vendor&gt;/policies/managed (Debian-style) or
    /// /etc/chromium/policies/managed; Windows: HKLM …\Software\Policies
    /// (registry write, Phase-5 code-review-only); macOS: /Library/Managed
    /// Preferences/&lt;bundle-id&gt; (code-review-only — no CLI elevation API).
    /// This is a structural per-vendor path map (the OS defines the layout), not
    /// a detection gate.
    /// </summary>
    /// <summary>
    /// Derive a Chromium managed-policy directory from the binary/config name —
    /// probe common /etc layouts without a brand dictionary. First existing
    /// parent wins; otherwise returns the conventional /etc/opt/&lt;name&gt;/policies/managed.
    /// </summary>
    public static string? GetChromiumPolicyDir(DetectedBrowser browser)
    {
        if (!OperatingSystem.IsLinux())
        {
            if (OperatingSystem.IsWindows()) return null; // registry-based; not file paths
            if (OperatingSystem.IsMacOS()) return "/Library/Managed Preferences";
            return null;
        }

        var names = new List<string>();
        if (!string.IsNullOrEmpty(browser.BinaryName)) names.Add(browser.BinaryName);
        if (!string.IsNullOrEmpty(browser.DesktopId)) names.Add(browser.DesktopId);
        if (!string.IsNullOrEmpty(browser.ConfigDir))
            names.Add(Path.GetFileName(browser.ConfigDir.TrimEnd(Path.DirectorySeparatorChar)));

        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (var candidate in new[]
                     {
                         $"/etc/{name}/policies/managed",
                         $"/etc/opt/{name}/policies/managed",
                     })
            {
                var parent = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                    return candidate;
            }
        }

        var fallback = names.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "chromium";
        return $"/etc/opt/{fallback}/policies/managed";
    }

    /// <summary>
    /// Elevated write of a managed-policy JSON file.
    /// Linux: pkexec bash &lt;tmp-script&gt; (the established GrantLinuxPermissionsAsync
    /// pattern). Windows: runas shell-out (Phase 5 — code-review-only). macOS:
    /// manual instructions only.
    /// </summary>
    private async Task<bool> WriteManagedPolicyAsync(string policyDir, string fileName, string json)
    {
        if (OperatingSystem.IsWindows())
        {
            // Code-review-only: Windows policy is registry-based (HKLM).
            _logger.LogWarning("Windows managed-policy write not implemented yet — code-review-only stub");
            return false;
        }
        if (OperatingSystem.IsMacOS())
        {
            _logger.LogWarning("macOS managed-policy write not implemented — manual instructions only");
            return false;
        }

        // Linux: pkexec bash <tmp-script> (one-shot elevated child process; the
        // tracker itself stays non-root).
        var tmpScript = Path.Combine(Path.GetTempPath(), "alpha_policy_" + Guid.NewGuid().ToString("N") + ".sh");
        try
        {
            var script = new System.Text.StringBuilder();
            script.AppendLine("#!/bin/sh");
            script.AppendLine($"mkdir -p '{policyDir}'");
            // Write the JSON via a quoted heredoc so the payload is byte-exact.
            script.AppendLine($"cat > '{policyDir}/{fileName}' <<'AAT_JSON_EOF'");
            script.AppendLine(json);
            script.AppendLine("AAT_JSON_EOF");
            script.AppendLine($"chmod 644 '{policyDir}/{fileName}'");

            await File.WriteAllTextAsync(tmpScript, script.ToString());

            var psi = new ProcessStartInfo
            {
                FileName = "pkexec",
                Arguments = $"bash {tmpScript}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var exited = proc.WaitForExit(120_000); // 2 min for the polkit dialog
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Managed-policy write failed");
            return false;
        }
        finally
        {
            try { if (File.Exists(tmpScript)) File.Delete(tmpScript); } catch { }
        }
    }

    /// <summary>
    /// Try launching with --load-extension. Polls for the C# native host
    /// (tracker exe spawned with chrome-extension://) or socket heartbeat up to 7.5s.
    /// Branded Chrome 137+ disables the switch by default; re-enable via
    /// <c>--disable-features=DisableLoadExtensionCommandLineSwitch</c> (engine-wide,
    /// not brand-specific).
    /// </summary>
    private async Task<bool> TryLaunchWithLoadExtensionAsync(DetectedBrowser browser, bool killOnFailure = true)
    {
        try
        {
            var extDir = Directory.Exists(ChromiumPersistentInstaller.StagedExtDir)
                ? Path.GetFullPath(ChromiumPersistentInstaller.StagedExtDir)
                : Path.GetFullPath(browser.ExtensionDir);
            // DisableLoadExtensionCommandLineSwitch: Chrome 137+ gate. Harmless on
            // Edge/Brave/Chromium builds that never set the feature.
            var args =
                $"--load-extension=\"{extDir}\" " +
                "--disable-features=DisableLoadExtensionCommandLineSwitch " +
                "--no-first-run --disable-gcm";

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

            // Poll for up to 12s — first Chromium launch after kill is slower on Windows.
            for (int i = 0; i < 24; i++)
            {
                await Task.Delay(500);

                if (await IsNativeHostRunningForPidAsync(proc.Id) ||
                    _nativeMessageService.IsExtensionConnected())
                {
                    _logger.LogInformation(
                        "Extension confirmed loaded for {Name} (native-host / heartbeat)", browser.Name);
                    return true;
                }
            }

            _logger.LogInformation(
                "Extension heartbeat not seen for {Name} via --load-extension (prefs may still be seeded).",
                browser.Name);
            if (killOnFailure)
            {
                KillBrowserProcesses(browser);
                await WaitForBrowserExitAsync(browser);
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TryLaunchWithLoadExtension failed");
            return false;
        }
    }

    /// <summary>
    /// Wait until the browser process tree is gone and (Chromium) the profile
    /// SingletonLock is released — otherwise a relaunch joins the old instance
    /// and ignores --load-extension.
    /// </summary>
    private static async Task WaitForBrowserExitAsync(DetectedBrowser browser, bool clearLocks = false)
    {
        // Up to ~30s for process exit (cold Chrome + AV on Windows).
        for (int i = 0; i < 60 && IsBrowserProcessRunning(browser); i++)
            await Task.Delay(500);

        if (IsBrowserProcessRunning(browser))
        {
            // Last resort — force clear locks only after we tried waiting.
            if (clearLocks && !string.IsNullOrEmpty(browser.ConfigDir))
                ChromiumProfileSeeder.ClearSingletonLocks(browser.ConfigDir);
            return;
        }

        var lockPath = string.IsNullOrEmpty(browser.ConfigDir)
            ? null
            : Path.Combine(browser.ConfigDir, "SingletonLock");
        if (string.IsNullOrEmpty(lockPath))
        {
            if (clearLocks && !string.IsNullOrEmpty(browser.ConfigDir))
                ChromiumProfileSeeder.ClearSingletonLocks(browser.ConfigDir);
            return;
        }

        for (int i = 0; i < 40; i++)
        {
            try
            {
                if (!File.Exists(lockPath) && !Directory.Exists(lockPath))
                    break;
            }
            catch { break; }
            await Task.Delay(250);
        }

        // Process is gone — safe to remove stale singleton files so the next
        // --load-extension launch does not join a dead instance.
        if (clearLocks && !string.IsNullOrEmpty(browser.ConfigDir))
            ChromiumProfileSeeder.ClearSingletonLocks(browser.ConfigDir);
    }

    /// <summary>
    /// Windows ExtensionInstallForcelist via HKLM, deriving Policies\&lt;Vendor&gt;\&lt;Product&gt;
    /// from the install path shape (...\Vendor\Product\Application\exe) — not a brand table.
    /// Soft-fails without elevation so AttachExtensionAsync can use the launch ladder.
    /// </summary>
    private async Task<BrowserInstallResult> TryWindowsRegistryForcelistAsync(DetectedBrowser browser)
    {
        if (string.IsNullOrWhiteSpace(_config.CrxExtensionId) ||
            string.IsNullOrWhiteSpace(_config.ServerUrl))
        {
            return new BrowserInstallResult(false, false,
                "CRX extension ID not configured (ALPHA_CRX_EXTENSION_ID).");
        }

        var policyKey = DeriveWindowsChromiumPolicyKey(browser.BinaryPath);
        if (string.IsNullOrEmpty(policyKey))
        {
            return new BrowserInstallResult(false, false,
                "Could not derive Windows policy registry path from the browser install layout.");
        }

        var updateUrl = $"{_config.ServerUrl.TrimEnd('/')}/api/v1/extensions/{_config.CrxExtensionId}/update.xml";
        var policyValue = $"{_config.CrxExtensionId};{updateUrl}";

        try
        {
            // Attempt HKLM write; UAC elevation via a tiny reg.exe / elevated child is
            // required for machine policy. Soft-fail → launch ladder.
            var wrote = await WriteWindowsPolicyRegistryAsync(policyKey, policyValue);
            if (!wrote)
            {
                return new BrowserInstallResult(false, false,
                    "Could not write HKLM ExtensionInstallForcelist (elevation declined or denied).");
            }

            await RewriteManifestsWithCrxIdAsync();

            var alreadyRunning = IsBrowserProcessRunning(browser);
            if (alreadyRunning)
            {
                KillBrowserProcesses(browser);
                await WaitForBrowserExitAsync(browser);
            }

            var (launchFile, launchArgs) = BuildLaunch(browser, "--disable-gcm");
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = launchFile,
                Arguments = launchArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc == null)
                return new BrowserInstallResult(false, false, "Could not start the browser process.");

            return new BrowserInstallResult(true, alreadyRunning,
                "HKLM policy written; browser relaunched. Heartbeat flips to Active within ~30s.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows registry forcelist failed for {Name}", browser.Name);
            return new BrowserInstallResult(false, false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// From ...\Vendor\Product\Application\browser.exe → Software\Policies\Vendor\Product.
    /// </summary>
    private static string? DeriveWindowsChromiumPolicyKey(string binaryPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
                return null;

            var dir = Path.GetDirectoryName(binaryPath);
            if (string.IsNullOrEmpty(dir)) return null;

            // Expect ...\Vendor\Product\Application
            var application = Path.GetFileName(dir);
            if (!string.Equals(application, "Application", StringComparison.OrdinalIgnoreCase))
            {
                // Some Chromium builds omit Application — treat dir as Product.
                var productAlt = Path.GetFileName(dir);
                var vendorAlt = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
                if (string.IsNullOrEmpty(productAlt) || string.IsNullOrEmpty(vendorAlt))
                    return null;
                return $@"Software\Policies\{vendorAlt}\{productAlt}";
            }

            var productDir = Path.GetDirectoryName(dir);
            var vendorDir = Path.GetDirectoryName(productDir);
            var product = Path.GetFileName(productDir);
            var vendor = Path.GetFileName(vendorDir);
            if (string.IsNullOrEmpty(product) || string.IsNullOrEmpty(vendor))
                return null;

            return $@"Software\Policies\{vendor}\{product}";
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> WriteWindowsPolicyRegistryAsync(string policyKeyRelative, string forcelistValue)
    {
        // Write via elevated powershell Set-ItemProperty. One UAC prompt.
        var tmpPs1 = Path.Combine(Path.GetTempPath(), "aat_policy_" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            var keyPath = $@"HKLM:\{policyKeyRelative}\ExtensionInstallForcelist";
            var script =
                $"New-Item -Path '{keyPath}' -Force | Out-Null; " +
                $"Set-ItemProperty -Path '{keyPath}' -Name '1' -Value '{forcelistValue.Replace("'", "''")}' -Type String";
            await File.WriteAllTextAsync(tmpPs1, script);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments =
                    $"-NoProfile -ExecutionPolicy Bypass -Command " +
                    $"\"Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \\\"{tmpPs1}\\\"'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var exited = proc.WaitForExit(120_000);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows policy registry write failed");
            return false;
        }
        finally
        {
            try { if (File.Exists(tmpPs1)) File.Delete(tmpPs1); } catch { }
        }
    }

    /// <summary>
    /// Check if the native messaging host is running as a child (or descendant) of
    /// the given Chrome PID. Uses pgrep on Linux, ps on macOS, wmic on Windows.
    /// Returns false if the detection tool is unavailable (assume not loaded).
    /// </summary>
    private async Task<bool> IsNativeHostRunningForPidAsync(int parentPid)
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pgrep",
                    Arguments = $"-P {parentPid}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                var children = (await proc.StandardOutput.ReadToEndAsync()).Trim();
                proc.WaitForExit(1000);
                if (string.IsNullOrEmpty(children)) return false;

                foreach (var childPid in children.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(childPid.Trim(), out var pid)) continue;
                    var cmdPsi = new ProcessStartInfo
                    {
                        FileName = "ps",
                        Arguments = OperatingSystem.IsMacOS()
                            ? $"-p {pid} -o args="
                            : $"-p {pid} -o args= --no-headers",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var cmdProc = Process.Start(cmdPsi);
                    if (cmdProc == null) continue;
                    var cmdline = (await cmdProc.StandardOutput.ReadToEndAsync()).Trim();
                    cmdProc.WaitForExit(500);
                    if (IsNativeHostCommandLine(cmdline)) return true;
                }
            }
            else if (OperatingSystem.IsWindows())
            {
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
                if (proc == null) return false;
                var output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
                proc.WaitForExit(2000);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (IsNativeHostCommandLine(line)) return true;
                }
            }
        }
        catch { }
        return false;
    }

    private static bool IsNativeHostCommandLine(string cmdline) =>
        !string.IsNullOrEmpty(cmdline) &&
        (cmdline.Contains("--native-host", StringComparison.OrdinalIgnoreCase) ||
         cmdline.Contains("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
         cmdline.Contains(NativeMessagingPaths.GeckoApplicationId, StringComparison.OrdinalIgnoreCase));

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
    /// Fill in ConfigDir/DefaultProfileDir. Always refresh ConfigDir when empty
    /// even if DefaultProfileDir was set from a stale scan.
    /// </summary>
    private static void EnsureProfilePaths(DetectedBrowser browser)
    {
        if (!string.IsNullOrEmpty(browser.ConfigDir) && Directory.Exists(browser.ConfigDir) &&
            !string.IsNullOrEmpty(browser.DefaultProfileDir))
            return;

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
    /// Windows hosts live in HKCU under vendor NativeMessagingHosts keys.
    /// Structural scan — no brand list.
    /// </summary>
    private static bool IsWindowsNativeHostRegistered(BrowserCandidate? candidate = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser;
            using var software = hkcu.OpenSubKey(@"Software");
            if (software == null) return false;

            foreach (var vendor in software.GetSubKeyNames())
            {
                using (var nm = software.OpenSubKey($@"{vendor}\NativeMessagingHosts\com.alphai.tracker"))
                {
                    if (nm != null) return true;
                }

                using var vendorKey = software.OpenSubKey(vendor);
                if (vendorKey == null) continue;
                foreach (var product in vendorKey.GetSubKeyNames())
                {
                    using var nm = vendorKey.OpenSubKey($@"{product}\NativeMessagingHosts\com.alphai.tracker");
                    if (nm != null) return true;
                }
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

    /// <summary>True when a process matching this browser's binary name is running.</summary>
    private static bool IsBrowserProcessRunning(DetectedBrowser browser)
    {
        var name = browser.BinaryName;
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileNameWithoutExtension(browser.BinaryPath);
        if (string.IsNullOrWhiteSpace(name)) return false;
        return IsProcessNameRunning(name);
    }

    private static bool IsProcessNameRunning(string processName)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var bare = Path.GetFileNameWithoutExtension(processName);
                return Process.GetProcesses().Any(p =>
                {
                    try
                    {
                        return p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase) ||
                               p.ProcessName.Equals(bare, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                });
            }

            var psi = new ProcessStartInfo
            {
                FileName = "pgrep",
                Arguments = $"-f \"{processName}\"",
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
            return false;
        }
    }

    /// <summary>Gracefully kill processes for this browser (prefer exe path match on Windows).</summary>
    private static void KillBrowserProcesses(DetectedBrowser browser)
    {
        var name = browser.BinaryName;
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileNameWithoutExtension(browser.BinaryPath);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var binFull = string.IsNullOrEmpty(browser.BinaryPath)
                    ? null
                    : Path.GetFullPath(browser.BinaryPath);
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var match = false;
                        if (!string.IsNullOrEmpty(binFull))
                        {
                            try
                            {
                                var path = proc.MainModule?.FileName;
                                if (!string.IsNullOrEmpty(path) &&
                                    path.Equals(binFull, StringComparison.OrdinalIgnoreCase))
                                    match = true;
                            }
                            catch
                            {
                                // Access denied on some processes — fall back to name.
                            }
                        }

                        if (!match)
                        {
                            match = proc.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                    Path.GetFileNameWithoutExtension(name)
                                        .Equals(proc.ProcessName, StringComparison.OrdinalIgnoreCase);
                        }

                        if (match)
                            proc.Kill(entireProcessTree: true);
                    }
                    catch { }
                }
                return;
            }

            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "pkill",
                Arguments = $"-f \"{name}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(2000);
            Thread.Sleep(500);
            using var killProc = Process.Start(new ProcessStartInfo
            {
                FileName = "pkill",
                Arguments = $"-9 -f \"{name}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            killProc?.WaitForExit(1000);
        }
        catch { }
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

    /// <summary>
    /// Check if the browser extension is actively connected by querying the
    /// NativeMessageService heartbeat.
    /// </summary>
    private Task<bool> IsExtensionActiveAsync(CancellationToken ct)
    {
        var connected = _nativeMessageService.IsExtensionConnected();
        return Task.FromResult(connected);
    }

    /// <summary>Check if the extension is already injected in a Chromium Preferences file (pure C#).</summary>
    private bool IsExtensionInjectedInProfile(DetectedBrowser browser)
    {
        try
        {
            EnsureProfilePaths(browser);
            if (string.IsNullOrEmpty(browser.DefaultProfileDir)) return false;
            var prefsPath = Path.Combine(browser.DefaultProfileDir, "Preferences");
            if (!File.Exists(prefsPath)) return false;

            var extDir = Path.GetFullPath(browser.ExtensionDir);
            using var doc = JsonDocument.Parse(File.ReadAllText(prefsPath));
            if (!doc.RootElement.TryGetProperty("extensions", out var exts)) return false;
            if (!exts.TryGetProperty("settings", out var settings)) return false;
            foreach (var prop in settings.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("path", out var pathEl))
                {
                    var path = pathEl.GetString() ?? "";
                    if (path.StartsWith(extDir, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    private async Task<bool> InstallNativeHostManuallyAsync(CancellationToken ct)
    {
        try
        {
            var hostPath = ResolveNativeHostBinaryPath();
            if (string.IsNullOrEmpty(hostPath) || !File.Exists(hostPath))
            {
                _logger.LogWarning("Tracker executable not found for native host path — cannot install native host");
                return false;
            }

            var extensionDir = ResolveEngineExtensionDir(BrowserEngine.Chromium);
            var extId = Directory.Exists(extensionDir)
                ? ExtensionIdCalculator.Compute(extensionDir)
                : "unknown";
            if (extId == "unknown")
            {
                _logger.LogWarning(
                    "Could not compute extension ID for {Path} — manifest allowed_origins will be empty",
                    extensionDir);
            }

            var written = new List<string>();

            foreach (var browser in _detected.ToList())
            {
                foreach (var (targetPath, isGecko) in GetManifestTargetsFor(browser))
                {
                    await WriteNativeHostManifestAsync(targetPath, hostPath, extId, isGecko, ct);
                    written.Add(targetPath);
                }
            }

            if (written.Count == 0)
            {
                await WriteNativeHostManifestAsync(
                    Path.Combine(_fallbackChromiumNativeHostDir, "com.alphai.tracker.json"),
                    hostPath, extId, isGecko: false, ct);
                await WriteNativeHostManifestAsync(
                    Path.Combine(_geckoNativeHostDir, "com.alphai.tracker.json"),
                    hostPath, extId, isGecko: true, ct);
                written.Add(_fallbackChromiumNativeHostDir);
                written.Add(_geckoNativeHostDir);
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var sideBySideManifest = Path.Combine(
                        Path.GetDirectoryName(hostPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                        "com.alphai.tracker.json");
                    await File.WriteAllTextAsync(sideBySideManifest,
                        BuildManifestJson(hostPath, extId, isGecko: false), ct);
                    RegisterNativeHostWindows(sideBySideManifest);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to register Windows native messaging host");
                }
            }

            _logger.LogInformation(
                "Native host manifests created for {Count} browsers (host: {Path}, extension ID: {ExtId})",
                _detected.Count, hostPath, extId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create native host manifests manually");
            return false;
        }
    }

    /// <summary>
    /// Manifest targets for a browser. Gecko → ~/.mozilla/native-messaging-hosts (+ snap copy).
    /// Chromium → per-browser NativeMessagingHosts from ManifestPath. WebKit/Unknown → none.
    /// </summary>
    private List<(string Path, bool IsGecko)> GetManifestTargetsFor(DetectedBrowser browser)
    {
        var targets = new List<(string, bool)>();

        if (browser.Engine is BrowserEngine.Unknown or BrowserEngine.WebKit)
        {
            _logger.LogDebug(
                "Skipping native-messaging manifest for {Name} (Engine={Engine})", browser.Name, browser.Engine);
            return targets;
        }

        if (browser.Engine == BrowserEngine.Gecko)
        {
            targets.Add((Path.Combine(_geckoNativeHostDir, "com.alphai.tracker.json"), true));

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
            targets.Add((Path.Combine(_fallbackChromiumNativeHostDir, "com.alphai.tracker.json"), false));
        }

        return targets;
    }

    /// <summary>Serialize and write one native-messaging host manifest.</summary>
    private static async Task WriteNativeHostManifestAsync(
        string targetPath, string hostPath, string extId, bool isGecko, CancellationToken ct) =>
        await WriteNativeHostManifestAsync(targetPath, hostPath, new[] { extId }, isGecko, ct);

    private static async Task WriteNativeHostManifestAsync(
        string targetPath, string hostPath, IEnumerable<string> extIds, bool isGecko, CancellationToken ct)
    {
        var json = BuildManifestJson(hostPath, extIds, isGecko);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? string.Empty);
        await File.WriteAllTextAsync(targetPath, json, ct);
    }

    /// <summary>Build the manifest JSON for a specific engine family (path = C# tracker exe).</summary>
    private static string BuildManifestJson(string hostPath, string extId, bool isGecko) =>
        BuildManifestJson(hostPath, new[] { extId }, isGecko);

    private static string BuildManifestJson(string hostPath, IEnumerable<string> extIds, bool isGecko)
    {
        var ids = extIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && id != "unknown")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var manifest = new Dictionary<string, object?>
        {
            ["name"] = "com.alphai.tracker",
            ["description"] = "Alpha AI Tracker — Native messaging bridge for browser tab/URL capture",
            ["path"] = hostPath,
            ["type"] = "stdio",
        };
        if (isGecko)
            manifest["allowed_extensions"] = new[] { NativeMessagingPaths.GeckoApplicationId };
        else
            manifest["allowed_origins"] = ids.Select(id => $"chrome-extension://{id}/").ToArray();

        return System.Text.Json.JsonSerializer.Serialize(manifest,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Absolute path of the tracker executable used as the native messaging host.
    /// The browser spawns this binary; Program.cs detects host mode via argv and
    /// runs <see cref="NativeMessagingHost.Run"/> without taking the app mutex.
    /// </summary>
    public static string ResolveNativeHostBinaryPath()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
                return Path.GetFullPath(processPath);
        }
        catch { }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (var name in new[] { "client", "alpha-ai-tracker", "AlphaAITracker" })
        {
            foreach (var ext in OperatingSystem.IsWindows()
                         ? new[] { ".exe", ".dll" }
                         : new[] { "", ".dll" })
            {
                var candidate = Path.Combine(baseDir, name + ext);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        return Path.GetFullPath(Path.Combine(baseDir, OperatingSystem.IsWindows() ? "client.exe" : "client"));
    }

    /// <summary>Resolve the absolute path to an engine extension directory (chromium / gecko).</summary>
    public static string ResolveEngineExtensionDir(BrowserEngine engine)
    {
        var folder = engine == BrowserEngine.Gecko ? "gecko" : "chromium";
        var legacy = engine == BrowserEngine.Gecko ? "firefox" : "chrome";
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "extensions", folder),
            Path.Combine(baseDir, "extensions", legacy),
            Path.Combine(baseDir, "publish", "extensions", folder),
            Path.Combine(baseDir, "publish", "extensions", legacy),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(c)) return Path.GetFullPath(c);
        }

        var dir = baseDir;
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            foreach (var name in new[] { folder, legacy })
            {
                var c = Path.Combine(dir, "extensions", name);
                if (Directory.Exists(c)) return Path.GetFullPath(c);
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        return Path.Combine(baseDir, "extensions", folder);
    }

    /// <summary>
    /// Ensure HKCU NativeMessagingHosts exists for this browser's Vendor\Product
    /// (derived from BinaryPath), even if the key was never created before.
    /// </summary>
    private void EnsureChromiumNativeHostRegistry(DetectedBrowser browser)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var soft = ChromiumPersistentInstaller.DeriveSoftwareVendorProductKey(browser.BinaryPath);
            if (string.IsNullOrEmpty(soft)) return;

            var hostPath = ResolveNativeHostBinaryPath();
            var sideBySide = Path.Combine(
                Path.GetDirectoryName(hostPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                "com.alphai.tracker.json");
            if (!File.Exists(sideBySide)) return;

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                $@"{soft}\NativeMessagingHosts\com.alphai.tracker", true);
            key?.SetValue(null, sideBySide, Microsoft.Win32.RegistryValueKind.String);
            _logger.LogInformation("Ensured native-host registry {Soft}\\NativeMessagingHosts", soft);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EnsureChromiumNativeHostRegistry failed");
        }
    }

    /// <summary>
    /// Register the native messaging host in HKCU for Chromium-shaped
    /// NativeMessagingHosts keys already present. No hardcoded vendor list.
    /// </summary>
    private void RegisterNativeHostWindows(string manifestPath)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!File.Exists(manifestPath))
        {
            _logger.LogWarning("Manifest file does not exist at {Path} — skipping registry write", manifestPath);
            return;
        }

        const string hostName = "com.alphai.tracker";
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser;
            using var software = hkcu.OpenSubKey(@"Software", writable: false);
            if (software != null)
            {
                foreach (var vendor in software.GetSubKeyNames())
                {
                    using var vendorKey = software.OpenSubKey(vendor);
                    if (vendorKey == null) continue;
                    if (vendorKey.OpenSubKey("NativeMessagingHosts") != null)
                        roots.Add($@"Software\{vendor}\NativeMessagingHosts");
                    foreach (var product in vendorKey.GetSubKeyNames())
                    {
                        using var productKey = vendorKey.OpenSubKey(product);
                        if (productKey?.OpenSubKey("NativeMessagingHosts") != null)
                            roots.Add($@"Software\{vendor}\{product}\NativeMessagingHosts");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed enumerating Windows NativeMessagingHosts roots");
        }

        foreach (var b in _detected.Where(x => x.Engine == BrowserEngine.Chromium))
        {
            if (!string.IsNullOrEmpty(b.DesktopId))
                roots.Add($@"Software\{b.DesktopId}\NativeMessagingHosts");

            // Always derive from install path (...\Vendor\Product\Application\exe).
            var soft = ChromiumPersistentInstaller.DeriveSoftwareVendorProductKey(b.BinaryPath);
            if (!string.IsNullOrEmpty(soft))
                roots.Add($@"{soft}\NativeMessagingHosts");
        }

        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser;
            foreach (var root in roots)
            {
                try
                {
                    using var rootKey = hkcu.CreateSubKey(root, writable: true);
                    if (rootKey == null) continue;
                    using var hostKey = rootKey.CreateSubKey(hostName, writable: true);
                    if (hostKey == null) continue;
                    hostKey.SetValue(null, manifestPath, Microsoft.Win32.RegistryValueKind.String);
                    _logger.LogInformation("Registered native messaging host: {Root}\\{Name}", root, hostName);
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

    /// <summary>Resolve the extensions/ directory from the running executable.</summary>
    private static string ResolveExtensionsRoot()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        var sameDir = Path.Combine(baseDir, "extensions");
        if (Directory.Exists(sameDir))
            return sameDir;

        var dir = baseDir;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "extensions");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null) break;
            dir = parent;
        }

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
        catch { }

        return sameDir;
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
    /// crashed session. Matches by COMMAND LINE — never by bare exe name,
    /// since the host now shares the tracker's exe name.
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
        }
    }
}

// ─── Models ───

public enum BrowserType { Chromium, Gecko, WebKit }

public enum BrowserInstallStatus
{
    NotInstalled,
    ReadyToInstall,
    NativeHostReady,
    Loading,
    ExtensionActive,
    NotSupported,
}

public class DetectedBrowser
{
    public string Name { get; set; } = string.Empty;
    public string BinaryPath { get; set; } = string.Empty;
    public string BinaryName { get; set; } = string.Empty;
    public string ExtensionDir { get; set; } = string.Empty;
    public BrowserInstallStatus Status { get; set; } = BrowserInstallStatus.NotInstalled;
    public BrowserType BrowserType { get; set; } = BrowserType.Chromium;
    public bool IsChromeBased { get; set; } = true;
    public bool NativeHostInstalled { get; set; }
    public bool IsInstalled => !string.IsNullOrEmpty(BinaryPath);

    public BrowserEngine Engine { get; set; } = BrowserEngine.Unknown;
    public bool IsDefault { get; set; }
    public string DesktopId { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string ConfigDir { get; set; } = string.Empty;
    public string DefaultProfileDir { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string? PolicyDir { get; set; }
    public string? FlatpakAppId { get; set; }

    public string StatusText => Status switch
    {
        BrowserInstallStatus.ReadyToInstall => "Ready to install",
        BrowserInstallStatus.NativeHostReady => "Host ready — click to load",
        BrowserInstallStatus.Loading => "Installing & launching…",
        BrowserInstallStatus.ExtensionActive => "Connected",
        BrowserInstallStatus.NotSupported => "Engine not supported",
        _ => "Not detected",
    };

    public string EngineText => Engine switch
    {
        BrowserEngine.Chromium => "Chromium",
        BrowserEngine.Gecko => "Gecko",
        BrowserEngine.WebKit => "WebKit",
        _ => "Unknown engine",
    };

    public string StatusLine =>
        $"{StatusText} · {EngineText}{(IsDefault ? " · System default" : "")}";

    public string ActionButtonText => Status switch
    {
        BrowserInstallStatus.ReadyToInstall => "Install Extension",
        BrowserInstallStatus.NativeHostReady => "Install Extension",
        BrowserInstallStatus.Loading => "Working…",
        BrowserInstallStatus.ExtensionActive => "Connected",
        BrowserInstallStatus.NotSupported => "Not supported",
        _ => "—",
    };

    public bool CanInstall => Status is BrowserInstallStatus.ReadyToInstall or BrowserInstallStatus.NativeHostReady;

    /// <summary>
    /// True while an attach may proceed — includes <see cref="BrowserInstallStatus.Loading"/>
    /// so the UI can set progress state before calling Attach without falsely rejecting.
    /// </summary>
    public bool MayAttach =>
        Status is BrowserInstallStatus.ReadyToInstall
            or BrowserInstallStatus.NativeHostReady
            or BrowserInstallStatus.Loading;
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
