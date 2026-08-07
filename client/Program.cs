using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.BrowserAccessibility;
using client.Core.DesktopEventBus;
using client.Services;
using client.Services.Watchers;
using client.Storage;
using client.ViewModels;

// ─── CLI modes (checked before anything else) ───
if (args.Contains("--encrypt-config"))
{
    // Build-time encryption: .env → config.enc using transport key
    // Usage: dotnet run --project client -- --encrypt-config [input] [output]
    var inputPath = args.ElementAtOrDefault(1) ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env");
    var outputPath = args.ElementAtOrDefault(2) ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.enc");

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Error: .env not found at {inputPath}");
        return;
    }

    Console.WriteLine($"Encrypting {inputPath} → {outputPath} (AES-256-GCM)...");
    EnvLoader.EncryptToFile(inputPath, outputPath);
    Console.WriteLine($"Done: {new FileInfo(outputPath).Length} bytes written.");
    return;
}

// Headless diagnostic: load config exactly like a normal launch, then print the
// resolved values. Use this to verify which server URL / settings an INSTALLED
// build actually uses (plain `dotnet run` always reads .env, so it can't show
// what config.enc will load). EnvLoader prints the config source it picked, too.
if (args.Contains("--print-config"))
{
    EnvLoader.Load();
    var cfg = AppConfig.FromEnv();
    Console.WriteLine($"ServerUrl={cfg.ServerUrl}");
    Console.WriteLine($"BrowserTrackingEnabled={cfg.BrowserTrackingEnabled}");
    Console.WriteLine($"BrowserAccessibilityPollSec={cfg.BrowserAccessibilityPollSec}");
    Console.WriteLine($"BrowserJourneyIdleMinutes={cfg.BrowserJourneyIdleMinutes}");
    Console.WriteLine($"BrowserCaptureIncognito={cfg.BrowserCaptureIncognito}");
    Console.WriteLine($"BrowserHistoryEnabled={cfg.BrowserHistoryEnabled}");
    Console.WriteLine($"BrowserHistoryPollSec={cfg.BrowserHistoryPollSec}");
    Console.WriteLine($"DbPath={cfg.DbPath}");
    Console.WriteLine($"ApiKeySet={!string.IsNullOrEmpty(cfg.ApiKey)}");
    return;
}

EnvLoader.Load();

var isBackground = args.Contains("--background");
var isMinimized = args.Contains("--minimized");
// A "user launch" is one without --background / --minimized — i.e. the user
// explicitly opened the app (clicked the desktop entry / ran the binary).
// Only user launches should bring an already-running instance to the front;
// background/minimized relaunches (e.g. auto-start firing twice, or systemd
// Restart=always racing) must exit quietly so they never disturb the user.
var isUserLaunch = !isBackground && !isMinimized;

var appMutex = new Mutex(true, "AlphaAITracker", out var mutexCreated);
if (!mutexCreated)
{
    // Another instance already owns the mutex — the persistent background
    // instance launched by auto-start / systemd, or a previously-opened GUI
    // that is hidden in the tray. Instead of exiting silently (which was
    // making the GUI "open and instantly close"), ask that instance to show
    // its window, then exit. This lets the user open the GUI as many times
    // as they want without ever stopping the running background tracker.
    Console.Error.WriteLine("Another instance is already running.");
    if (isUserLaunch)
    {
        SingleInstanceService.SignalExistingInstance();
    }
    return;
}

// We are the primary (mutex-owning) instance: start listening for SHOW
// requests from any future second launch so it can bring our window forward.
SingleInstanceService.StartServer();

var config = AppConfig.FromEnv();

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var logLevel = config.LogLevel?.ToLowerInvariant() switch
{
    "verbose" => LogLevel.Trace,
    "debug"   => LogLevel.Debug,
    "info"    => LogLevel.Information,
    "warn"    => LogLevel.Warning,
    "error"   => LogLevel.Error,
    _         => LogLevel.Information
};
builder.Logging.SetMinimumLevel(logLevel);

// Log file location. In dev (`dotnet run`) the app dir (bin/...) is writable,
// so the log lands next to the build output as before. When installed via the
// .deb, the app dir is /usr/share/alpha-ai-tracker — root-owned and READ-ONLY
// for a normal user. Writing dotnetrunlog.txt there throws
// UnauthorizedAccessException from the FileLoggerProvider ctor, which aborts
// startup before the GUI opens (the "open and instantly close" symptom).
// Probe writability and fall back to the user-writable data dir (the same
// place the SQLite db lives) when the install dir is not writable.
var logFilePath = ResolveLogPath();
builder.Logging.AddFile(logFilePath);

builder.Services.AddSingleton(config);

// SQLite Log Store
builder.Services.AddSingleton<ILogStore>(sp =>
{
    return new SqliteLogStore(ResolveDbPath(config.DbPath), config.DbEncryptionKey);
});

// HTTP Client
builder.Services.AddSingleton<HttpClient>(sp =>
{
    var client = new HttpClient();
    client.Timeout = TimeSpan.FromSeconds(30);
    return client;
});

// Installed App Detector (cross-platform)
builder.Services.AddSingleton<IInstalledAppDetector, InstalledAppDetector>();

// Package Detector (cross-platform — npm, pip, apt, brew, etc.)
builder.Services.AddSingleton<IPackageDetector, PackageDetector>();

// Platform-specific services
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IActivityCollector>(
        _ => new client.Platform.Windows.ProcessCollector(config.ClientId));
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<IActivityCollector>(
        _ => new client.Platform.Linux.ProcessCollector(config.ClientId));
}
else if (OperatingSystem.IsMacOS())
{
    builder.Services.AddSingleton<IActivityCollector>(
        _ => new client.Platform.MacOS.ProcessCollector(config.ClientId));
}
else
{
    throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");
}

// Background services (order matters: guard first for most resilience)
builder.Services.AddSingleton<AutoStartService>();
builder.Services.AddHostedService<BackgroundGuardService>();
builder.Services.AddSingleton<LogCollectorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LogCollectorService>());

// Browser Journey (Option B — accessibility-based: reads the OS accessibility tree.
// No debugger, no extension, no browser catalog dependency; works on every browser
// and every Chrome version, including install→use→uninstall-in-5-min scenarios.)
if (config.BrowserTrackingEnabled)
{
    builder.Services.AddSingleton<IAccessibilityBrowserReader>(sp =>
        AccessibilityBrowserReaderFactory.Create(sp.GetRequiredService<ILoggerFactory>()));
    // Hybrid URL fallback: reads the browser's own profile history DB when the a11y
    // tree cannot expose the omnibox (Linux Chrome 136+ / snap Firefox). No restart,
    // no flag, no extension; works on all platforms and browsers incl. brand-new ones.
    builder.Services.AddSingleton<BrowserHistoryReader>();
    builder.Services.AddHostedService<AccessibilityBrowserTracker>();
}

// Desktop Event Bus (File Explorer tracking via AT-SPI + FileSystemWatcher)
builder.Services.AddSingleton<EventCoordinator>();
builder.Services.AddSingleton<JourneyEngine>();
builder.Services.AddSingleton<ATSPIEventWatcher>();
builder.Services.AddSingleton<FileSystemEventWatcher>();
builder.Services.AddSingleton<RecentFilesWatcher>();
builder.Services.AddHostedService<DesktopEventService>();

// Main ViewModel
builder.Services.AddTransient<MainViewModel>();

var host = builder.Build();

try
{
    await host.StartAsync(CancellationToken.None);

    // Set service provider for App to resolve ViewModels from DI
    App.ServiceProvider = host.Services;

    if (isBackground)
    {
        // Headless service mode (systemd `--background`): run the hosted services
        // only, with no Avalonia/X11 UI. GUI init requires a working X connection
        // (DISPLAY + XAUTHORITY); on Wayland the Xwayland auth file lives at
        // /run/user/<uid>/.mutter-Xwaylandauth.* — NOT ~/.Xauthority — so a unit
        // hardcoding XAUTHORITY=~/.Xauthority made the installed background
        // service crash at startup (XOpenDisplay failed → Avalonia exception).
        // Background mode never needs a window or tray, so skip the UI entirely;
        // the process keeps running until systemd stops it (SIGTERM).
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }
    else
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    await host.StopAsync(CancellationToken.None);
    host.Dispose();
}
finally
{
    SingleInstanceService.StopServer();
    appMutex.Dispose();
}

static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();

// ─── Log path resolution ───
static string ResolveDbPath(string dbPath)
{
    if (Path.IsPathRooted(dbPath)) return dbPath;
    var baseDir = OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlphaAITracker")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "alpha-ai-tracker");
    return Path.Combine(baseDir, dbPath);
}

// ─── Log path resolution ───
// Prefer the app dir (bin/... in dev, so the log sits next to the build
// output). When that dir is not writable (installed .deb → root-owned
// /usr/share/alpha-ai-tracker), use the user-writable data dir instead so
// the FileLoggerProvider never fails to open its file. The FileLoggerProvider
// itself is also defensive — this just picks a good path up front.
static string ResolveLogPath()
{
    var appDir = AppDomain.CurrentDomain.BaseDirectory;
    if (IsDirWritable(appDir))
        return Path.Combine(appDir, "dotnetrunlog.txt");

    var userDataDir = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlphaAITracker")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "alpha-ai-tracker");
    return Path.Combine(userDataDir, "dotnetrunlog.txt");
}

static bool IsDirWritable(string dir)
{
    try
    {
        if (string.IsNullOrEmpty(dir)) return false;
        // Create + delete a throwaway marker file. This is the only reliable
        // cross-platform writability test (ACLs / root ownership / read-only
        // mounts all surface here without actually touching the real log file).
        var marker = Path.Combine(dir, ".aat_write_probe_" + Guid.NewGuid().ToString("N"));
        using (new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { }
        try { File.Delete(marker); } catch { }
        return true;
    }
    catch
    {
        return false;
    }
}
