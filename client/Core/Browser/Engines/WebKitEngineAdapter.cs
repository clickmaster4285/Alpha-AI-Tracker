using client.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace client.Core.Browser.Engines;

/// <summary>
/// WebKit engine adapter (Safari on macOS, WebKitGTK/GNOME Web on Linux).
/// Protocol: Web Inspector Protocol. Safari requires admin "Allow Remote Automation";
/// WebKitGTK requires one-time developer-extras enablement. Until that happens this
/// adapter reports ManualSetupRequired and produces NO data (never fabricated).
/// </summary>
public sealed class WebKitEngineAdapter : Abstractions.IBrowserEngineAdapter
{
    private readonly IInstalledAppDetector _appDetector;
    private readonly ILogger _logger;

    public WebKitEngineAdapter(IInstalledAppDetector appDetector, ILogger<WebKitEngineAdapter> logger)
    {
        _appDetector = appDetector;
        _logger = logger;
    }

    public BrowserEngine Engine => BrowserEngine.WebKit;

    public Task<IReadOnlyList<DetectedBrowserRuntime>> DetectRuntimesAsync(CancellationToken ct)
    {
        var result = new List<DetectedBrowserRuntime>();
        foreach (var app in _appDetector.GetAllInstalledApplications().Where(a => a.IsBrowser))
        {
            try
            {
                var isWebKit = IsWebKitBinary(app.BinaryName) || IsWebKitBundle(app);
                if (!isWebKit) continue;

                var runtime = new DetectedBrowserRuntime
                {
                    Engine = BrowserEngine.WebKit,
                    BinaryName = string.IsNullOrWhiteSpace(app.BinaryName)
                        ? Path.GetFileNameWithoutExtension(app.InstallPath)
                        : app.BinaryName,
                    BinaryPath = string.IsNullOrWhiteSpace(app.InstallPath) ? null : app.InstallPath.Trim().Trim('"'),
                    DisplayName = app.AppName,
                    InstalledAppId = app.Id,
                    UserDataDir = ResolveProfileDir(app),
                    Version = string.IsNullOrWhiteSpace(app.AppVersion) ? null : app.AppVersion,
                };
                runtime.Capabilities = ProbeCapabilities(runtime);
                result.Add(runtime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebKit detection failed for {App}", app.AppName);
            }
        }
        return Task.FromResult<IReadOnlyList<DetectedBrowserRuntime>>(result);
    }

    public BrowserCapabilities ProbeCapabilities(DetectedBrowserRuntime runtime)
    {
        var isSafari = runtime.BinaryName.Contains("safari", StringComparison.OrdinalIgnoreCase);
        return new BrowserCapabilities
        {
            Capabilities = new()
            {
                [Capability.Debugger] = isSafari
                    ? CapabilityClassification.AdminAssisted   // safaridriver needs manual enable
                    : CapabilityClassification.UserAssisted,   // WebKitGTK developer-extras toggle
                [Capability.Profiles] = isSafari
                    ? CapabilityClassification.Unsupported      // Safari single profile surface
                    : CapabilityClassification.Automatic,
                [Capability.NetworkEvents] = CapabilityClassification.Automatic,
                [Capability.History] = CapabilityClassification.Automatic,
                [Capability.IncognitoDetection] = CapabilityClassification.Unsupported,
                [Capability.DomInspection] = CapabilityClassification.Automatic,
            }
        };
    }

    public IReadOnlyList<BrowserProfileInfo> DiscoverProfiles(DetectedBrowserRuntime runtime)
    {
        // WebKitGTK stores history in a single profile dir; Safari uses one profile dir.
        if (!string.IsNullOrEmpty(runtime.UserDataDir))
            return new[] { new BrowserProfileInfo { Name = "Default", ProfileDir = runtime.UserDataDir } };
        return Array.Empty<BrowserProfileInfo>();
    }

    public Task<Abstractions.IBrowserConnection?> LaunchAndConnectAsync(
        DetectedBrowserRuntime runtime, int port, CancellationToken ct)
    {
        _logger.LogWarning(
            "WebKit debugger requires manual setup ({Runtime}). "
            + "Safari: enable 'Allow Remote Automation' (safaridriver). "
            + "WebKitGTK: enable developer extras. No journey data until then — never fabricated.",
            runtime.BinaryName);
        return Task.FromResult<Abstractions.IBrowserConnection?>(null);
    }

    private static bool IsWebKitBinary(string? binaryName)
    {
        if (string.IsNullOrWhiteSpace(binaryName)) return false;
        var name = binaryName.ToLowerInvariant();
        return name.Contains("epiphany", StringComparison.Ordinal)
            || name.Contains("webkit", StringComparison.Ordinal)
            || name.Contains("safari", StringComparison.Ordinal)
            || name.Contains("gnome-web", StringComparison.Ordinal);
    }

    private static bool IsWebKitBundle(Models.InstalledApplication app)
    {
        if (OperatingSystem.IsMacOS())
        {
            var bundleId = app.Categories ?? string.Empty;
            return bundleId.Contains("com.apple.Safari", StringComparison.OrdinalIgnoreCase);
        }
        if (OperatingSystem.IsLinux())
        {
            var desktopId = app.DesktopId ?? string.Empty;
            return desktopId.Contains("epiphany", StringComparison.OrdinalIgnoreCase)
                || desktopId.Contains("org.gnome.Epiphany", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string? ResolveProfileDir(Models.InstalledApplication app)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            var dir = Path.Combine(home, "Library", "Safari");
            return Directory.Exists(dir) ? dir : null;
        }
        if (OperatingSystem.IsLinux())
        {
            // WebKitGTK per-app profile dirs.
            var config = Path.Combine(home, ".config");
            if (!Directory.Exists(config)) return null;
            foreach (var dir in Directory.EnumerateDirectories(config))
            {
                var name = Path.GetFileName(dir);
                if (name.Contains("epiphany", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("gnome-web", StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }
        return null;
    }
}
