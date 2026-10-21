using AlphaAITracker.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

// AlphaAITracker.Service — Windows SYSTEM DLP spine (plan.md P0).
// Parked unless ALPHA_DLP_ENABLED=true. Does not host Avalonia UI.

EnvBootstrap.Apply();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "AlphaAITracker.Service";
});

var cfg = DlpConfig.FromEnvironment();
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<PolicyStore>();
builder.Services.AddSingleton<RemovableDriveMonitor>();
builder.Services.AddHttpClient("dlp", c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHostedService<PolicyPullWorker>();
builder.Services.AddHostedService<AlertSyncWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RemovableDriveMonitor>());
builder.Services.AddHostedService<EnforceWorker>();
builder.Services.AddHostedService<NamedPipeHost>();

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DlpService");
logger.LogInformation(
    "AlphaAITracker.Service starting (enabled={Enabled}, dataDir={DataDir})",
    cfg.Enabled, cfg.DataDir);

await host.RunAsync();
