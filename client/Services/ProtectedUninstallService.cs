using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using client.Core;

namespace client.Services;

/// <summary>
/// Protected uninstall pipeline (CLI-driven):
/// <list type="number">
/// <item><b>Approve</b> — verify code with server (instant; fail on 401).</item>
/// <item><b>Stop</b> — stop collectors / kill takeover; disable auto-start watchdog bait.</item>
/// <item><b>Sync</b> — drain unsynced rows (bounded).</item>
/// <item><b>Status</b> — <c>self-remove</c>.</item>
/// <item><b>Wipe</b> — local account + machine id.</item>
/// <item><b>Teardown</b> — <c>teardown.ok</c>, pipe unlock, USB restore, delete DLP service, verify.</item>
/// </list>
/// Full success requires independently verified local postconditions — not merely
/// that <c>teardown.ok</c> exists.
/// </summary>
public sealed class ProtectedUninstallService
{
    private const int HttpVerifyTimeoutSec = 8;
    /// <summary>How long the CLI waits for the running tracker to start reporting phases.</summary>
    public static readonly TimeSpan HandoffGrace = TimeSpan.FromSeconds(5);
    /// <summary>Max wait for the full pipeline after handoff is alive.</summary>
    public static readonly TimeSpan PipelineBudget = TimeSpan.FromSeconds(90);

    private readonly AccountRemovalService _accountRemoval;
    private readonly AutoStartService? _autoStart;

    public ProtectedUninstallService(
        AccountRemovalService accountRemoval,
        AutoStartService? autoStart = null)
    {
        _accountRemoval = accountRemoval;
        _autoStart = autoStart;
    }

    public static string TeardownOkPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppInfo.DataFolderName, "dlp", "teardown.ok");

    public static string ApprovedMarkerPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppInfo.DataFolderName, "dlp", "removal.approved");

    /// <summary>Last structured report from <see cref="RunPipelineDetailedAsync"/>.</summary>
    public TeardownReport? LastReport { get; private set; }

    /// <summary>
    /// Full pipeline. Skip approve when <paramref name="codeAlreadyVerified"/>
    /// (CLI already consumed the one-time code).
    /// Returns true only for <see cref="TeardownReport.FullLocalSuccess"/>.
    /// </summary>
    public async Task<bool> RunPipelineAsync(
        string rawCode,
        string purpose = "both",
        CancellationToken ct = default,
        Action<string>? progress = null,
        bool codeAlreadyVerified = false)
    {
        var report = await RunPipelineDetailedAsync(
            rawCode, purpose, ct, progress, codeAlreadyVerified).ConfigureAwait(false);
        return report.FullLocalSuccess;
    }

    public async Task<TeardownReport> RunPipelineDetailedAsync(
        string rawCode,
        string purpose = "both",
        CancellationToken ct = default,
        Action<string>? progress = null,
        bool codeAlreadyVerified = false)
    {
        var code = NormalizeCode(rawCode);
        RemovalProgress.Clear();
        var steps = new List<string>();

        try
        {
            Log("Starting protected uninstall pipeline");

            if (!codeAlreadyVerified)
            {
                if (string.IsNullOrEmpty(code))
                {
                    Fail("empty removal code");
                    return Finish(new TeardownReport
                    {
                        FailedOperation = "approve",
                        Detail = "empty removal code",
                    });
                }

                Set(progress, "approve", "Approving with server");
                Log("Approving removal code with server");
                if (!await VerifyCodeOnlyAsync(code, purpose, ct).ConfigureAwait(false))
                {
                    return Finish(new TeardownReport
                    {
                        FailedOperation = "approve",
                        Detail = "removal code rejected or verify failed",
                    });
                }
                ClearApprovedMarker();
                Log("Approval completed");
            }
            else
            {
                Set(progress, "approve", "Approved (CLI)");
                ClearApprovedMarker();
                Log("Approval skipped (code already verified)");
            }

            // Prevent BackgroundGuard / Run-key relaunch during teardown.
            BackgroundGuardService.SuppressWatchdog = true;
            Set(progress, "stop", "Disabling auto-start");
            var autoClearedEarly = TryDisableAutoStart(steps);
            Log($"Auto-start disable early: {autoClearedEarly}");

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

            Log($"Account removal completed (serverNotified={notified})");
            if (!notified)
            {
                Console.Error.WriteLine(
                    "[uninstall] Server self-remove did not confirm — local wipe done; continuing DLP teardown.");
            }

            Set(progress, "teardown", "Unlocking DLP / removing service");
            var teardown = await FinalizeTeardownDetailedAsync(purpose, ct, progress, steps)
                .ConfigureAwait(false);

            var autoCleared = autoClearedEarly || TryDisableAutoStart(steps) ||
                              !(_autoStart?.IsAutoStartEnabled() ?? IsWindowsAutoStartPresent());

            var report = new TeardownReport
            {
                AccountRemoved = true,
                ServerNotified = notified,
                GateWritten = teardown.GateWritten,
                PipeUnlockOk = teardown.PipeUnlockOk,
                PipeTimedOut = teardown.PipeTimedOut,
                UsbRestored = teardown.UsbRestored,
                DlpServiceGone = teardown.DlpServiceGone,
                AutoStartCleared = autoCleared,
                CleanupPendingElevation = teardown.CleanupPendingElevation,
                FailedOperation = teardown.FailedOperation,
                Detail = teardown.Detail,
                Steps = steps,
            };

            if (report.FullLocalSuccess)
            {
                RemovalProgress.Write("done", "protected-uninstall successfully");
                Log("Full uninstall verified");
            }
            else
            {
                RemovalProgress.Write("failed", report.SummaryLine);
                Console.Error.WriteLine($"[uninstall] {report.SummaryLine}");
                foreach (var s in steps)
                    Console.Error.WriteLine($"[uninstall]   · {s}");
            }

            return Finish(report);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return Finish(new TeardownReport
            {
                FailedOperation = "pipeline",
                Detail = ex.Message,
                Steps = steps,
            });
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
        var r = await FinalizeTeardownDetailedAsync(purpose, ct).ConfigureAwait(false);
        return r.GateWritten && r.UsbRestored && r.DlpServiceGone;
    }

    public static async Task<TeardownSlice> FinalizeTeardownDetailedAsync(
        string purpose = "both",
        CancellationToken ct = default,
        Action<string>? progress = null,
        List<string>? steps = null)
    {
        steps ??= new List<string>();
        var sw = Stopwatch.StartNew();

        Log("Writing teardown.ok gate");
        Set(progress, "teardown", "Writing uninstall gate");
        if (!WriteTeardownOkLocal(purpose))
        {
            steps.Add("gate write failed");
            return new TeardownSlice
            {
                FailedOperation = "write_teardown_ok",
                Detail = "failed to write teardown.ok",
            };
        }
        steps.Add($"gate written ({sw.ElapsedMilliseconds}ms)");
        Log("Gate written");

        var pipeOk = false;
        var pipeTimedOut = false;
        if (OperatingSystem.IsWindows())
        {
            Log("Unlocking DLP teardown via named pipe");
            Set(progress, "teardown", "Unlocking DLP service");
            var pipeSw = Stopwatch.StartNew();
            // Hard wall-clock: never wait forever if the pipe call itself hangs.
            var pipeTask = DlpPipeClient.ApplyTeardownGateDetailedAsync(purpose, CancellationToken.None);
            var pipeFinished = await Task.WhenAny(
                    pipeTask,
                    Task.Delay(DlpPipeClient.ApplyTeardownOverallTimeoutMs + 1_000, CancellationToken.None))
                .ConfigureAwait(false);
            if (pipeFinished == pipeTask)
            {
                var pipe = await pipeTask.ConfigureAwait(false);
                pipeOk = pipe.Ok;
                pipeTimedOut = pipe.TimedOut;
                steps.Add(
                    $"pipe unlock ok={pipeOk} timedOut={pipeTimedOut} " +
                    $"err={pipe.Error ?? "none"} {pipeSw.ElapsedMilliseconds}ms");
                if (pipeOk)
                    Log("DLP unlock completed");
                else
                    Log($"DLP unlock failed: {pipe.Error ?? "not_ok"} (continuing SYSTEM cleanup)");
            }
            else
            {
                pipeTimedOut = true;
                steps.Add($"pipe unlock abandoned after {pipeSw.ElapsedMilliseconds}ms (overall timeout)");
                Log("DLP unlock timed out — continuing SYSTEM cleanup (will not wait on pipe)");
            }

            // Brief settle only when unlock reported ok; never delay on timeout.
            if (pipeOk)
                await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);

            Log("Stopping/removing DLP service + restoring USBSTOR");
            Set(progress, "teardown", "Removing DLP service registration");
            var cleanupSw = Stopwatch.StartNew();
            var cleanup = WindowsTeardownCleanup.RestoreUsbAndRemoveDlpService();
            steps.AddRange(cleanup.Steps);
            steps.Add($"cleanup summary={cleanup.Summary} {cleanupSw.ElapsedMilliseconds}ms");

            Log($"USB restored={cleanup.UsbRestored}; DLP gone={cleanup.ServiceGone}");

            var pendingElevation = !cleanup.ServiceGone && !cleanup.IsProcessElevated;
            string? failedOp = null;
            string? detail = null;
            if (!cleanup.ServiceGone)
            {
                failedOp = "remove_dlp_service";
                detail = cleanup.IsProcessElevated
                    ? "service still registered after elevated cleanup"
                    : "service still registered — re-run elevated or uninstall from Apps & features";
            }
            else if (!cleanup.UsbRestored)
            {
                failedOp = "restore_usbstor";
                detail = $"USBSTOR Start={WindowsTeardownCleanup.ReadUsbStorStart()?.ToString() ?? "?"}";
            }

            Set(progress, "teardown", "Verifying remaining services");
            Log($"Verifying: service={WindowsTeardownCleanup.QueryServiceState(AppInfo.WindowsServiceName)} " +
                $"usbstor={WindowsTeardownCleanup.ReadUsbStorStart()?.ToString() ?? "?"}");

            return new TeardownSlice
            {
                GateWritten = File.Exists(TeardownOkPath),
                PipeUnlockOk = pipeOk,
                PipeTimedOut = pipeTimedOut,
                UsbRestored = cleanup.UsbRestored,
                DlpServiceGone = cleanup.ServiceGone,
                CleanupPendingElevation = pendingElevation,
                FailedOperation = failedOp,
                Detail = detail,
            };
        }

        return new TeardownSlice
        {
            GateWritten = File.Exists(TeardownOkPath),
            UsbRestored = true,
            DlpServiceGone = true,
        };
    }

    public sealed class TeardownSlice
    {
        public bool GateWritten { get; init; }
        public bool PipeUnlockOk { get; init; }
        public bool PipeTimedOut { get; init; }
        public bool UsbRestored { get; init; }
        public bool DlpServiceGone { get; init; }
        public bool CleanupPendingElevation { get; init; }
        public string? FailedOperation { get; init; }
        public string? Detail { get; init; }
    }

    /// <summary>
    /// CLI when another tracker holds the mutex: approve HERE, then always kill
    /// the running instance and take over. Handoff-and-wait was unreliable
    /// (progress stalls → 90s timeout with USB never restored).
    /// </summary>
    public static async Task<HandoffResult> ApproveAndHandoffAsync(
        string rawCode,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!await ApproveAsync(NormalizeCode(rawCode), "both", ct, progress).ConfigureAwait(false))
            return HandoffResult.Failed;

        Set(progress, "stop", "Stopping running tracker");
        _ = SingleInstanceService.SignalExistingInstance(
            SingleInstanceService.CommandProtectedUninstallGo);

        await Task.Delay(800, ct).ConfigureAwait(false);

        Set(progress, "stop", "Taking over — sync and wipe in this process");
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

    private TeardownReport Finish(TeardownReport report)
    {
        LastReport = report;
        return report;
    }

    private bool TryDisableAutoStart(List<string> steps)
    {
        try
        {
            if (_autoStart is not null)
            {
                var ok = _autoStart.DisableAutoStart();
                var still = _autoStart.IsAutoStartEnabled();
                steps.Add($"auto-start disable via service ok={ok} stillPresent={still}");
                return !still;
            }

            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                key?.DeleteValue("AlphaAITracker", throwOnMissingValue: false);
                var gone = !IsWindowsAutoStartPresent();
                steps.Add($"auto-start Run key cleared={gone}");
                return gone;
            }
        }
        catch (Exception ex)
        {
            steps.Add("auto-start disable error: " + ex.Message);
        }
        return false;
    }

    private static bool IsWindowsAutoStartPresent()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("AlphaAITracker") != null;
        }
        catch
        {
            return false;
        }
    }

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
            Console.Error.WriteLine($"[uninstall] Failed to write teardown.ok: {ex.Message}");
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
        Console.Error.WriteLine($"[uninstall] protected-uninstall failed — {reason}");
    }

    private static void Log(string message) =>
        Console.WriteLine($"[uninstall] {message}");
}
