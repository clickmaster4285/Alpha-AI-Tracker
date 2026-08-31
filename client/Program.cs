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
    Console.WriteLine($"FileJourneyEnabled={cfg.FileJourneyEnabled}");
    Console.WriteLine($"DbPath={cfg.DbPath}");
    Console.WriteLine($"ApiKeySet={!string.IsNullOrEmpty(cfg.ApiKey)}");
    Console.WriteLine($"SyncIntervalSec={cfg.SyncIntervalSec}");
    Console.WriteLine($"SyncMaxRows={cfg.SyncMaxRows}");
    Console.WriteLine($"SyncMaxBytes={cfg.SyncMaxBytes}");
    Console.WriteLine($"SyncCompression={cfg.SyncCompression}");
    Console.WriteLine($"SyncRetentionHours={cfg.SyncRetentionHours}");
    Console.WriteLine($"TaEnabled={cfg.TaEnabled}");
    Console.WriteLine($"IdleThresholdSeconds={cfg.IdleThresholdSeconds}");
    Console.WriteLine($"IdleAwayThresholdSeconds={cfg.IdleAwayThresholdSeconds}");
    Console.WriteLine($"IdlePollSeconds={cfg.IdlePollSeconds}");
    Console.WriteLine($"LockHysteresisSeconds={cfg.LockHysteresisSeconds}");
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

var appMutex = new Mutex(true, SingleInstanceService.MutexName, out var mutexCreated);
if (!mutexCreated)
{
    // ── --restart (post-update relaunch) ──
    // The updater launches us with --restart and then shuts the OLD process down
    // so the newly-installed binary can take over. The old process may still own
    // the mutex for a moment, so instead of treating this like a normal second
    // launch (signal-and-exit), wait up to 8s for it to release ownership.
    if (args.Contains("--restart"))
    {
        if (appMutex.WaitOne(TimeSpan.FromSeconds(8)))
        {
            mutexCreated = true;
        }
        else
        {
            return;
        }
    }
    else
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

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, finalplan section 3 / R7): the ShutdownSentinel
// is registered FIRST so it is StartAsync'd first and StopAsync'd last by the
// .NET host. That ordering guarantees the sentinel's ApplicationStopping hook
// fires before any other hosted service is torn down (including the SQLite
// store). The IEventRecorder that the sentinel writes through is registered
// immediately after so the dependency is satisfied.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.Watchers.ShutdownSentinel>();
builder.Services.AddSingleton<IEventRecorder, SessionEventRecorder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.Watchers.ShutdownSentinel>());

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, A.3): SystemEventWatcher subscribes to OS power /
// lock / sleep / login signals and writes them through the IEventRecorder. It
// runs as a hosted service so the host's lifecycle (StartAsync/StopAsync) governs
// its D-Bus / SystemEvents subscriptions.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.Watchers.SystemEventWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.Watchers.SystemEventWatcher>());

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, A.4): IdleDetector polls the OS idle source
// (Mutter.IdleMonitor / XScreenSaver / GetLastInputInfo) and emits idle_start /
// idle_end threshold crossings through the IEventRecorder.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.IdleDetector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.IdleDetector>());

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, A.7): LocalTimeSkewService measures the client
// clock's skew against the server's HTTP Date header every 15 min and stores
// it per server URL (BUG-7 + BUG-12 fix). No-op when ALPHA_TA_ENABLED=false.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.LocalTimeSkewService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.LocalTimeSkewService>());

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, A.6): ScheduleCacheService mirrors the employee's
// shift + holidays from GET /api/v1/schedules/me every 6h (BUG-6 fix). No-op
// when ALPHA_TA_ENABLED=false or the Phase 2 endpoint is absent (404).
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.ScheduleCacheService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.ScheduleCacheService>());

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, A.8): AttendanceAggregator rolls up today's
// session-idle activity into daily_attendance_cache every 5 min. Reads use the
// read-only connection (R8). No-op when ALPHA_TA_ENABLED=false.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.AttendanceAggregator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.AttendanceAggregator>());

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

// Dedicated sync engine — runs on its OWN loop so collection never blocks on the network.
// Drains unsent rows in adaptive byte-bounded chunks (gzip, polite pauses, backoff); a
// 50k+ row backlog drains in minutes without spiking CPU or adding collection latency.
// Registered as a singleton + hosted so the login flow can inject it and trigger an
// IMMEDIATE drain the moment credentials are persisted (RequestImmediateSync).
builder.Services.AddSingleton<SyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncService>());

// Self-updater — checks GitHub Releases for a newer installer, downloads it into the
// user data dir and installs via the OS installer (pkexec dpkg / Inno / dmg). Always
// registered (the service no-ops when ALPHA_UPDATE_ENABLED=false) so MainViewModel
// and the dashboard can bind its state regardless.
builder.Services.AddSingleton<AppUpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AppUpdateService>());

// Near-real-time install/uninstall detection: watches .desktop/dpkg/Start Menu//Applications
// and triggers an immediate inventory rescan on change — no GUI interaction needed.
if (config.InventoryWatchEnabled)
{
    builder.Services.AddHostedService<InstalledSoftwareWatcher>();
}

// Browser Journey (Option B — accessibility-based: reads the OS accessibility tree.
// No debugger, no extension, no browser catalog dependency; works on every browser
// and every Chrome version, including install→use→uninstall-in-5-min scenarios.)
//
// IBrowserRegistry is registered unconditionally because LogCollectorService and
// SessionLabelResolver consume it regardless of the browser-tracking master switch.
builder.Services.AddSingleton<IBrowserRegistry, BrowserRegistry>();

if (config.BrowserTrackingEnabled)
{
    builder.Services.AddSingleton<IAccessibilityBrowserReader>(sp =>
        AccessibilityBrowserReaderFactory.Create(
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IBrowserRegistry>()));
    // Hybrid URL fallback: reads the browser's own profile history DB when the a11y
    // tree cannot expose the omnibox (Linux Chrome 136+ / snap Firefox). No restart,
    // no flag, no extension; works on all platforms and browsers incl. brand-new ones.
    builder.Services.AddSingleton<BrowserHistoryReader>();
    builder.Services.AddHostedService<AccessibilityBrowserTracker>();
}

// Desktop Event Bus (File Explorer tracking via AT-SPI on Linux, Shell COM on Windows,
// plus FileSystemWatcher + recent-files sources on every platform). Master switch:
// ALPHA_FILE_JOURNEY_ENABLED — when false, no file-journey data is collected at all.
if (config.FileJourneyEnabled)
{
    builder.Services.AddSingleton<EventCoordinator>();
    builder.Services.AddSingleton<JourneyEngine>();
    builder.Services.AddSingleton<ATSPIEventWatcher>();
    builder.Services.AddSingleton<WindowsExplorerWatcher>();
    builder.Services.AddSingleton<IExplorerWindowProvider>(
        sp => sp.GetRequiredService<WindowsExplorerWatcher>());
    builder.Services.AddSingleton<FileSystemEventWatcher>();
    builder.Services.AddSingleton<RecentFilesWatcher>();
    builder.Services.AddHostedService<DesktopEventService>();
}

// USB / peripheral hotplug tracker (local SQLite only; no server sync yet)
if (config.HardwareDevicesEnabled)
{
    builder.Services.AddHostedService<HardwareDeviceWatcherService>();
}

// Main ViewModel + the per-page ViewModels it composes (pages 4–6).
// Transient alongside MainViewModel so each window gets its own page state.
builder.Services.AddTransient<DashboardViewModel>();
builder.Services.AddTransient<SystemSpecsViewModel>();
builder.Services.AddTransient<InstalledAppsViewModel>();
builder.Services.AddTransient<MainViewModel>();

var host = builder.Build();

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, finalplan section 2.6 / R7): wire the sentinel to
// the IHostApplicationLifetime AFTER the host is built (the lifetime is
// created during host.StartAsync, so we have to reach into Services now).
// The Console.CancelKeyPress hook is also installed here so a Ctrl+C in a
// terminal emits the power_off event before the host's normal stop path.
// ────────────────────────────────────────────────────────────────────────────
var shutdownSentinel = host.Services.GetRequiredService<client.Services.Watchers.ShutdownSentinel>();
shutdownSentinel.HookConsoleCancelKeyPress();

try
{
    // Initialize SQLite before any hosted service starts. SystemEventWatcher is
    // intentionally first in DI and emits power_on immediately; starting the
    // host before the store was ready made that write a silent no-op, while its
    // dedup bucket then suppressed LogCollectorService's valid retry.
    await host.Services.GetRequiredService<ILogStore>()
        .InitializeAsync(CancellationToken.None);

    await host.StartAsync(CancellationToken.None);

    // The lifetime is now available - wire the ApplicationStopping hook.
    shutdownSentinel.HookLifetime(host.Services.GetRequiredService<IHostApplicationLifetime>());

    // Set service provider for App to resolve ViewModels from DI
    App.ServiceProvider = host.Services;

    if (isBackground)
    {
        // Headless service mode (systemd `--background` / auto-start at boot): run the
        // hosted services only — NO Avalonia/X11 UI, no window, no tray. GUI init
        // requires a working X connection (DISPLAY + XAUTHORITY); on Wayland the
        // Xwayland auth file lives at /run/user/<uid>/.mutter-Xwaylandauth.* — NOT
        // ~/.Xauthority — so a unit hardcoding XAUTHORITY=~/.Xauthority made the
        // installed background service crash at startup. Background mode never needs
        // a window or tray, so skip the UI entirely; the process keeps running until
        // systemd stops it (SIGTERM).
        //
        // The GUI is created LAZILY: when the user manually launches the app (desktop
        // entry / start menu), the second launch sends a SHOW signal over the named
        // pipe. We then start the Avalonia UI ONCE on a dedicated thread (window +
        // tray) while THIS process keeps running all tracking services — no restart,
        // no data gap. After the UI initializes, App.axaml.cs takes over
        // OnShowRequested to bring the existing window forward for further launches.
        var uiStarted = 0;
        SingleInstanceService.OnShowRequested = () =>
        {
            if (Interlocked.Exchange(ref uiStarted, 1) == 1) return;
            try
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        // The user asked for the GUI — show the window immediately.
                        RefreshLinuxDesktopEnvironment();
                        App.LaunchedHidden = false;
                        BuildAvaloniaApp().StartWithClassicDesktopLifetime(
                            args.Where(a => !a.Equals("--background", StringComparison.OrdinalIgnoreCase)).ToArray());
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[client] GUI startup failed: {ex.Message}");
                    }
                });
                thread.IsBackground = true;
                if (OperatingSystem.IsWindows())
                {
                    thread.SetApartmentState(ApartmentState.STA);
                }
                thread.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[client] Failed to start GUI thread: {ex.Message}");
            }
        };
        // WaitForShutdownAsync is tied to IHostApplicationLifetime. The previous
        // uncancellable Task.Delay kept the process alive after SIGTERM even though
        // ConsoleLifetime had fired ApplicationStopping; systemd eventually had to
        // SIGKILL it and the code below never ran.
        await host.WaitForShutdownAsync();
    }
    else
    {
        App.LaunchedHidden = isMinimized;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        await host.StopAsync(CancellationToken.None);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Time & Attendance (Phase 1, R7): the ShutdownSentinel writes the power_off
    // event inside its ApplicationStopping handler. host.StopAsync() returns
    // AFTER the sentinel's StopAsync runs (reverse DI order), but the SQL write
    // inside the sentinel is async; the SQLite store's Dispose() may run on
    // a parallel path. Wait on the sentinel's MRE with a hard 3-second ceiling
    // (slightly above the recorder's 2s internal timeout) to guarantee the
    // write finishes before we move into the finally block that disposes the
    // single-instance service and the mutex.
    // ────────────────────────────────────────────────────────────────────────────
    shutdownSentinel.PowerOffWritten.Wait(TimeSpan.FromSeconds(3));
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

// A systemd user service keeps the environment captured by its manager, while
// this long-lived process may have inherited stale values from an old unit.
// Read the manager's current graphical-session values immediately before lazy
// Avalonia startup (especially GNOME Wayland's rotating XAUTHORITY path).
static void RefreshLinuxDesktopEnvironment()
{
    if (!OperatingSystem.IsLinux()) return;

    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "systemctl",
            Arguments = "--user show-environment",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return;

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        if (!proc.WaitForExit(2000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return;
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "DISPLAY",
            "WAYLAND_DISPLAY",
            "XAUTHORITY",
            "XDG_RUNTIME_DIR",
            "DBUS_SESSION_BUS_ADDRESS",
        };
        foreach (var line in outputTask.GetAwaiter().GetResult().Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var name = line[..separator];
            if (!allowed.Contains(name)) continue;
            Environment.SetEnvironmentVariable(name, line[(separator + 1)..].TrimEnd('\r'));
        }
    }
    catch
    {
        // The existing environment may already be valid; Avalonia will report
        // the actionable display error if it is not.
    }
}

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
