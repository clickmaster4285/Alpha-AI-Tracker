using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client;
using client.Configuration;
using client.Core.Abstractions;
using client.Services;
using client.Storage;

EnvLoader.Load();

var appMutex = new Mutex(true, "AlphaAITracker", out _);

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

builder.Services.AddHostedService<LogCollectorService>();

var host = builder.Build();

var ct = CancellationToken.None;
await host.StartAsync(ct);

try
{
    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}
finally
{
    await host.StopAsync(ct);
    host.Dispose();
    appMutex.Dispose();
}

static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
