using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Services;
using client.Storage;
using client.ViewModels;

EnvLoader.Load();

var isBackground = args.Contains("--background");

var appMutex = new Mutex(true, "AlphaAITracker", out var mutexCreated);
if (!mutexCreated)
{
    Console.Error.WriteLine("Another instance is already running.");
    if (!isBackground) Console.ReadKey();
    return;
}

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
    client.Timeout = TimeSpan.FromSeconds(10);
    return client;
});

// Installed App Detector (cross-platform)
builder.Services.AddSingleton<IInstalledAppDetector, InstalledAppDetector>();

// Platform-specific services
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IActivityCollector>(
        _ => new client.Platform.Windows.ProcessCollector(config.ClientId));
    builder.Services.AddSingleton<IShellCommandCollector>(
        _ => new client.Platform.Windows.ShellCommandCollector(config.ClientId));
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<IActivityCollector>(
        _ => new client.Platform.Linux.ProcessCollector(config.ClientId));
    builder.Services.AddSingleton<IShellCommandCollector>(
        _ => new client.Platform.Linux.ShellCommandCollector(config.ClientId));
}
else if (OperatingSystem.IsMacOS())
{
    builder.Services.AddSingleton<IActivityCollector>(
        _ => new client.Platform.MacOS.ProcessCollector(config.ClientId));
    builder.Services.AddSingleton<IShellCommandCollector>(
        _ => new client.Platform.MacOS.ShellCommandCollector(config.ClientId));
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

// Main ViewModel
builder.Services.AddTransient<MainViewModel>();

var host = builder.Build();

await host.StartAsync(CancellationToken.None);

// Set service provider for App to resolve ViewModels from DI
App.ServiceProvider = host.Services;

// Enable auto-start registration immediately
try
{
    var autoStart = host.Services.GetRequiredService<AutoStartService>();
    if (!autoStart.IsAutoStartEnabled())
    {
        autoStart.EnableAutoStart();
    }
}
catch (Exception ex)
{
    var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
    var logger = loggerFactory.CreateLogger("AutoStart");
    logger.LogWarning(ex, "Failed to enable auto-start");
}

if (isBackground)
{
    // Headless mode: keep background services running until Ctrl+C
    var tcs = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        tcs.TrySetResult();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => tcs.TrySetResult();
    await tcs.Task;
}
else
{
    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}

await host.StopAsync(CancellationToken.None);
host.Dispose();
appMutex.Dispose();

static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
