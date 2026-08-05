using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Browser;
using client.Core.Browser.Abstractions;
using client.Core.Browser.Engines;
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

// Platform-specific services (IShellCommandCollector removed per user request)
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

// Browser Journey Engine (debugger-only: CDP / RDP+BiDi / WebKit inspector)
// Chain (aiplan.txt §29): Runtime → Coordinator → JourneyEngine → Store
var dbPath = ResolveDbPath(config.DbPath);
if (config.BrowserTrackingEnabled)
{
    builder.Services.AddSingleton<BrowserRuntimeStateStore>();
    builder.Services.AddSingleton<IBrowserRuntimeStore>(sp => sp.GetRequiredService<BrowserRuntimeStateStore>());
    builder.Services.AddSingleton<BrowserEventCoordinator>(sp => new BrowserEventCoordinator(
        id => sp.GetRequiredService<BrowserRuntimeManager>().Lookup(id),
        config.BrowserCoordinatorDedupSeconds));
    builder.Services.AddSingleton<BrowserConnectionManager>();
    builder.Services.AddSingleton<DebugPortManager>(sp => new DebugPortManager(
        config.BrowserDebugPortStart, sp.GetRequiredService<IBrowserRuntimeStore>()));
    builder.Services.AddSingleton<BrowserRuntimeManager>(sp =>
    {
        var cfg = sp.GetRequiredService<AppConfig>();
        var manager = new BrowserRuntimeManager(
            cfg,
            new IBrowserEngineAdapter[]
            {
                new ChromiumEngineAdapter(sp.GetRequiredService<IInstalledAppDetector>(),
                    cfg.BrowserAutoLaunch,
                    TimeSpan.FromMinutes(cfg.BrowserHijackCooldownMinutes),
                    sp.GetRequiredService<ILogger<ChromiumEngineAdapter>>()),
                new GeckoEngineAdapter(sp.GetRequiredService<IInstalledAppDetector>(),
                    cfg.BrowserAutoLaunch,
                    TimeSpan.FromMinutes(cfg.BrowserHijackCooldownMinutes),
                    sp.GetRequiredService<ILogger<GeckoEngineAdapter>>()),
                new WebKitEngineAdapter(sp.GetRequiredService<IInstalledAppDetector>(),
                    sp.GetRequiredService<ILogger<WebKitEngineAdapter>>()),
            },
            sp.GetRequiredService<IBrowserRuntimeStore>(),
            sp.GetRequiredService<BrowserEventCoordinator>(),
            sp.GetRequiredService<BrowserConnectionManager>(),
            sp.GetRequiredService<DebugPortManager>(),
            sp.GetRequiredService<ILogger<BrowserRuntimeManager>>());
        return manager;
    });
    builder.Services.AddSingleton<BrowserJourneyEngine>();
    builder.Services.AddHostedService(sp => new BrowserRuntimeHostedService(
        sp.GetRequiredService<BrowserRuntimeManager>(),
        sp.GetRequiredService<BrowserJourneyEngine>(),
        sp.GetRequiredService<BrowserEventCoordinator>(),
        sp.GetRequiredService<BrowserRuntimeStateStore>(),
        dbPath,
        sp.GetRequiredService<ILogger<BrowserRuntimeHostedService>>()));
    builder.Services.AddHostedService<BrowserWatchdogService>();
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

    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

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
