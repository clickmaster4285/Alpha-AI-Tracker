using System.Text;
using System.Text.Json;

namespace client.Services;

/// <summary>
/// Publishes DeviceAuth credentials for AlphaAITracker.Service (LocalSystem).
/// SYSTEM cannot read the interactive user's LocalAppData SQLite, so login writes
/// %ProgramData%\AlphaAITracker\dlp\device.auth and wipe deletes it.
/// </summary>
public static class DlpDeviceCredentialBridge
{
    private const string FileName = "device.auth";

    private static string DirPath
    {
        get
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "AlphaAITracker", "dlp");
        }
    }

    private static string FilePath => Path.Combine(DirPath, FileName);

    public static void Publish(string? deviceToken, string? employeeId = null, string? serverUrl = null)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            Clear();
            return;
        }

        try
        {
            Directory.CreateDirectory(DirPath);
            var url = (serverUrl ?? Environment.GetEnvironmentVariable("ALPHA_SERVER_URL") ?? "").TrimEnd('/');
            var payload = JsonSerializer.Serialize(new
            {
                serverUrl = url,
                deviceToken = deviceToken.Trim(),
                employeeId = employeeId ?? "",
                updatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            // Atomic-ish write so the service never reads a half-written file.
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, payload, Encoding.UTF8);
            File.Copy(tmp, FilePath, overwrite: true);
            File.Delete(tmp);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DlpDeviceCredentialBridge] Publish failed: {ex.Message}");
        }
    }

    public static void Clear()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DlpDeviceCredentialBridge] Clear failed: {ex.Message}");
        }
    }
}
