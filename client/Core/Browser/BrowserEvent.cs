using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace client.Core.Browser;

public enum BrowserEngine
{
    Unknown,
    Chromium,
    Gecko,
    WebKit
}

public enum BrowserEventSource
{
    Cdp,
    GeckoRdp,
    WebKitInspector
}

public enum BrowserEventAction
{
    Created,
    Activated,
    Deactivated,
    Navigated,
    Updated,
    Closed,
    Reloaded,
    Error,
    Ping
}

public enum UrlReliability
{
    Exact,
    Unknown
}

/// <summary>
/// A capability an engine may or may not provide. Probed at runtime — never assumed.
/// </summary>
public enum Capability
{
    Debugger,
    Profiles,
    History,
    IncognitoDetection,
    NetworkEvents,
    DomInspection
}

/// <summary>
/// How a capability becomes available. Drives GUI behavior and recovery tiers.
/// </summary>
public enum CapabilityClassification
{
    Automatic,
    AdminAssisted,
    UserAssisted,
    Unsupported
}

/// <summary>
/// Per-runtime state machine states (see aiplan.txt §11).
/// </summary>
public enum BrowserRuntimeState
{
    Undetected,
    Detected,
    Managed,
    Running,
    DebuggerRequested,
    DebuggerConnected,
    JourneyActive,
    Disconnected,
    Recovery,
    ManagedStopped,
    Unsupported,
    ManualSetupRequired
}

/// <summary>
/// Engine-shaped payload from the wire (CDP params, RDP params, WIP payload).
/// Normalizers convert this to a canonical <see cref="BrowserEvent"/>.
/// </summary>
public sealed class RawBrowserEvent
{
    public BrowserEngine Engine { get; set; } = BrowserEngine.Unknown;
    public BrowserEventSource Source { get; set; }
    public string? RuntimeId { get; set; }
    public string? ProfileId { get; set; }
    public string? WindowId { get; set; }
    /// <summary>Engine-native tab/target identifier.</summary>
    public string? TabId { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }
    public string? Action { get; set; }
    public bool? Incognito { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Canonical, engine-agnostic event emitted by every adapter. Downstream never
/// knows the source. All identity is UUID, never titles/names/PIDs.
/// </summary>
public sealed class BrowserEvent
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public Guid RuntimeId { get; set; }
    public BrowserEngine Engine { get; set; } = BrowserEngine.Unknown;
    public Guid ProfileId { get; set; }
    public Guid? WindowId { get; set; }
    public Guid TabId { get; set; }
    public Guid JourneyId { get; set; }
    public BrowserEventAction Action { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }
    public string? Domain { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public BrowserEventSource Source { get; set; }
    public bool? Incognito { get; set; }
    public UrlReliability UrlReliability { get; set; } = UrlReliability.Exact;
    public Dictionary<string, string> Metadata { get; set; } = new();

    public string ToMetadataJson() =>
        JsonSerializer.Serialize(new
        {
            source = Source.ToString(),
            engine = Engine.ToString(),
            profileId = ProfileId.ToString("N"),
            windowId = WindowId?.ToString("N"),
            tabId = TabId.ToString("N"),
            journeyId = JourneyId.ToString("N"),
            incognito = Incognito,
            urlReliability = UrlReliability.ToString(),
            title = Title,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}

/// <summary>
/// Result of ProbeCapabilities() — what a specific runtime can actually do,
/// classified per capability. The runtime adapts to this, never to brand names.
/// </summary>
public sealed class BrowserCapabilities
{
    public Dictionary<Capability, CapabilityClassification> Capabilities { get; set; } = new();

    public CapabilityClassification this[Capability c] =>
        Capabilities.TryGetValue(c, out var v) ? v : CapabilityClassification.Unsupported;

    public bool Supports(Capability c) =>
        Capabilities.TryGetValue(c, out var v) && v != CapabilityClassification.Unsupported;
}

/// <summary>A browser profile discovered on disk (Local State / profiles.ini / WebKit dir).</summary>
public sealed class BrowserProfileInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? ProfileDir { get; set; }
}

/// <summary>A snapshot of a currently-open tab queried from the engine.</summary>
public sealed class BrowserTabSnapshot
{
    public string TargetId { get; set; } = string.Empty;
    public string? WindowId { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public bool? Incognito { get; set; }
    public string? ProfileId { get; set; }
}

/// <summary>
/// A detected browser runtime (binary identity). One per installed browser binary,
/// e.g. Chrome Stable, Edge, Brave, Firefox, GNOME Web. UUID persisted across reboots.
/// </summary>
public sealed class DetectedBrowserRuntime
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public BrowserEngine Engine { get; set; } = BrowserEngine.Unknown;
    public string BinaryName { get; set; } = string.Empty;
    public string? BinaryPath { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? InstalledAppId { get; set; }
    public string? UserDataDir { get; set; }
    public string? Version { get; set; }
    public List<BrowserProfileInfo> Profiles { get; set; } = new();
    public BrowserRuntimeState State { get; set; } = BrowserRuntimeState.Undetected;
    public int? DebugPort { get; set; }
    public BrowserCapabilities Capabilities { get; set; } = new();
    public bool IsRunning { get; set; }
    public DateTime? LastSeenAt { get; set; }
}

/// <summary>
/// Classifies an installed browser binary into its engine family by generic tokens in the
/// binary name/path (never exact brand lists). The OS metadata (Categories/MIME) only tells
/// us "this is a browser", not which engine — so adapters gate detection on this.
/// </summary>
public static class BrowserEngineFamily
{
    private static readonly string[] ChromiumTokens =
    {
        "chrom", "chrome", "brave", "edge", "msedge", "opera", "vivaldi", "arc", "yandex",
        "slimjet", "electron", "chromium", "avast", "iron", "centbrowser", "orbitum",
    };

    private static readonly string[] GeckoTokens =
    {
        "firefox", "waterfox", "librewolf", "palemoon", "icecat", "seamonkey", "floorp", "gecko",
    };

    private static readonly string[] WebKitTokens =
    {
        "epiphany", "gnome-web", "webkit", "safari", "konqueror", "midori",
    };

    public static BrowserEngine Classify(string? binaryName)
    {
        var name = (binaryName ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name)) return BrowserEngine.Unknown;

        foreach (var t in GeckoTokens)
            if (name.Contains(t)) return BrowserEngine.Gecko;
        foreach (var t in WebKitTokens)
            if (name.Contains(t)) return BrowserEngine.WebKit;
        foreach (var t in ChromiumTokens)
            if (name.Contains(t)) return BrowserEngine.Chromium;
        return BrowserEngine.Unknown;
    }
}

/// <summary>Shared process-alive probe for the watchdog/state machine.</summary>
public static class BrowserProcessProbe
{
    public static bool IsRunning(DetectedBrowserRuntime runtime)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var baseName = Path.GetFileNameWithoutExtension(runtime.BinaryPath ?? runtime.BinaryName);
                if (Process.GetProcessesByName(baseName).Length > 0) return true;
                return Process.GetProcessesByName(runtime.BinaryName).Length > 0;
            }

            // Resolve the real binary through symlinks (google-chrome-stable → chrome) so
            // we match the actual process image name, not the desktop wrapper name.
            var realName = ResolveRealBinary(runtime.BinaryPath);
            foreach (var p in Process.GetProcesses())
            {
                var name = p.ProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (string.Equals(name, runtime.BinaryName, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(name, realName, StringComparison.OrdinalIgnoreCase)) return true;
                // ProcessName may carry a wrapper suffix or the real name may be a prefix
                // (chrome vs chrome_crashpad_handler); match by token containment.
                if (realName != null && name.Contains(realName, StringComparison.OrdinalIgnoreCase)) return true;
                if (name.Contains(runtime.BinaryName, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    private static string? ResolveRealBinary(string? binaryPath)
    {
        var wrapper = Path.GetFileName(binaryPath).ToLowerInvariant();
        try
        {
            var info = new FileInfo(binaryPath);
            var target = info.ResolveLinkTarget(true);
            var resolved = target?.FullName ?? binaryPath;
            var name = Path.GetFileName(resolved);
            // The symlink-resolved basename may still be a desktop wrapper (e.g. /opt/google/chrome/google-chrome
            // resolving to the shell wrapper, whose real image is `chrome`). Always run the wrapper→core map so
            // the probe matches the actual process image name (p.ProcessName), not the wrapper's argv name.
            if (!string.IsNullOrWhiteSpace(name))
            {
                foreach (var (prefix, core) in WrapperCoreMap())
                    if (name.Contains(prefix, StringComparison.OrdinalIgnoreCase)) return core;
                return name;
            }
        }
        catch { }

        // Fallback: map known desktop-wrapper names to the real process image name.
        foreach (var (prefix, core) in WrapperCoreMap())
        {
            if (wrapper.Contains(prefix)) return core;
        }
        return Path.GetFileName(binaryPath);
    }

    private static IEnumerable<(string Prefix, string Core)> WrapperCoreMap()
    {
        yield return ("google-chrome", "chrome");
        yield return ("chromium", "chromium");
        yield return ("mozilla-firefox", "firefox");
        yield return ("firefox", "firefox");
    }

    /// <summary>
    /// Find a running debugger port for a process whose command line contains the given
    /// token (e.g. our dedicated debug dir, or the binary name). Used to recover a debug
    /// instance left behind by a previous client run instead of launching into a locked
    /// profile dir. Returns 0 when none found.
    /// </summary>
    public static int FindRemoteDebuggingPort(string cmdlineToken) =>
        FindRemoteDebuggingPorts(cmdlineToken).FirstOrDefault();

    /// <summary>All debugger ports advertised by running processes whose cmdline contains the token.</summary>
    public static IEnumerable<int> FindRemoteDebuggingPorts(string cmdlineToken)
    {
        foreach (var cmdline in EnumerateProcessCmdlines())
        {
            if (!cmdline.Contains(cmdlineToken, StringComparison.OrdinalIgnoreCase)) continue;
            var match = Regex.Match(cmdline, @"--remote-debugging-port[= ](\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var port) && port > 0)
                yield return port;
        }
    }

    /// <summary>The real process image name for a runtime (e.g. "chrome"), used as a token
    /// to attach to ANY running instance of that browser, whatever profile it launched with.</summary>
    public static string? RealBinaryName(DetectedBrowserRuntime runtime) =>
        ResolveRealBinary(runtime.BinaryPath) ?? runtime.BinaryName;

    /// <summary>
    /// Terminate all running instances of a browser binary (SIGTERM then SIGKILL on
    /// Unix, WM_CLOSE then force on Windows) so the real profile can be relaunched with
    /// a debug flag. Returns the number of processes that matched. Snap-confined processes
    /// (Permission denied) are skipped gracefully.
    /// </summary>
    public static int TerminateInstances(DetectedBrowserRuntime runtime)
    {
        var realName = ResolveRealBinary(runtime.BinaryPath);
        var targets = new List<Process>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                var name = p.ProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var match =
                    string.Equals(name, runtime.BinaryName, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(realName) && string.Equals(name, realName, StringComparison.OrdinalIgnoreCase)) ||
                    (realName != null && name.Contains(realName, StringComparison.OrdinalIgnoreCase)) ||
                    name.Contains(runtime.BinaryName, StringComparison.OrdinalIgnoreCase);
                if (match) targets.Add(p);
            }
        }
        catch { }

        if (targets.Count == 0) return 0;

        if (OperatingSystem.IsWindows())
        {
            foreach (var p in targets)
            {
                try { p.CloseMainWindow(); } catch { }
            }
            Thread.Sleep(800);
            foreach (var p in targets)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
            }
            return targets.Count;
        }

        // Unix: graceful SIGTERM first (lets the browser persist its session), then force.
        foreach (var p in targets)
        {
            try { RunKill("-15", p.Id); } catch { }
        }
        Thread.Sleep(1500);
        foreach (var p in targets)
        {
            try
            {
                if (!p.HasExited) RunKill("-9", p.Id);
            }
            catch { }
        }
        return targets.Count;
    }

    private static void RunKill(string signal, int pid)
    {
        var psi = new ProcessStartInfo("kill", $"{signal} {pid}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p != null) p.WaitForExit(3000);
    }

    public static IEnumerable<string> EnumerateProcessCmdlines()
    {
        var result = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var psi = new ProcessStartInfo("powershell", "-NoProfile -Command "
                    + "\"Get-CimInstance Win32_Process | Select-Object -ExpandProperty CommandLine\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    foreach (var line in output.Split('\n'))
                        result.Add(line.Trim());
                }
            }
            catch { }
            return result;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var psi = new ProcessStartInfo("ps", "-axo args=")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    foreach (var line in output.Split('\n'))
                        result.Add(line.Trim());
                }
            }
            catch { }
            return result;
        }

        // Linux: read /proc/<pid>/cmdline (NUL-separated args).
        foreach (var pidDir in Directory.EnumerateDirectories("/proc"))
        {
            var pid = Path.GetFileName(pidDir);
            if (!pid.All(char.IsAsciiDigit)) continue;
            try
            {
                var bytes = File.ReadAllBytes(Path.Combine(pidDir, "cmdline"));
                if (bytes.Length == 0) continue;
                var args = new List<string>();
                var start = 0;
                for (var i = 0; i <= bytes.Length; i++)
                {
                    if (i == bytes.Length || bytes[i] == 0)
                    {
                        if (i > start)
                            args.Add(System.Text.Encoding.UTF8.GetString(bytes, start, i - start));
                        start = i + 1;
                    }
                }
                if (args.Count > 0)
                    result.Add(string.Join(' ', args));
            }
            catch { }
        }
        return result;
    }
}
