using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Reads the machine-wide DLP credential file written by the user-session tracker
/// on login (%ProgramData%\&lt;APP_MUTEX&gt;\dlp\device.auth). LocalSystem cannot see
/// the interactive user's LocalAppData SQLite, so this file is the production bridge.
/// </summary>
internal static class DeviceCredentialStore
{
    internal const string FileName = "device.auth";

    public static string DefaultDir
    {
        get
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, ServiceIdentity.DataFolderName, "dlp");
        }
    }

    public static string DefaultPath => Path.Combine(DefaultDir, FileName);

    public static bool TryLoad(out string serverUrl, out string deviceToken)
    {
        serverUrl = "";
        deviceToken = "";
        var path = DefaultPath;
        if (!File.Exists(path)) return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("serverUrl", out var su))
                serverUrl = (su.GetString() ?? "").TrimEnd('/');
            if (root.TryGetProperty("deviceToken", out var dt))
                deviceToken = (dt.GetString() ?? "").Trim();
            return !string.IsNullOrWhiteSpace(serverUrl) || !string.IsNullOrWhiteSpace(deviceToken);
        }
        catch
        {
            return false;
        }
    }
}
