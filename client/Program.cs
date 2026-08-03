using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
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
        return 1;
    }

    Console.WriteLine($"Encrypting {inputPath} → {outputPath} (AES-256-GCM)...");
    EnvLoader.EncryptToFile(inputPath, outputPath);
    Console.WriteLine($"Done: {new FileInfo(outputPath).Length} bytes written.");
    return 0;
}

// ─── Native messaging host mode (pure C#, Phase 1) ───
// The browser spawns THIS executable (the manifest `path` points at the main
// binary) with a single identifying argument. Detect host mode when ANY of:
//   (a) argv[1] starts with "chrome-extension://"  — Chromium family
//   (b) argv[1] exactly equals the Gecko application id from
//       extensions/firefox/manifest.json browser_specific_settings.gecko.id —
//       Firefox invokes the host with the BARE id, no URL-scheme prefix
//   (c) the explicit --native-host flag (manual/dev invocation)
// Host mode ONLY does stdio ⇄ socket forwarding and exits cleanly — it must
// NEVER touch the mutex, config, DI, SQLite, or Avalonia.
if (args.Contains("--native-host") ||
    (args.Length > 0 && IsNativeHostInvocation(args[0])))
{
    return NativeMessagingHost.Run();
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
    return 0;
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
    var dbPath = config.DbPath;
    if (!Path.IsPathRooted(dbPath))
    {
        var baseDir = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlphaAITracker")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "alpha-ai-tracker");
        dbPath = Path.Combine(baseDir, dbPath);
    }
    return new SqliteLogStore(dbPath, config.DbEncryptionKey);
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

// Native Messaging (browser extension bridge — Unix socket for tab/URL capture)
builder.Services.AddSingleton<NativeMessageService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NativeMessageService>());
builder.Services.AddSingleton<BrowserExtensionService>();

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

return 0;

static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();

// ─── Native-host mode invocation detection ───
// Matches the identifying argv[1] the browser appends when it spawns the host:
//   (a) Chromium:  chrome-extension://<id>/
//   (b) Firefox:   the bare Gecko application id (no prefix)
// The explicit --native-host flag is handled separately in the main flow.
static bool IsNativeHostInvocation(string firstArg) =>
    firstArg.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(firstArg, NativeMessagingPaths.GeckoApplicationId, StringComparison.Ordinal);

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
