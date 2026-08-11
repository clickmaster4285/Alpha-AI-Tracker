using System.Reflection;

namespace client.Configuration;

public class AppConfig
{
    public string ClientId { get; init; } = GenerateMachineId();
    public string DbPath { get; init; } = "data/alpha_tracker.db";
    public string? DbEncryptionKey { get; init; }
    public int CollectIntervalSec { get; init; } = 30;
    public string LogLevel { get; init; } = "Info";
    public string? ServerUrl { get; init; }
    public string? ApiKey { get; init; }

    // Browser journey (Option B — accessibility-based; see Core/BrowserAccessibility)
    public bool BrowserTrackingEnabled { get; init; } = true;
    public int BrowserAccessibilityPollSec { get; init; } = 3;   // how often the OS accessibility tree is polled
    public int BrowserJourneyIdleMinutes { get; init; } = 15;    // close journeys after this many idle minutes
    public bool BrowserCaptureIncognito { get; init; } = false;  // store incognito URLs (legal review required; default off)

    // Browser journey URL fallback — reads the browser's OWN profile history DB when the
    // a11y tree cannot expose the omnibox URL (Linux Chrome 136+ / snap Firefox). Works
    // while the browser runs; no restart, no flag, no extension.
    public bool BrowserHistoryEnabled { get; init; } = true;
    public int BrowserHistoryPollSec { get; init; } = 10;        // how often history DBs are re-scanned/re-read

    // USB / peripheral hotplug tracking (local SQLite only; no server sync yet)
    public bool HardwareDevicesEnabled { get; init; } = true;

    // Software inventory — 100% EVENT-DRIVEN detection via InstalledSoftwareWatcher: it watches
    // the OS install locations (.desktop dirs, dpkg state, Start Menu, /Applications, package-
    // manager dirs) and triggers an instant rescan when software is installed/uninstalled through
    // terminal / software center / control panel / cmd / powershell / manual file delete. NO
    // minute-based periodic scan (user rule 2026-08-10).
    public bool InventoryWatchEnabled { get; init; } = true;

    public static AppConfig FromEnv()
    {
        return new AppConfig
        {
            ClientId = GetEnv("ALPHA_CLIENT_ID") ?? GenerateMachineId(),
            DbPath = GetEnv("ALPHA_DB_PATH") ?? "data/alpha_tracker.db",
            DbEncryptionKey = GetEnv("ALPHA_DB_ENCRYPTION_KEY"),
            CollectIntervalSec = int.TryParse(GetEnv("ALPHA_COLLECT_INTERVAL_SEC"), out var sec) ? sec : 30,
            LogLevel = GetEnv("ALPHA_LOG_LEVEL") ?? "Info",
            ServerUrl = GetEnv("ALPHA_SERVER_URL") ?? GetDefaultServerUrl(),
            ApiKey = GetEnv("ALPHA_API_KEY"),
            BrowserTrackingEnabled = GetEnv("ALPHA_BROWSER_TRACKING_ENABLED") is not ("0" or "false" or "False"),
            BrowserAccessibilityPollSec = int.TryParse(GetEnv("ALPHA_BROWSER_ACCESSIBILITY_POLL_SECONDS"), out var poll) ? poll : 3,
            BrowserJourneyIdleMinutes = int.TryParse(GetEnv("ALPHA_BROWSER_JOURNEY_IDLE_MINUTES"), out var idle) ? idle : 15,
            BrowserCaptureIncognito = GetEnv("ALPHA_BROWSER_CAPTURE_INCOGNITO") is ("1" or "true" or "True"),
            BrowserHistoryEnabled = GetEnv("ALPHA_BROWSER_HISTORY_ENABLED") is not ("0" or "false" or "False"),
            BrowserHistoryPollSec = int.TryParse(GetEnv("ALPHA_BROWSER_HISTORY_POLL_SECONDS"), out var histPoll) ? histPoll : 10,
            HardwareDevicesEnabled = GetEnv("ALPHA_HARDWARE_DEVICES_ENABLED") is not ("0" or "false" or "False"),
            InventoryWatchEnabled = GetEnv("ALPHA_INVENTORY_WATCH_ENABLED") is not ("0" or "false" or "False"),
        };
    }

    private static string? GetDefaultServerUrl()
    {
        var attrs = typeof(AppConfig).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
        return attrs.FirstOrDefault(a => a.Key == "DefaultServerUrl")?.Value;
    }

    private static string? GetEnv(string key)
    {
        var val = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(val) ? null : val.Trim();
    }

    private static string GenerateMachineId()
    {
        var id = Environment.GetEnvironmentVariable("ALPHA_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(id)) return id.Trim();

        var existing = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".machine-id");
        if (File.Exists(existing))
        {
            return File.ReadAllText(existing).Trim();
        }

        var newId = Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(existing, newId);
        }
        catch { }
        return newId;
    }
}
