using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;

namespace client.Services;

/// <summary>
/// P1 protected uninstall: verify one-time removal code → drain/self-remove/wipe →
/// authorize DLP service teardown (unlock ACL + stop).
/// </summary>
public sealed class ProtectedUninstallService
{
    private readonly AccountRemovalService _accountRemoval;
    private readonly AppConfig _config;
    private readonly ILogStore _store;
    private readonly ILogger<ProtectedUninstallService> _logger;

    public ProtectedUninstallService(
        AccountRemovalService accountRemoval,
        AppConfig config,
        ILogStore store,
        ILogger<ProtectedUninstallService> logger)
    {
        _accountRemoval = accountRemoval;
        _config = config;
        _store = store;
        _logger = logger;
    }

    public static string TeardownOkPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppInfo.DataFolderName, "dlp", "teardown.ok");

    /// <summary>
    /// Verify the removal code and write teardown.ok. Does not need SQLite — uses the
    /// DLP named pipe and/or %ProgramData%\…\device.auth. Safe to call from a second
    /// CLI process while the tracker still holds the mutex.
    /// </summary>
    public static async Task<bool> AuthorizeGateAsync(
        string rawCode,
        string purpose = "both",
        CancellationToken ct = default)
    {
        var code = (rawCode ?? "").Trim();
        if (string.IsNullOrEmpty(code))
        {
            Console.Error.WriteLine("Protected uninstall: empty removal code.");
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("Authorizing DLP service teardown with removal code...");
            if (await DlpPipeClient.AuthorizeTeardownAsync(code, purpose, ct))
            {
                // Service also writes teardown.ok; write again so the Inno gate is
                // reliable even if the service path differs or exits mid-write.
                WriteTeardownOkLocal(purpose);
                Console.WriteLine($"DLP service accepted the code. Gate: {TeardownOkPath}");
                return true;
            }

            Console.WriteLine(
                "DLP service did not accept the code (pipe down or reject). " +
                "Trying DeviceAuth verify via device.auth...");
        }

        if (!await VerifyViaDeviceAuthFileAsync(code, purpose, ct))
        {
            Console.Error.WriteLine("Removal code invalid, expired, or already used.");
            return false;
        }

        WriteTeardownOkLocal(purpose);
        Console.WriteLine($"Removal code verified. Gate: {TeardownOkPath}");
        return true;
    }

    /// <summary>
    /// Full path: authorize gate → drain/self-remove/wipe (needs live store).
    /// </summary>
    public async Task<bool> RunAsync(string rawCode, string purpose = "both", CancellationToken ct = default)
    {
        if (!await AuthorizeGateAsync(rawCode, purpose, ct))
            return false;

        Console.WriteLine("Draining pending data and notifying the server...");
        var notified = await _accountRemoval.RunAsync(ct);
        Console.WriteLine(notified
            ? "Server notified — machine marked old, employee untracked."
            : "Server could not be reached — local account wiped anyway.");

        return true;
    }

    /// <summary>Attach a console so WinExe CLI flags print visible output in PowerShell.</summary>
    public static void EnsureCliConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (GetConsoleWindow() == IntPtr.Zero)
            {
                if (!AttachConsole(ATTACH_PARENT_PROCESS))
                    AllocConsole();
            }
        }
        catch { /* ignore */ }
    }

    public static bool WaitForTeardownOk(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(TeardownOkPath)) return true;
            Thread.Sleep(250);
        }
        return File.Exists(TeardownOkPath);
    }

    private static async Task<bool> VerifyViaDeviceAuthFileAsync(
        string code, string purpose, CancellationToken ct)
    {
        if (!TryReadDeviceAuth(out var serverUrl, out var token) || string.IsNullOrEmpty(token))
        {
            Console.Error.WriteLine(
                "No device.auth under ProgramData — login the tracker once, then retry.");
            return false;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"{serverUrl}/api/v1/devices/removal-verify")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { code, purpose }),
                    Encoding.UTF8,
                    "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Device", token!);
            ClientIdentityHeaders.Apply(req);

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"removal-verify HTTP {(int)resp.StatusCode}: {body}");
                return false;
            }
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("valid", out var v) && v.GetBoolean();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"removal-verify failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> VerifyViaHttpAsync(string code, string purpose, CancellationToken ct)
    {
        var emp = await _store.GetEmployeeInfoAsync(ct);
        var token = emp?.DeviceToken;
        var serverUrl = (_config.ServerUrl ?? "").TrimEnd('/');
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(serverUrl))
            return await VerifyViaDeviceAuthFileAsync(code, purpose, ct);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"{serverUrl}/api/v1/devices/removal-verify")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { code, purpose }),
                    Encoding.UTF8,
                    "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Device", token);
            ClientIdentityHeaders.Apply(req);

            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("removal-verify HTTP {Status}: {Body}", (int)resp.StatusCode, body);
                return false;
            }
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("valid", out var v) && v.GetBoolean();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "removal-verify failed");
            return false;
        }
    }

    private static bool TryReadDeviceAuth(out string serverUrl, out string? token)
    {
        serverUrl = "";
        token = null;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                AppInfo.DataFolderName, "dlp", "device.auth");
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("serverUrl", out var su))
                serverUrl = (su.GetString() ?? "").TrimEnd('/');
            if (doc.RootElement.TryGetProperty("deviceToken", out var dt))
                token = dt.GetString();
            return !string.IsNullOrWhiteSpace(token);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteTeardownOkLocal(string purpose)
    {
        try
        {
            var dir = Path.GetDirectoryName(TeardownOkPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                TeardownOkPath,
                $"authorizedAt={DateTime.UtcNow:o}\npurpose={purpose}\n");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write teardown.ok: {ex.Message}");
        }
    }

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
