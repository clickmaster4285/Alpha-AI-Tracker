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
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly TimeSpan HijackCooldown = TimeSpan.FromMinutes(5);
    private readonly Dictionary<Guid, DateTime> _lastHijack = new();

    public ChromiumEngineAdapter(
        IInstalledAppDetector appDetector,
        bool autoLaunch,
        ILogger<ChromiumEngineAdapter> logger)
    {
        _appDetector = appDetector;
        _autoLaunch = autoLaunch;
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
        // 1) Already debugging on this port?
        if (await PortRespondsAsync(port, ct))
            return await OpenConnectionAsync(runtime, port, ct);

        // 2) Debugger active port file in the real profile dir?
        if (runtime.UserDataDir != null)
        {
            var activePortFile = Path.Combine(runtime.UserDataDir, "DevToolsActivePort");
            if (File.Exists(activePortFile) && TryReadActivePort(activePortFile, out var activePort))
            {
                if (await PortRespondsAsync(activePort, ct))
                {
                    runtime.DebugPort = activePort;
                    return await OpenConnectionAsync(runtime, activePort, ct);
                }
            }
        }

        // 2.5) Attach to an already-running debugger instance of this browser: one we
        //      launched on our dedicated debug dir earlier, OR any chrome/chromium running
        //      with --remote-debugging-port (whatever profile it used). Never launches.
        if (!await PortRespondsAsync(port, ct))
        {
            var candidatePorts = new List<int>();
            var debugDir = DebugDirFor(runtime);
            if (debugDir != null)
                candidatePorts.AddRange(
                    BrowserProcessProbe.FindRemoteDebuggingPorts(
                        debugDir.Replace("\\", "/", StringComparison.Ordinal)));
            var realName = BrowserProcessProbe.RealBinaryName(runtime);
            if (!string.IsNullOrEmpty(realName))
                candidatePorts.AddRange(BrowserProcessProbe.FindRemoteDebuggingPorts(realName));

            foreach (var orphanPort in candidatePorts.Distinct())
            {
                if (!await PortRespondsAsync(orphanPort, ct)) continue;
                _logger.LogInformation(
                    "Attached to running debug instance for {Runtime} on port {Port}",
                    runtime.BinaryName, orphanPort);
                runtime.DebugPort = orphanPort;
                return await OpenConnectionAsync(runtime, orphanPort, ct);
            }
        }

        // 3) Not running → launch the real profile with the debug flag. Only when
        //    auto-launch is enabled (default off): the tracker is attach-only, it never
        //    opens a browser by itself.
        if (_autoLaunch && !BrowserProcessProbe.IsRunning(runtime))
        {
            if (TryLaunch(runtime, port, runtime.UserDataDir))
            {
                // The launched instance can still forward to an existing session (ignoring
                // the debug flag). Give it a short window; if the port never opens, fall
                // through to the hijack below instead of giving up.
                if (await WaitForPortAsync(port, TimeSpan.FromSeconds(8), ct))
                {
                    runtime.DebugPort = port;
                    return await OpenConnectionAsync(runtime, port, ct);
                }
            }
        }

        // 4) Running WITHOUT a debugger → the ONLY way to track the employee's real profile
        //    is to terminate the normal instance and relaunch the SAME profile with the debug
        //    flag (a normally-launched browser can't be debugged retroactively). Cooldown
        //    prevents the 10s reconnect loop from killing the browser repeatedly.
        if (_autoLaunch && !await PortRespondsAsync(port, ct))
        {
            var now = DateTime.UtcNow;
            if (!_lastHijack.TryGetValue(runtime.Id, out var last) || now - last > HijackCooldown)
            {
                _lastHijack[runtime.Id] = now;
                if (BrowserProcessProbe.IsRunning(runtime))
                {
                    _logger.LogWarning(
                        "{Runtime} is running without a debugger — relaunching it with remote "
                        + "debugging on the real profile so its browsing is tracked.",
                        runtime.BinaryName);
                    BrowserProcessProbe.TerminateInstances(runtime);
                    await Task.Delay(2000, ct);
                    if (TryLaunch(runtime, port, runtime.UserDataDir))
                    {
                        if (await WaitForPortAsync(port, TimeSpan.FromSeconds(15), ct))
                        {
                            runtime.DebugPort = port;
                            return await OpenConnectionAsync(runtime, port, ct);
                        }
                    }
                }
            }
        }

        // 4b) Fallback when the real profile can't be relaunched: a separate debug instance
        //     on a dedicated profile. Auto-launch only.
        if (_autoLaunch && !await PortRespondsAsync(port, ct))
        {
            _logger.LogWarning(
                "{Runtime} could not be relaunched on its real profile. Launching a separate "
                + "debug instance (dedicated profile) as a fallback.", runtime.BinaryName);
            var dedicatedDir = DebugDirFor(runtime);
            if (TryLaunch(runtime, port, dedicatedDir))
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

            var args = new List<string>();
            args.Add($"--remote-debugging-port={port}");
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
            _logger.LogInformation("Launched {Runtime} on port {Port}", runtime.BinaryName, port);
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
