using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using client.Core;

namespace client.Services;

/// <summary>
/// Protected uninstall pipeline (fast, CLI-driven):
/// <list type="number">
/// <item><b>Approve</b> — verify code with server (instant; fail on 401).</item>
/// <item><b>Stop</b> — stop collectors (running instance or kill takeover).</item>
/// <item><b>Sync</b> — drain unsynced rows (bounded).</item>
/// <item><b>Status</b> — <c>self-remove</c>.</item>
/// <item><b>Teardown</b> — wipe DB, <c>teardown.ok</c>, unlock/stop DLP.</item>
/// </list>
/// </summary>
public sealed class ProtectedUninstallService
{
    private const int HttpVerifyTimeoutSec = 8;
    /// <summary>How long the CLI waits for the running tracker to start reporting phases.</summary>
    public static readonly TimeSpan HandoffGrace = TimeSpan.FromSeconds(5);
    /// <summary>Max wait for the full pipeline after handoff is alive.</summary>
    public static readonly TimeSpan PipelineBudget = TimeSpan.FromSeconds(90);

    private readonly AccountRemovalService _accountRemoval;

    public ProtectedUninstallService(AccountRemovalService accountRemoval)
    {
        _accountRemoval = accountRemoval;
    }

    public static string TeardownOkPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppInfo.DataFolderName, "dlp", "teardown.ok");

    public static string ApprovedMarkerPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppInfo.DataFolderName, "dlp", "removal.approved");

    /// <summary>
    /// Full pipeline. Skip approve when <paramref name="codeAlreadyVerified"/>
    /// (CLI already consumed the one-time code).
    /// </summary>
    public async Task<bool> RunPipelineAsync(
        string rawCode,
        string purpose = "both",
        CancellationToken ct = default,
        Action<string>? progress = null,
        bool codeAlreadyVerified = false)
    {
        var code = NormalizeCode(rawCode);
        RemovalProgress.Clear();

        try
        {
            if (!codeAlreadyVerified)
            {
                if (string.IsNullOrEmpty(code))
                {
                    Fail("empty removal code");
                    return false;
                }

                Set(progress, "approve", "Approving with server");
                if (!await VerifyCodeOnlyAsync(code, purpose, ct).ConfigureAwait(false))
                    return false; // VerifyCodeOnlyAsync already Fail()'d with detail
                ClearApprovedMarker();
            }
            else
            {
                Set(progress, "approve", "Approved (CLI)");
                ClearApprovedMarker();
            }

            // stop → sync → status → wipe
            var notified = await _accountRemoval.RunAsync(ct, phase =>
            {
                var (p, label) = phase switch
                {
                    "stop" => ("stop", "Stopping collectors"),
                    "sync" => ("sync", "Syncing unsynced data"),
                    "status" => ("status", "Updating server status"),
                    "wipe" => ("wipe", "Wiping local account"),
                    _ => ("sync", phase),
                };
                Set(progress, p, label);
            }).ConfigureAwait(false);

            if (!notified)
            {
                Console.Error.WriteLine(
                    "Server self-remove did not confirm — local wipe done; continuing DLP teardown.");
            }

            Set(progress, "teardown", "Removing DLP / unlock uninstall");
            if (!await FinalizeTeardownAsync(purpose, ct).ConfigureAwait(false))
            {
                Fail("failed to write uninstall gate or unlock DLP");
                return false;
            }

            RemovalProgress.Write("done", "protected-uninstall successfully");
            return true;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return false;
        }
    }

    /// <summary>Approve only — consumes the one-time code. Instant fail on 401.</summary>
    public static async Task<bool> ApproveAsync(
        string rawCode,
        string purpose = "both",
        CancellationToken ct = default,
        Action<string>? progress = null)
    {
        var code = NormalizeCode(rawCode);
        if (string.IsNullOrEmpty(code))
        {
            Fail("empty removal code");
            return false;
        }

        RemovalProgress.Clear();
        Set(progress, "approve", "Approving with server");
        if (!await VerifyCodeOnlyAsync(code, purpose, ct).ConfigureAwait(false))
            return false;

        WriteApprovedMarker(purpose);
        RemovalProgress.Write("approve", "Approved");
        progress?.Invoke("Approved");
        return true;
    }

    public static async Task<bool> FinalizeTeardownAsync(
        string purpose = "both",
        CancellationToken ct = default)
    {
        if (!WriteTeardownOkLocal(purpose))
            return false;

        if (OperatingSystem.IsWindows())
            _ = await DlpPipeClient.ApplyTeardownGateAsync(purpose, ct).ConfigureAwait(false);

        return File.Exists(TeardownOkPath);
    }

    /// <summary>
    /// CLI when another tracker holds the mutex:
    /// 1) approve HERE (instant), 2) hand off GO to running instance,
    /// 3) if it does not start within <see cref="HandoffGrace"/> → kill it
    ///    and return <c>TakeOver</c> so Program can finish the pipeline itself.
    /// </summary>
    public static async Task<HandoffResult> ApproveAndHandoffAsync(
        string rawCode,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!await ApproveAsync(NormalizeCode(rawCode), "both", ct, progress).ConfigureAwait(false))
            return HandoffResult.Failed;

        Set(progress, "stop", "Handing off to running tracker");
        // Code already consumed — running instance must NOT verify again.
        if (!SingleInstanceService.SignalExistingInstance(
                SingleInstanceService.CommandProtectedUninstallGo))
        {
            Set(progress, "stop", "Tracker not responding — taking over");
            KillOtherClientProcesses();
            return HandoffResult.TakeOver;
        }

        // Wait briefly for the running instance to start writing phases.
        var graceDeadline = DateTime.UtcNow + HandoffGrace;
        while (DateTime.UtcNow < graceDeadline)
        {
            ct.ThrowIfCancellationRequested();
            if (RemovalProgress.TryRead(out var phase, out var label))
            {
                if (phase is "stop" or "sync" or "status" or "wipe" or "teardown" or "done")
                {
                    // Handoff alive — wait for terminal state.
                    return await WaitUntilTerminalAsync(progress, label, ct).ConfigureAwait(false)
                        ? HandoffResult.Completed
                        : HandoffResult.Failed;
                }

                if (phase == "failed")
                    return HandoffResult.Failed;
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        // No heartbeat — kill and take over (approved marker already set).
        Set(progress, "stop", "Tracker stuck — stopping it and taking over");
        KillOtherClientProcesses();
        return HandoffResult.TakeOver;
    }

    public static async Task<bool> WaitForRemoveAccountAsync(
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        RemovalProgress.Clear();
        Set(progress, "stop", "Signaling running tracker");
        if (!SingleInstanceService.SignalExistingInstance(
                SingleInstanceService.CommandRemoveAccount))
        {
            Fail("could not reach the running app");
            return false;
        }

        var graceDeadline = DateTime.UtcNow + HandoffGrace;
        var sawWork = false;
        while (DateTime.UtcNow < graceDeadline && !sawWork)
        {
            if (RemovalProgress.TryRead(out var phase, out _))
            {
                if (phase is "stop" or "sync" or "status" or "wipe" or "done")
                    sawWork = true;
                if (phase == "failed") return false;
            }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        if (!sawWork)
        {
            Set(progress, "stop", "Tracker stuck — stopping it and taking over");
            KillOtherClientProcesses();
            return false; // caller takes over
        }

        return await WaitUntilTerminalAsync(progress, null, ct).ConfigureAwait(false);
    }

    public enum HandoffResult { Completed, Failed, TakeOver }

    public static void EnsureCliConsole() => CliConsole.Ensure();

    public static bool HasApprovedMarker() => File.Exists(ApprovedMarkerPath);

    /// <summary>
    /// cmd.exe treats <c>'code'</c> as literal quotes (unlike PowerShell). Strip a
    /// matching pair so the SHA-256 hash still matches the server challenge.
    /// </summary>
    public static string NormalizeCode(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length >= 2)
        {
            var a = s[0];
            var b = s[^1];
            if ((a == '\'' && b == '\'') || (a == '"' && b == '"'))
                s = s[1..^1].Trim();
        }
        return s;
    }

    public static void KillOtherClientProcesses()
    {
        var self = Environment.ProcessId;
        foreach (var name in new[] { "client", "AlphaAI" })
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (proc.Id == self) continue;
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(3000);
                    }
                    catch { /* ignore */ }
                    finally { proc.Dispose(); }
                }
            }
            catch { /* ignore */ }
        }

        Thread.Sleep(400);
    }

    // ── internals ────────────────────────────────────────────────────────────

    private static async Task<bool> WaitUntilTerminalAsync(
        Action<string>? progress, string? lastLabel, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + PipelineBudget;
        lastLabel ??= "";
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (RemovalProgress.TryRead(out var phase, out var label))
            {
                if (!string.IsNullOrEmpty(label) && label != lastLabel)
                {
                    lastLabel = label;
                    progress?.Invoke(label);
                }

                if (phase == "done") return true;
                if (phase == "failed") return false;
            }

            if (File.Exists(TeardownOkPath) &&
                RemovalProgress.TryRead(out var p2, out _) &&
                p2 is "teardown" or "done")
                return true;

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        Fail("timed out waiting for pipeline to finish");
        return false;
    }

    private static async Task<bool> VerifyCodeOnlyAsync(
        string code, string purpose, CancellationToken ct)
    {
        if (!TryReadDeviceAuth(out var serverUrl, out var deviceToken) ||
            string.IsNullOrEmpty(deviceToken))
        {
            Fail("no device.auth — login the tracker once, then retry");
            return false;
        }

        return await VerifyHttpAsync(serverUrl, deviceToken!, code, purpose, ct)
            .ConfigureAwait(false);
    }

    private static async Task<bool> VerifyHttpAsync(
        string serverUrl,
        string deviceToken,
        string code,
        string purpose,
        CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(HttpVerifyTimeoutSec) };
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(HttpVerifyTimeoutSec));

            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"{serverUrl.TrimEnd('/')}/api/v1/devices/removal-verify")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { code, purpose }),
                    Encoding.UTF8,
                    "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Device", deviceToken);
            ClientIdentityHeaders.Apply(req);

            using var resp = await http.SendAsync(req, linked.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Fail(
                    $"removal code rejected (HTTP {(int)resp.StatusCode}) — " +
                    "issue a FRESH code for THIS device on the web and retry. " +
                    $"Detail: {TrimBody(body)}");
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("valid", out var v) && v.GetBoolean())
                return true;

            Fail("removal code invalid, expired, or already used — issue a fresh code");
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Fail($"removal-verify timed out after {HttpVerifyTimeoutSec}s");
            return false;
        }
        catch (Exception ex)
        {
            Fail($"removal-verify failed: {ex.Message}");
            return false;
        }
    }

    private static string TrimBody(string body)
    {
        body = (body ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return body.Length <= 180 ? body : body[..180] + "…";
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

    private static void WriteApprovedMarker(string purpose)
    {
        try
        {
            var dir = Path.GetDirectoryName(ApprovedMarkerPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(ApprovedMarkerPath,
                $"authorizedAt={DateTime.UtcNow:o}\npurpose={purpose}\n");
        }
        catch { /* best-effort */ }
    }

    private static void ClearApprovedMarker()
    {
        try
        {
            if (File.Exists(ApprovedMarkerPath)) File.Delete(ApprovedMarkerPath);
        }
        catch { /* ignore */ }
    }

    private static bool WriteTeardownOkLocal(string purpose)
    {
        try
        {
            var dir = Path.GetDirectoryName(TeardownOkPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                TeardownOkPath,
                $"authorizedAt={DateTime.UtcNow:o}\npurpose={purpose}\n");
            return File.Exists(TeardownOkPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write teardown.ok: {ex.Message}");
            return false;
        }
    }

    private static void Set(Action<string>? progress, string phase, string label)
    {
        RemovalProgress.Write(phase, label);
        progress?.Invoke(label);
    }

    private static void Fail(string reason)
    {
        RemovalProgress.Write("failed", reason);
        Console.Error.WriteLine($"protected-uninstall failed — {reason}");
    }
}
