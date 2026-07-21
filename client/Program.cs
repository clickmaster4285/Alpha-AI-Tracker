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

var config = AppConfig.FromEnv();

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<ILogStore>(sp =>
{
    var resolvedPath = Path.IsPathRooted(config.DbPath)
        ? config.DbPath
        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.DbPath);
    return new SqliteLogStore(resolvedPath, config.DbEncryptionKey);
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
}

static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
