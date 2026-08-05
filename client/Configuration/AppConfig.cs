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

    // Browser journey feature flags & knobs
    public bool BrowserTrackingEnabled { get; init; } = true;
    public int BrowserDebugPortStart { get; init; } = 30000;
    public bool BrowserAutoLaunch { get; init; } = false;

    // Runtime/ephemeral thresholds
    public int BrowserRuntimePersistThresholdSec { get; init; } = 30;   // persist runtime after active for N seconds
    public int BrowserEphemeralTtlSec { get; init; } = 300;             // garbage-collect ephemeral runtimes after N seconds
    public int BrowserStartDebounceSec { get; init; } = 10;              // debounce rapid restarts
    public int BrowserJourneyIdleMinutes { get; init; } = 15;           // close journeys after idle (keeps existing name)
    public int BrowserMinMeaningfulEvents { get; init; } = 2;           // optional: require at least N events before persisting
    public int BrowserReconnectBaseSeconds { get; init; } = 5;          // reconnect backoff base
    public int BrowserReconnectMaxSeconds { get; init; } = 60;          // reconnect backoff cap
    public int BrowserCoordinatorDedupSeconds { get; init; } = 5;
    public int BrowserMaxConcurrentSessions { get; init; } = 0;         // 0 = unlimited
    public int BrowserHijackCooldownMinutes { get; init; } = 3;         // min wait between real-profile relaunches (kill-loop guard)

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
            BrowserDebugPortStart = int.TryParse(GetEnv("ALPHA_BROWSER_DEBUG_PORT_START"), out var port) ? port : 30000,
            BrowserAutoLaunch = GetEnv("ALPHA_BROWSER_AUTO_LAUNCH") is ("1" or "true" or "True"),
            BrowserRuntimePersistThresholdSec = int.TryParse(GetEnv("ALPHA_BROWSER_RUNTIME_PERSIST_THRESHOLD_SECONDS"), out var t1) ? t1 : 30,
            BrowserEphemeralTtlSec = int.TryParse(GetEnv("ALPHA_BROWSER_EPHEMERAL_TTL_SECONDS"), out var t2) ? t2 : 300,
            BrowserStartDebounceSec = int.TryParse(GetEnv("ALPHA_BROWSER_START_DEBOUNCE_SECONDS"), out var t3) ? t3 : 10,
            BrowserJourneyIdleMinutes = int.TryParse(GetEnv("ALPHA_BROWSER_JOURNEY_IDLE_MINUTES"), out var t4) ? t4 : 15,
            BrowserMinMeaningfulEvents = int.TryParse(GetEnv("ALPHA_BROWSER_MIN_MEANINGFUL_EVENTS"), out var t5) ? t5 : 2,
            BrowserReconnectBaseSeconds = int.TryParse(GetEnv("ALPHA_BROWSER_RECONNECT_BASE_SECONDS"), out var t6) ? t6 : 5,
            BrowserReconnectMaxSeconds = int.TryParse(GetEnv("ALPHA_BROWSER_RECONNECT_MAX_SECONDS"), out var t7) ? t7 : 60,
            BrowserCoordinatorDedupSeconds = int.TryParse(GetEnv("ALPHA_BROWSER_COORDINATOR_DEDUP_SECONDS"), out var t8) ? t8 : 5,
            BrowserMaxConcurrentSessions = int.TryParse(GetEnv("ALPHA_BROWSER_MAX_CONCURRENT_SESSIONS"), out var t9) ? t9 : 0,
            BrowserHijackCooldownMinutes = int.TryParse(GetEnv("ALPHA_BROWSER_HIJACK_COOLDOWN_MINUTES"), out var t10) ? t10 : 3,
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
