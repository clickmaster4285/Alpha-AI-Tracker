using System.Net.Http.Headers;
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

    /// <summary>
    /// Returns true when the removal code was accepted and local teardown finished.
    /// </summary>
    public async Task<bool> RunAsync(string rawCode, string purpose = "both", CancellationToken ct = default)
    {
        var code = (rawCode ?? "").Trim();
        if (string.IsNullOrEmpty(code))
        {
            _logger.LogError("Protected uninstall: empty removal code");
            return false;
        }

        // Prefer service-side verify (LocalSystem + device.auth) so the code is
        // consumed once and the service unlocks/stops in the same step.
        var serviceOk = false;
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("Authorizing DLP service teardown with removal code...");
            serviceOk = await DlpPipeClient.AuthorizeTeardownAsync(code, purpose, ct);
            if (serviceOk)
            {
                Console.WriteLine("DLP service accepted the code and is stopping.");
            }
            else
            {
                Console.WriteLine(
                    "DLP service did not accept the code (not running, or invalid). " +
                    "Trying DeviceAuth verify from this process...");
                if (!await VerifyViaHttpAsync(code, purpose, ct))
                {
                    Console.Error.WriteLine("Removal code invalid or expired.");
                    return false;
                }
                WriteTeardownOkLocal(purpose);
            }
        }
        else
        {
            if (!await VerifyViaHttpAsync(code, purpose, ct))
            {
                Console.Error.WriteLine("Removal code invalid or expired.");
                return false;
            }
        }

        Console.WriteLine("Draining pending data and notifying the server...");
        var notified = await _accountRemoval.RunAsync(ct);
        Console.WriteLine(notified
            ? "Server notified — machine marked old, employee untracked."
            : "Server could not be reached — local account wiped anyway.");

        return true;
    }

    private async Task<bool> VerifyViaHttpAsync(string code, string purpose, CancellationToken ct)
    {
        var emp = await _store.GetEmployeeInfoAsync(ct);
        var token = emp?.DeviceToken;
        var serverUrl = (_config.ServerUrl ?? "").TrimEnd('/');
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(serverUrl))
        {
            // Fall back to device.auth written for the DLP service.
            if (!TryReadDeviceAuth(out serverUrl, out token) || string.IsNullOrEmpty(token))
            {
                _logger.LogError("No device token available to verify removal code");
                return false;
            }
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
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                AppInfo.DataFolderName, "dlp");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "teardown.ok"),
                $"authorizedAt={DateTime.UtcNow:o}\npurpose={purpose}\n");
        }
        catch { /* best-effort */ }
    }
}
