using System.Diagnostics;
using System.Text.Json;
using client.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace client.Core.Browser.Engines;

/// <summary>
/// Gecko engine adapter (Firefox, Waterfox, LibreWolf, ...).
/// Protocol: RDP (classic) / WebDriver BiDi. Firefox removed CDP support in FF 85,
/// so this adapter probes the version and degrades honestly when a debugger is unavailable.
/// Default policy: Debugger=Automatic (flag) — verified at runtime; private windows untracked.
/// </summary>
public sealed class GeckoEngineAdapter : Abstractions.IBrowserEngineAdapter
{
    private readonly IInstalledAppDetector _appDetector;
    private readonly ILogger _logger;
    private readonly bool _autoLaunch;
    private readonly TimeSpan _hijackCooldown;
    private readonly Dictionary<Guid, DateTime> _lastHijack = new();

    public GeckoEngineAdapter(
        IInstalledAppDetector appDetector,
        bool autoLaunch,
        TimeSpan hijackCooldown,
        ILogger<GeckoEngineAdapter> logger)
    {
        _appDetector = appDetector;
        _autoLaunch = autoLaunch;
        _hijackCooldown = hijackCooldown;
        _logger = logger;
    }

    public BrowserEngine Engine => BrowserEngine.Gecko;

    public Task<IReadOnlyList<DetectedBrowserRuntime>> DetectRuntimesAsync(CancellationToken ct)
    {
        var result = new List<DetectedBrowserRuntime>();
        foreach (var app in _appDetector.GetAllInstalledApplications().Where(a => a.IsBrowser))
        {
            try
            {
                if (BrowserEngineFamily.Classify(app.BinaryName ?? app.AppName) != BrowserEngine.Gecko)
                    continue;
                var binaryPath = ResolveBinaryPath(app);
                if (string.IsNullOrEmpty(binaryPath)) continue;
                var runtime = new DetectedBrowserRuntime
                {
                    Engine = BrowserEngine.Gecko,
                    BinaryName = string.IsNullOrWhiteSpace(app.BinaryName)
                        ? Path.GetFileNameWithoutExtension(binaryPath)
                        : app.BinaryName,
                    BinaryPath = binaryPath,
                    DisplayName = app.AppName,
                    InstalledAppId = app.Id,
                    UserDataDir = ResolveProfileRoot(app),
                    Version = string.IsNullOrWhiteSpace(app.AppVersion) ? null : app.AppVersion,
                };
                runtime.Capabilities = ProbeCapabilities(runtime);
                runtime.Profiles = DiscoverProfiles(runtime).ToList();
                result.Add(runtime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gecko detection failed for {App}", app.AppName);
            }
        }

        // Running-process probe (same rationale as the Chromium adapter): a browser that is live
        // right now but missing from the installed-apps catalog still gets a runtime identity so
        // the hijack/attach path can track it immediately.
        foreach (var hit in RunningBrowserProbe.Detect(BrowserEngine.Gecko))
        {
            if (result.Any(r => string.Equals(r.BinaryPath, hit.BinaryPath, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                var runtime = new DetectedBrowserRuntime
                {
                    Engine = BrowserEngine.Gecko,
                    BinaryName = hit.BinaryName,
                    BinaryPath = hit.BinaryPath,
                    DisplayName = hit.DisplayName,
                    InstalledAppId = null,
                    UserDataDir = ResolveProfileRoot(new Models.InstalledApplication { InstallPath = hit.BinaryPath }),
                    Version = null,
                };
                runtime.Capabilities = ProbeCapabilities(runtime);
                runtime.Profiles = DiscoverProfiles(runtime).ToList();
                _logger.LogInformation(
                    "Running-process probe detected browser {Display} at {Path}", hit.DisplayName, hit.BinaryPath);
                result.Add(runtime);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Running-process probe failed for {Path}", hit.BinaryPath);
            }
        }
        return Task.FromResult<IReadOnlyList<DetectedBrowserRuntime>>(result);
    }

    public BrowserCapabilities ProbeCapabilities(DetectedBrowserRuntime runtime) => new()
    {
        Capabilities = new()
        {
            // Debugger requires a version-gated flag launch; probe at connect time.
            [Capability.Debugger] = CapabilityClassification.Automatic,
            [Capability.Profiles] = CapabilityClassification.Automatic,
            [Capability.NetworkEvents] = CapabilityClassification.Automatic,
            [Capability.History] = CapabilityClassification.Automatic,
            [Capability.DomInspection] = CapabilityClassification.Automatic,
            // Firefox RDP/BiDi does not reliably expose private windows.
            [Capability.IncognitoDetection] = CapabilityClassification.Unsupported,
        }
    };

    public IReadOnlyList<BrowserProfileInfo> DiscoverProfiles(DetectedBrowserRuntime runtime)
    {
        var result = new List<BrowserProfileInfo>();
        try
        {
            var profilesIni = Path.Combine(runtime.UserDataDir ?? string.Empty, "profiles.ini");
            if (!File.Exists(profilesIni)) return result;

            string? currentName = null;
            string? currentPath = null;
            foreach (var line in File.ReadLines(profilesIni))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[Profile", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentName != null && currentPath != null)
                        result.Add(new BrowserProfileInfo { Name = currentName, ProfileDir = currentPath });
                    currentName = null;
                    currentPath = null;
                }
                else if (trimmed.StartsWith("Name=", StringComparison.OrdinalIgnoreCase))
                {
                    currentName = trimmed[5..].Trim();
                }
                else if (trimmed.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                {
                    currentPath = Path.IsPathRooted(trimmed[5..].Trim())
                        ? trimmed[5..].Trim()
                        : Path.Combine(runtime.UserDataDir!, trimmed[5..].Trim());
                }
                else if (trimmed.StartsWith("IsRelative=", StringComparison.OrdinalIgnoreCase)
                         && currentPath != null && runtime.UserDataDir != null
                         && trimmed[11..].Trim() == "0")
                {
                    // Path already absolute; leave as-is.
                }
            }
            if (currentName != null && currentPath != null)
                result.Add(new BrowserProfileInfo { Name = currentName, ProfileDir = currentPath });
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Firefox profile discovery failed: {Msg}", ex.Message);
        }
        return result;
    }

    public async Task<Abstractions.IBrowserConnection?> LaunchAndConnectAsync(
        DetectedBrowserRuntime runtime, int port, CancellationToken ct)
    {
        // 1) Already debugging on this port?
        if (await PortRespondsAsync(port, ct))
            if (await OpenAsync(runtime, port, ct) is { } existing)
                return existing;

        // 2) Not running → launch the real profile with the debug flag. Auto-launch only
        //    (default off): the tracker is attach-only, it never opens a browser by itself.
        if (_autoLaunch && !BrowserProcessProbe.IsRunning(runtime) && !string.IsNullOrEmpty(runtime.BinaryPath))
        {
            if (await LaunchAndConnectAsync(runtime, port, runtime.UserDataDir, ct) is { } fresh)
                return fresh;
        }

        // 3) Running → recover a firefox that IS exposing a debugging port (e.g. a debug
        //    instance left by a previous client run). If its session is exhausted (Firefox
        //    caps active WebDriver sessions and zombie sessions survive a client crash),
        //    OpenAsync returns null and we fall through to a dedicated instance instead.
        var orphanPort = BrowserProcessProbe.FindRemoteDebuggingPort(runtime.BinaryName);
        if (orphanPort > 0 && await PortRespondsAsync(orphanPort, ct))
        {
            _logger.LogInformation(
                "Reusing running debug instance for {Runtime} on port {Port}",
                runtime.BinaryName, orphanPort);
            if (await OpenAsync(runtime, orphanPort, ct) is { } orphan)
                return orphan;
        }

        // 4) Running without a usable debugger → the ONLY way to track the employee's real
        //    profile is to terminate the normal instance and relaunch the SAME profile with
        //    the debug flag (a normally-launched browser can't be debugged retroactively).
        //    Cooldown prevents the reconnect loop from killing the browser repeatedly.
        if (_autoLaunch && !await PortRespondsAsync(port, ct))
        {
            var now = DateTime.UtcNow;
            if (!_lastHijack.TryGetValue(runtime.Id, out var last) || now - last > _hijackCooldown)
            {
                _lastHijack[runtime.Id] = now;
                if (BrowserProcessProbe.IsRunning(runtime))
                {
                    var realProfile = ResolveRealProfileDir(runtime);
                    if (!string.IsNullOrEmpty(realProfile))
                    {
                        _logger.LogWarning(
                            "{Runtime} is running without a usable debugger — relaunching it "
                            + "with remote debugging on its real profile so browsing is tracked.",
                            runtime.BinaryName);
                        BrowserProcessProbe.TerminateInstances(runtime);
                        await Task.Delay(2000, ct);
                        if (await LaunchAndConnectAsync(runtime, port, realProfile, ct) is { } hijacked)
                            return hijacked;
                    }
                }
            }
        }

        // 4b) Fallback when the real profile can't be relaunched: dedicated debug instance
        //     on a dedicated profile. Isolates tracker sessions from the user's Firefox.
        //     Auto-launch only.
        if (!_autoLaunch)
        {
            _logger.LogInformation(
                "{Runtime} has no usable debugger and auto-launch is disabled — not attaching.",
                runtime.BinaryName);
            return null;
        }
        _logger.LogWarning(
            "{Runtime} could not be relaunched on its real profile. Launching a separate debug "
            + "instance on a dedicated profile as a fallback.", runtime.BinaryName);
        var debugDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "alpha-ai-tracker", "browser-debug", $"gecko-{runtime.Id.ToString("N")}");
        if (await LaunchAndConnectAsync(runtime, port, debugDir, ct) is { } dedicated)
            return dedicated;

        _logger.LogWarning(
            "{Runtime} did not expose a usable debugging endpoint. "
            + "Falling back to Unsupported — no fabricated data.", runtime.BinaryName);
        return null;
    }

    private async Task<Abstractions.IBrowserConnection?> LaunchAndConnectAsync(
        DetectedBrowserRuntime runtime, int port, string? profileDir, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(runtime.BinaryPath)) return null;
        try
        {
            if (!string.IsNullOrEmpty(profileDir))
                Directory.CreateDirectory(profileDir);

            var args = new List<string>
            {
                $"--remote-debugging-port {port}",
                "-new-instance",
                "-no-remote",
            };
            if (!string.IsNullOrEmpty(profileDir))
                args.Add($"--profile {Quote(profileDir)}");
            args.Add("about:blank");

            var psi = new ProcessStartInfo(runtime.BinaryPath, string.Join(' ', args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            _logger.LogInformation("Launched {Runtime} (Gecko) on port {Port}", runtime.BinaryName, port);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to launch {Runtime}", runtime.BinaryName);
            return null;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await PortRespondsAsync(port, ct))
                return await OpenAsync(runtime, port, ct);
            await Task.Delay(500, ct);
        }
        return null;
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    /// <summary>The actual profile directory of the user's real Firefox (the default
    /// profile, which Firefox's own instance manager locks), or null if unknown.</summary>
    private string? ResolveRealProfileDir(DetectedBrowserRuntime runtime)
    {
        try
        {
            if (!string.IsNullOrEmpty(runtime.UserDataDir) && Directory.Exists(runtime.UserDataDir))
            {
                var profilesIni = Path.Combine(runtime.UserDataDir, "profiles.ini");
                if (File.Exists(profilesIni))
                {
                    string? currentPath = null;
                    string? isRelative = "1";
                    foreach (var line in File.ReadLines(profilesIni))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                            currentPath = trimmed[5..].Trim();
                        else if (trimmed.StartsWith("IsRelative=", StringComparison.OrdinalIgnoreCase))
                            isRelative = trimmed[11..].Trim();
                        else if (trimmed.StartsWith("Default=", StringComparison.OrdinalIgnoreCase)
                                 && trimmed[8..].Trim() == "1" && currentPath != null)
                        {
                            return isRelative == "0" ? currentPath
                                : Path.Combine(runtime.UserDataDir, currentPath);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Real profile resolution failed: {Msg}", ex.Message);
        }
        return null;
    }

    private static async Task<bool> PortRespondsAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await client.GetAsync($"http://127.0.0.1:{port}/", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<Abstractions.IBrowserConnection?> OpenAsync(
        DetectedBrowserRuntime runtime, int port, CancellationToken ct)
    {
        try
        {
            var conn = new GeckoConnection(runtime, port, _logger);
            await conn.StartAsync(ct);
            if (!conn.IsConnected)
            {
                _logger.LogWarning("Gecko endpoint on port {Port} is not BiDi-compatible — Unsupported.", port);
                return null;
            }
            return conn;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gecko debugger handshake failed on port {Port} — Unsupported.", port);
            return null;
        }
    }

    private string? ResolveBinaryPath(Models.InstalledApplication app)
    {
        if (!string.IsNullOrWhiteSpace(app.InstallPath) && File.Exists(app.InstallPath.Trim().Trim('"')))
            return app.InstallPath.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(app.BinaryName))
        {
            var cmd = OperatingSystem.IsWindows() ? "where" : "which";
            try
            {
                var psi = new ProcessStartInfo(cmd, app.BinaryName)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                if (p.ExitCode == 0 && output.Length > 0)
                    return output.Split('\n')[0].Trim();
            }
            catch { }
        }
        return null;
    }

    private string? ResolveProfileRoot(Models.InstalledApplication app)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux())
        {
            if (Directory.Exists(Path.Combine(home, ".mozilla", "firefox")))
                return Path.Combine(home, ".mozilla", "firefox");
            // snap-packaged Firefox keeps profiles under ~/snap/firefox/common/.mozilla/firefox.
            if (Directory.Exists(Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox")))
                return Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox");
        }
        if (OperatingSystem.IsMacOS() && Directory.Exists(Path.Combine(home, "Library", "Application Support", "Firefox")))
            return Path.Combine(home, "Library", "Application Support", "Firefox");
        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(app.InstallPath))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox");
        return null;
    }
}
