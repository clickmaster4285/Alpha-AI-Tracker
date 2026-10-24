using AlphaAITracker.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

// AlphaAITracker.Service — Windows SYSTEM DLP spine (plan.md P0/P1).
// Parked unless ALPHA_DLP_ENABLED=true. Does not host Avalonia UI.

EnvBootstrap.Apply();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = ServiceIdentity.ServiceName;
});

if (WindowsServiceHelpers.IsWindowsService())
{
    // SCM starts us with System32 as cwd — pin to the exe folder so relative
    // assets and logs stay next to the binary under Program Files.
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
    builder.Environment.ContentRootPath = AppContext.BaseDirectory;
}

var cfg = DlpConfig.FromEnvironment();
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<PolicyStore>();
builder.Services.AddSingleton<WebBlockState>();
builder.Services.AddSingleton<RemovableDriveMonitor>();
builder.Services.AddHttpClient("dlp", c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddHostedService<ServiceAclWorker>();
builder.Services.AddHostedService<PolicyPullWorker>();
builder.Services.AddHostedService<AlertSyncWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RemovableDriveMonitor>());
builder.Services.AddHostedService<EnforceWorker>();
builder.Services.AddHostedService<DnsBlockPinWorker>();
builder.Services.AddHostedService<NamedPipeHost>();
builder.Services.AddHostedService<TrackerWatchdogWorker>();

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DlpService");
logger.LogInformation(
    "{Service} starting (enabled={Enabled}, dataDir={DataDir}, asService={AsService})",
    ServiceIdentity.ServiceName, cfg.Enabled, cfg.DataDir, WindowsServiceHelpers.IsWindowsService());

await host.RunAsync();
