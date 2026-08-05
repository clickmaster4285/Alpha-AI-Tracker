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
    public bool BrowserTrackingEnabled { get; init; } = true;
    public int BrowserDebugPortStart { get; init; } = 30000;
    public bool BrowserAutoLaunch { get; init; } = false;

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
            BrowserAutoLaunch = GetEnv("ALPHA_BROWSER_AUTO_LAUNCH") is ("1" or "true" or "True")
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
