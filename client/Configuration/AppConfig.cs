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

    /// <summary>
    /// The a–p extension ID of the PACKED (signed CRX) extension, derived from
    /// the organization's CRX signing public key (see server/cmd/crxsign). This
    /// is the ID Chrome assigns for policy force-installs (ExtensionInstallForcelist),
    /// and it differs from the path-derived ID used for dev/unpacked loads — the
    /// native-messaging manifest must use whichever ID the install method in use
    /// actually produces. Empty → policy forcelist path is unavailable and the UI
    /// falls back to dev attach / manual instructions.
    /// </summary>
    public string? CrxExtensionId { get; init; }

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
            CrxExtensionId = GetEnv("ALPHA_CRX_EXTENSION_ID")
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
