using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using client.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace client.Core.Browser.Engines;

/// <summary>
/// Chromium engine adapter (Chrome, Chromium, Edge, Brave, Opera, Vivaldi, Arc, ...).
/// Protocol: Chrome DevTools Protocol (CDP) over WebSocket. Engine-scoped, never brand-driven.
/// </summary>
public sealed class ChromiumEngineAdapter : Abstractions.IBrowserEngineAdapter
{
    private readonly IInstalledAppDetector _appDetector;
    private readonly ILogger _logger;
    private readonly bool _autoLaunch;
    private readonly TimeSpan _hijackCooldown;
    private readonly Dictionary<Guid, DateTime> _lastHijack = new();
    /// <summary>Runtimes where the real-profile relaunch provably does NOT expose a debug port
    /// (modern Chrome blocks the flag on the default profile). Once proven, never hijack again
    /// this app run — a kill would lose the employee's browser for zero tracking gain.</summary>
    private readonly HashSet<Guid> _realProfileBlocked = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public ChromiumEngineAdapter(
        IInstalledAppDetector appDetector,
        bool autoLaunch,
        TimeSpan hijackCooldown,
        ILogger<ChromiumEngineAdapter> logger)
    {
        _appDetector = appDetector;
        _autoLaunch = autoLaunch;
        _hijackCooldown = hijackCooldown;
        _logger = logger;
    }

    public BrowserEngine Engine => BrowserEngine.Chromium;

    public Task<IReadOnlyList<DetectedBrowserRuntime>> DetectRuntimesAsync(CancellationToken ct)
    {
        var result = new List<DetectedBrowserRuntime>();
        foreach (var app in _appDetector.GetAllInstalledApplications().Where(a => a.IsBrowser))
        {
            try
            {
                if (BrowserEngineFamily.Classify(app.BinaryName ?? app.AppName) != BrowserEngine.Chromium)
                    continue;
                var binaryPath = ResolveBinaryPath(app);
                if (string.IsNullOrEmpty(binaryPath)) continue;

                var runtime = new DetectedBrowserRuntime
                {
                    Engine = BrowserEngine.Chromium,
                    BinaryName = string.IsNullOrWhiteSpace(app.BinaryName)
                        ? Path.GetFileNameWithoutExtension(binaryPath)
                        : app.BinaryName,
                    BinaryPath = binaryPath,
                    DisplayName = app.AppName,
                    InstalledAppId = app.Id,
                    UserDataDir = ResolveUserDataDir(app, binaryPath),
                    Version = string.IsNullOrWhiteSpace(app.AppVersion) ? null : app.AppVersion,
                };
                runtime.Capabilities = ProbeCapabilities(runtime);
                runtime.Profiles = DiscoverProfiles(runtime).ToList();
                result.Add(runtime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Chromium detection failed for {App}", app.AppName);
            }
        }

        // Running-process probe: browsers that are LIVE right now but missing from the
        // installed-apps catalog (covers install → use → uninstall inside the ~15-min catalog
        // scan window, and browsers whose catalog entry never got detected).
        foreach (var hit in RunningBrowserProbe.Detect(BrowserEngine.Chromium))
        {
            if (result.Any(r => string.Equals(r.BinaryPath, hit.BinaryPath, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                var runtime = new DetectedBrowserRuntime
                {
                    Engine = BrowserEngine.Chromium,
                    BinaryName = hit.BinaryName,
                    BinaryPath = hit.BinaryPath,
                    DisplayName = hit.DisplayName,
                    InstalledAppId = null,
                    UserDataDir = ResolveUserDataDir(
                        new Models.InstalledApplication
                        {
                            AppName = hit.DisplayName,
                            BinaryName = hit.BinaryName,
                            InstallPath = hit.BinaryPath,
                        },
                        hit.BinaryPath),
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
            [Capability.Debugger] = CapabilityClassification.Automatic,
            [Capability.Profiles] = CapabilityClassification.Automatic,
            [Capability.NetworkEvents] = CapabilityClassification.Automatic,
            [Capability.DomInspection] = CapabilityClassification.Automatic,
            [Capability.IncognitoDetection] = CapabilityClassification.Automatic,
            [Capability.History] = CapabilityClassification.Automatic,
        }
    };

    public IReadOnlyList<BrowserProfileInfo> DiscoverProfiles(DetectedBrowserRuntime runtime)
    {
        var profiles = new List<BrowserProfileInfo>();
        try
        {
            var localState = Path.Combine(runtime.UserDataDir ?? string.Empty, "Local State");
            if (!File.Exists(localState)) return profiles;

            using var doc = JsonDocument.Parse(File.ReadAllText(localState));
            if (doc.RootElement.TryGetProperty("profile", out var profileNode) &&
                profileNode.TryGetProperty("info_cache", out var cache))
            {
                foreach (var kv in cache.EnumerateObject())
                {
                    var name = kv.Value.TryGetProperty("name", out var n) ? n.GetString() : kv.Name;
                    profiles.Add(new BrowserProfileInfo
                    {
                        Name = name ?? kv.Name,
                        ProfileDir = Path.Combine(runtime.UserDataDir!, kv.Name),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Profile discovery failed for {Runtime}: {Msg}", runtime.BinaryName, ex.Message);
        }
        return profiles;
    }

    public async Task<Abstractions.IBrowserConnection?> LaunchAndConnectAsync(
        DetectedBrowserRuntime runtime, int port, CancellationToken ct)
    {
        // 0) The real profile is ALREADY running with a debugger (DevToolsActivePort present) →
        //    attach directly, never touch it. Ideal state: a browser we relaunched previously,
        //    or a browser the user/dev started with a debug flag.
        if (runtime.UserDataDir != null)
        {
            var activePortFile = Path.Combine(runtime.UserDataDir, "DevToolsActivePort");
            if (File.Exists(activePortFile) && TryReadActivePort(activePortFile, out var activePort)
                && await PortRespondsAsync(activePort, ct))
            {
                runtime.DebugPort = activePort;
                return await OpenConnectionAsync(runtime, activePort, ct);
            }
        }

        var debugDir = DebugDirFor(runtime);
        // Our own dedicated debug instances live under the tracker debug dir; they are never
        // "the user's real browser" — never mistaken for it and never killed during a hijack.
        var realRunning = BrowserProcessProbe.IsRunning(runtime, excludeCmdlineToken: debugDir);
        var realName = BrowserProcessProbe.RealBinaryName(runtime);

        // 1) The real browser already exposes a debug port (any chrome with the flag that is NOT
        //    our dedicated instance) → attach; no kill needed, real browsing is tracked.
        if (!string.IsNullOrEmpty(realName))
        {
            var dedicatedPorts = debugDir == null
                ? new HashSet<int>()
                : BrowserProcessProbe.FindRemoteDebuggingPorts(
                    debugDir.Replace("\\", "/", StringComparison.Ordinal)).ToHashSet();
            var realDebugPorts = BrowserProcessProbe.FindRemoteDebuggingPorts(realName)
                .Where(p => !dedicatedPorts.Contains(p));
            if (await TryAttachFirstRespondingAsync(runtime, realDebugPorts, ct) is { } debugged)
                return debugged;
        }

        // 2) The user's real browser IS running, but WITHOUT a debugger. A normally-launched
        //    browser cannot be debugged retroactively — the ONLY way to track the user's real
        //    browsing is to terminate it and relaunch the SAME real profile with the debug flag.
        //    Permission for this is granted at install time. A cooldown prevents kill-loops.
        if (_autoLaunch && realRunning)
        {
            if (await TryHijackRealProfileAsync(runtime, port, debugDir, ct) is { } hijacked)
                return hijacked;
            // Real-profile relaunch did not expose a port (modern Chrome blocks the flag on the
            // default profile even with an explicit --user-data-dir) — fall through. The
            // employee's real browser is left running untracked; the dedicated instance (step 5)
            // still tracks browsing done in it.
        }

        // 3) Nothing at all running → launch the real profile with the flag. Works on Chrome <136;
        //    on modern Chrome the port never opens and we clean up before falling back.
        if (_autoLaunch && !BrowserProcessProbe.IsRunning(runtime))
        {
            if (await TryLaunchRealProfileAsync(runtime, port, ct) is { } fresh)
                return fresh;
        }

        // 4) A leftover debug instance of this browser (our dedicated dir, or any chrome with a
        //    debug flag) → attach. Covers tracker restarts and recovery.
        if (!realRunning)
        {
            var leftoverPorts = new List<int>();
            if (debugDir != null)
                leftoverPorts.AddRange(BrowserProcessProbe.FindRemoteDebuggingPorts(
                    debugDir.Replace("\\", "/", StringComparison.Ordinal)));
            if (!string.IsNullOrEmpty(realName))
                leftoverPorts.AddRange(BrowserProcessProbe.FindRemoteDebuggingPorts(realName));
            if (await TryAttachFirstRespondingAsync(runtime, leftoverPorts, ct) is { } leftover)
                return leftover;
        }

        // 5) Dedicated debug instance (separate profile) — works on every Chrome version. The
        //    employee's real browser is left untouched. Last resort.
        if (_autoLaunch && !await PortRespondsAsync(port, ct))
        {
            _logger.LogInformation(
                "Launching a dedicated debug instance for {Runtime} (separate profile) — " +
                "browsing in this instance is tracked; the employee's real browser is left untouched.",
                runtime.BinaryName);
            if (TryLaunch(runtime, port, debugDir))
            {
                if (await WaitForPortAsync(port, TimeSpan.FromSeconds(12), ct))
                {
                    runtime.DebugPort = port;
                    return await OpenConnectionAsync(runtime, port, ct);
                }
            }
        }
        return null;
    }

    /// <summary>Kill the running real browser (preserving our dedicated instance) and relaunch the
    /// SAME real profile with the debug flag. Returns a connection when the port opens. Once a
    /// runtime proves un-relaunchable (modern Chrome), it is added to <see cref="_realProfileBlocked"/>
    /// so we never kill the employee's browser again for zero tracking gain.</summary>
    private async Task<Abstractions.IBrowserConnection?> TryHijackRealProfileAsync(
        DetectedBrowserRuntime runtime, int port, string? debugDir, CancellationToken ct)
    {
        if (_realProfileBlocked.Contains(runtime.Id)) return null;

        var now = DateTime.UtcNow;
        if (_lastHijack.TryGetValue(runtime.Id, out var last) && now - last <= _hijackCooldown)
            return null;
        _lastHijack[runtime.Id] = now;

        if (!BrowserProcessProbe.IsRunning(runtime, excludeCmdlineToken: debugDir)) return null;

        _logger.LogWarning(
            "{Runtime} is running without a debugger — relaunching it with remote debugging on " +
            "its real profile so its browsing is tracked (permission granted at install). The " +
            "browser closes and re-opens automatically; unsaved form data is lost once.",
            runtime.BinaryName);
        BrowserProcessProbe.TerminateInstances(runtime, preserveCmdlineToken: debugDir);
        await WaitForExitAsync(runtime, debugDir, TimeSpan.FromSeconds(8), ct);
        // On a failed relaunch the just-reopened browser IS the employee's browser (tabs
        // restored) — never kill it a second time; the dedicated instance takes over tracking.
        return await TryLaunchRealProfileAsync(runtime, port, ct, terminateStrayOnFailure: false);
    }

    /// <summary>Launch the browser on its REAL profile with the debug flag. On modern Chrome the
    /// flag is ignored on the default profile — the port never opens. The runtime is then marked
    /// <see cref="_realProfileBlocked"/> and (optionally) the stray we launched is cleaned up.
    /// When <paramref name="terminateStrayOnFailure"/> is false the just-launched browser is left
    /// running — it is the employee's browser and must not be killed twice.</summary>
    private async Task<Abstractions.IBrowserConnection?> TryLaunchRealProfileAsync(
        DetectedBrowserRuntime runtime, int port, CancellationToken ct, bool terminateStrayOnFailure = true)
    {
        if (!TryLaunch(runtime, port, runtime.UserDataDir)) return null;
        if (await WaitForPortAsync(port, TimeSpan.FromSeconds(15), ct))
        {
            runtime.DebugPort = port;
            return await OpenConnectionAsync(runtime, port, ct);
        }
        _realProfileBlocked.Add(runtime.Id);
        _logger.LogInformation(
            "{Runtime} real-profile launch did not expose a debug port (modern Chrome blocks " +
            "--remote-debugging-port on the default profile) — the real profile is not " +
            "relaunchable this run; falling back to the dedicated debug instance.",
            runtime.BinaryName);
        if (terminateStrayOnFailure)
        {
            BrowserProcessProbe.TerminateInstances(runtime, preserveCmdlineToken: DebugDirFor(runtime));
            await Task.Delay(1000, ct);
        }
        return null;
    }

    /// <summary>Attach to the first candidate port that responds to the DevTools version probe.</summary>
    private async Task<Abstractions.IBrowserConnection?> TryAttachFirstRespondingAsync(
        DetectedBrowserRuntime runtime, IEnumerable<int> ports, CancellationToken ct)
    {
        foreach (var candidate in ports.Distinct())
        {
            if (!await PortRespondsAsync(candidate, ct)) continue;
            _logger.LogInformation(
                "Attached to running debug instance for {Runtime} on port {Port}",
                runtime.BinaryName, candidate);
            runtime.DebugPort = candidate;
            return await OpenConnectionAsync(runtime, candidate, ct);
        }
        return null;
    }

    /// <summary>Wait (polling) until the real browser process is gone, ignoring our dedicated instance.</summary>
    private static async Task WaitForExitAsync(
        DetectedBrowserRuntime runtime, string? excludeToken, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!BrowserProcessProbe.IsRunning(runtime, excludeCmdlineToken: excludeToken)) return;
            await Task.Delay(250, ct);
        }
    }

    private async Task<Abstractions.IBrowserConnection?> OpenConnectionAsync(
        DetectedBrowserRuntime runtime, int port, CancellationToken ct)
    {
        try
        {
            var version = await _http.GetFromJsonAsync<JsonElement>(
                $"http://127.0.0.1:{port}/json/version", ct);
            var wsUrl = version.TryGetProperty("webSocketDebuggerUrl", out var ws)
                ? ws.GetString()
                : null;
            if (string.IsNullOrEmpty(wsUrl))
                return null;

            runtime.Version ??= version.TryGetProperty("Browser", out var b) ? b.GetString() : null;
            var conn = new ChromiumConnection(runtime, wsUrl!, port, _logger);
            await conn.StartAsync(ct);
            return conn;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open CDP connection on port {Port}", port);
            return null;
        }
    }

    // ── helpers ──

    private static async Task<bool> PortRespondsAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await client.GetAsync($"http://127.0.0.1:{port}/json/version", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static bool TryReadActivePort(string path, out int port)
    {
        port = 0;
        try
        {
            var first = File.ReadLines(path).FirstOrDefault();
            return int.TryParse(first, out port) && port > 0;
        }
        catch { return false; }
    }

    private static string? DebugDirFor(DetectedBrowserRuntime runtime) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "alpha-ai-tracker", "browser-debug", runtime.Id.ToString("N"));

    private async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await PortRespondsAsync(port, ct)) return true;
            await Task.Delay(500, ct);
        }
        return false;
    }

    private string? ResolveBinaryPath(Models.InstalledApplication app)
    {
        if (!string.IsNullOrWhiteSpace(app.InstallPath))
        {
            var path = app.InstallPath.Trim().Trim('"');
            if (File.Exists(path)) return path;
            if (path.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
            {
                var exec = ExtractDesktopExec(path);
                if (exec != null)
                {
                    var resolved = ResolveInPath(exec);
                    if (resolved != null) return resolved;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(app.BinaryName))
        {
            var inPath = ResolveInPath(app.BinaryName);
            if (inPath != null) return inPath;
        }

        return null;
    }

    private static string? ExtractDesktopExec(string desktopPath)
    {
        try
        {
            foreach (var line in File.ReadLines(desktopPath))
            {
                if (line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line[5..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length > 0 ? Path.GetFileName(parts[0]) : null;
                }
            }
        }
        catch { }
        return null;
    }

    private string? ResolveInPath(string binaryName)
    {
        if (string.IsNullOrWhiteSpace(binaryName)) return null;
        var cmd = OperatingSystem.IsWindows() ? "where" : "which";
        try
        {
            var psi = new ProcessStartInfo(cmd, binaryName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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
        return null;
    }

    private string? ResolveUserDataDir(Models.InstalledApplication app, string binaryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var baseName = Path.GetFileNameWithoutExtension(binaryPath).ToLowerInvariant();
            foreach (var dir in Directory.EnumerateDirectories(local))
            {
                if (Directory.Exists(Path.Combine(dir, "User Data")))
                    return Path.Combine(dir, "User Data");
            }
            _ = baseName;
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            var config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
            foreach (var dir in Directory.EnumerateDirectories(config))
            {
                if (Directory.Exists(Path.Combine(dir, "Default")))
                    return Path.Combine(dir, "Default");
            }
            return null;
        }

        // Linux: scan ~/.config for Chromium-family profile dirs (Local State + Default present).
        var configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        if (!Directory.Exists(configDir)) return null;
        var candidates = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(configDir))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "Local State")) && Directory.Exists(Path.Combine(dir, "Default")))
                    candidates.Add(dir);
            }
            catch { }
        }
        if (candidates.Count == 0) return null;

        var binToken = Path.GetFileNameWithoutExtension(binaryPath).ToLowerInvariant();
        foreach (var dir in candidates)
        {
            var name = Path.GetFileName(dir).ToLowerInvariant();
            if (name.Contains(binToken, StringComparison.OrdinalIgnoreCase)) return dir;
        }
        // Try matching the installed app display name tokens (e.g. "Google Chrome" → google-chrome).
        var appName = app.AppName.ToLowerInvariant();
        foreach (var dir in candidates)
        {
            var name = Path.GetFileName(dir).ToLowerInvariant();
            if (appName.Contains(name, StringComparison.OrdinalIgnoreCase)) return dir;
        }
        return candidates[0];
    }

    private bool TryLaunch(DetectedBrowserRuntime runtime, int port, string? userDataDir)
    {
        if (string.IsNullOrEmpty(runtime.BinaryPath)) return false;
        try
        {
            if (!string.IsNullOrEmpty(userDataDir))
                Directory.CreateDirectory(userDataDir);

            var args = new List<string>
            {
                $"--remote-debugging-port={port}",
            };
            if (!string.IsNullOrEmpty(userDataDir))
                args.Add($"--user-data-dir={Quote(userDataDir)}");
            args.Add("--no-first-run");
            args.Add("--no-default-browser-check");
            args.Add("--new-window");
            args.Add("about:blank");

            var psi = new ProcessStartInfo(runtime.BinaryPath, string.Join(' ', args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            _logger.LogInformation("Launched {Runtime} on port {Port} (user-data-dir={Dir})",
                runtime.BinaryName, port, userDataDir ?? "(default)");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to launch {Runtime}", runtime.BinaryName);
            return false;
        }
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    internal static bool IsProcessRunning(DetectedBrowserRuntime runtime)
    {
        if (string.IsNullOrEmpty(runtime.BinaryPath)) return false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var name = Path.GetFileNameWithoutExtension(runtime.BinaryPath);
                return Process.GetProcessesByName(name).Length > 0;
            }

            var psi = new ProcessStartInfo("pgrep", "-f")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(Path.GetFileName(runtime.BinaryPath));
            using var p = Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            return output.Length > 0;
        }
        catch { return false; }
    }
}
