namespace AlphaAITracker.Service;

/// <summary>
/// Env-driven config for the SYSTEM DLP service (plan.md §10).
/// Defaults keep workers parked until ALPHA_DLP_ENABLED=true.
/// </summary>
public sealed class DlpConfig
{
    public bool Enabled { get; init; }
    public string ServerUrl { get; init; } = "";
    public string DeviceToken { get; init; } = "";
    public string DataDir { get; init; } = "";
    public int PolicyPollSec { get; init; } = 300;
    public int AlertSyncSec { get; init; } = 60;
    public bool UsbBlock { get; init; } = true;
    public bool WebBlock { get; init; } = true;
    public int MaxLocalAlerts { get; init; } = 50_000;
    public int DedupSec { get; init; } = 300;
    public string PipeName { get; init; } = "AlphaAITracker.Dlp";

    public string DbPath => Path.Combine(DataDir, "dlp.db");
    public string PolicyDir => Path.Combine(DataDir, "policy");

    public static DlpConfig FromEnvironment()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var defaultDir = Path.Combine(programData, "AlphaAITracker", "dlp");

        return new DlpConfig
        {
            Enabled = GetBool("ALPHA_DLP_ENABLED", false),
            ServerUrl = (Environment.GetEnvironmentVariable("ALPHA_SERVER_URL") ?? "").TrimEnd('/'),
            DeviceToken = Environment.GetEnvironmentVariable("ALPHA_DEVICE_TOKEN") ?? "",
            DataDir = Environment.GetEnvironmentVariable("ALPHA_DLP_DATA_DIR") is { Length: > 0 } d
                ? d
                : defaultDir,
            PolicyPollSec = GetInt("ALPHA_DLP_POLICY_POLL_SEC", 300),
            AlertSyncSec = GetInt("ALPHA_DLP_ALERT_SYNC_SEC", 60),
            UsbBlock = GetBool("ALPHA_DLP_USB_BLOCK", true),
            WebBlock = GetBool("ALPHA_DLP_WEB_BLOCK", true),
            MaxLocalAlerts = GetInt("ALPHA_DLP_MAX_LOCAL_ALERTS", 50_000),
            DedupSec = GetInt("ALPHA_DLP_DEDUP_SEC", 300),
            PipeName = Environment.GetEnvironmentVariable("ALPHA_DLP_PIPE_NAME") is { Length: > 0 } p
                ? p
                : "AlphaAITracker.Dlp",
        };
    }

    private static bool GetBool(string key, bool fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(v)) return fallback;
        return v is "1" or "true" or "TRUE" or "yes" or "YES";
    }

    private static int GetInt(string key, int fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return int.TryParse(v, out var n) && n > 0 ? n : fallback;
    }
}
