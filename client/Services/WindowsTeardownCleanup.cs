using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using client.Core;
using Microsoft.Win32;

namespace client.Services;

/// <summary>
/// Privileged Windows teardown: restore USBSTOR Start=3, stop/delete the DLP
/// service, clear failure actions. Prefer SYSTEM via schtasks (same pattern as
/// Inno). Every external process is hard-killed on timeout so the CLI never
/// hangs indefinitely. Results are verified, not assumed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsTeardownCleanup
{
    public const int UsbStorStartManual = 3;
    private const int ProcessTimeoutMs = 15_000;
    private const int SystemTaskSettleMs = 4_000;

    public sealed record TeardownCleanupResult(
        bool UsbRestored,
        bool ServiceGone,
        bool AttemptedElevatedCleanup,
        bool IsProcessElevated,
        string Summary,
        IReadOnlyList<string> Steps);

    /// <summary>
    /// Restore USB + remove DLP service registration. Idempotent.
    /// </summary>
    public static TeardownCleanupResult RestoreUsbAndRemoveDlpService()
    {
        if (!OperatingSystem.IsWindows())
            return new TeardownCleanupResult(true, true, false, false, "not_windows", Array.Empty<string>());

        var steps = new List<string>();
        var svc = AppInfo.WindowsServiceName;
        var exe = AppInfo.WindowsServiceName + ".exe";
        var elevated = IsElevated();
        steps.Add($"context elevated={elevated}");

        var beforeState = QueryServiceState(svc);
        var beforeUsb = ReadUsbStorStart();
        steps.Add($"before service={beforeState} usbstor={beforeUsb?.ToString() ?? "?"}");

        // Clear SCM failure actions so stop/kill is not undone by restart/5s.
        RunLogged(steps, "sc-failure-clear", "sc.exe",
            $"failure {svc} reset= 0 actions= //", ProcessTimeoutMs);

        // Direct attempts (work after ApplyTeardownGate opened the DACL, if elevated).
        RunLogged(steps, "sc-stop-direct", "sc.exe", $"stop \"{svc}\"", ProcessTimeoutMs);
        RunLogged(steps, "sc-delete-direct", "sc.exe", $"delete \"{svc}\"", ProcessTimeoutMs);
        RunLogged(steps, "taskkill-direct", "taskkill.exe", $"/F /IM \"{exe}\"", ProcessTimeoutMs);
        TryRestoreUsbStorDirect(steps);

        var afterDirectGone = !ServiceExists(svc);
        var afterDirectUsb = ReadUsbStorStart() == UsbStorStartManual;
        if (afterDirectGone && afterDirectUsb)
        {
            steps.Add("verified after direct cleanup");
            return new TeardownCleanupResult(true, true, false, elevated,
                "verified_direct", steps);
        }

        // SYSTEM one-shot (requires ability to create schtasks — usually admin).
        var systemOk = TrySystemCleanupTask(steps, svc, exe);
        Thread.Sleep(SystemTaskSettleMs);

        // Re-try direct after SYSTEM task may have opened/stopped the service.
        RunLogged(steps, "sc-stop-retry", "sc.exe", $"stop \"{svc}\"", ProcessTimeoutMs);
        RunLogged(steps, "sc-delete-retry", "sc.exe", $"delete \"{svc}\"", ProcessTimeoutMs);
        RunLogged(steps, "taskkill-retry", "taskkill.exe", $"/F /IM \"{exe}\"", ProcessTimeoutMs);
        TryRestoreUsbStorDirect(steps);

        var usbOk = ReadUsbStorStart() == UsbStorStartManual;
        var gone = !ServiceExists(svc);
        var afterState = QueryServiceState(svc);
        steps.Add($"after service={afterState} usbstor={ReadUsbStorStart()?.ToString() ?? "?"} systemTask={systemOk}");

        var summary = (usbOk, gone) switch
        {
            (true, true) => "verified",
            (false, true) => "service_gone_usbstor_not_manual",
            (true, false) => "usbstor_ok_service_still_registered",
            _ => "cleanup_incomplete",
        };

        return new TeardownCleanupResult(usbOk, gone, systemOk, elevated, summary, steps);
    }

    public static bool ServiceExists(string? serviceName = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var name = serviceName ?? AppInfo.WindowsServiceName;
        return QueryServiceState(name) is not ("absent" or "query_failed");
    }

    public static string QueryServiceState(string serviceName)
    {
        try
        {
            var (exit, stdout, stderr) = RunCapture("sc.exe", $"query \"{serviceName}\"", ProcessTimeoutMs);
            var text = (stdout + "\n" + stderr);
            if (exit == 1060 || text.Contains("1060", StringComparison.Ordinal) ||
                text.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return "absent";
            if (text.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return "RUNNING";
            if (text.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return "STOPPED";
            if (text.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase)) return "STOP_PENDING";
            if (exit != 0) return $"query_failed:{exit}";
            return "present";
        }
        catch (Exception ex)
        {
            return "query_failed:" + ex.Message;
        }
    }

    public static int? ReadUsbStorStart()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\USBSTOR", writable: false);
            var v = key?.GetValue("Start");
            return v is int i ? i : v is long l ? (int)l : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(id);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void TryRestoreUsbStorDirect(List<string> steps)
    {
        RunLogged(steps, "reg-usbstor", "reg.exe",
            "add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\USBSTOR\" /v Start /t REG_DWORD /d 3 /f",
            ProcessTimeoutMs);
        RunLogged(steps, "sc-usbstor-demand", "sc.exe",
            "config USBSTOR start= demand", ProcessTimeoutMs);
    }

    private static bool TrySystemCleanupTask(List<string> steps, string svc, string exe)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                AppInfo.DataFolderName, "dlp");
            Directory.CreateDirectory(dir);

            var script = Path.Combine(dir, "teardown-cleanup.cmd");
            // Keep script short and non-interactive; no pauses.
            File.WriteAllText(script,
                "@echo off\r\n" +
                "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\USBSTOR\" /v Start /t REG_DWORD /d 3 /f >nul 2>&1\r\n" +
                "sc config USBSTOR start= demand >nul 2>&1\r\n" +
                $"sc failure \"{svc}\" reset= 0 actions= // >nul 2>&1\r\n" +
                $"sc stop \"{svc}\" >nul 2>&1\r\n" +
                $"sc delete \"{svc}\" >nul 2>&1\r\n" +
                $"taskkill /F /IM \"{exe}\" >nul 2>&1\r\n");

            var task = AppInfo.WindowsServiceMaintTask + "Teardown";
            // Match Inno: single-quoted /TR payload that runs cmd /c <script>.
            var tr = $"cmd.exe /c \\\"{script}\\\"";

            var create = RunLogged(steps, "schtasks-create", "schtasks.exe",
                $"/Create /TN \"{task}\" /TR \"{tr}\" /SC ONCE /ST 00:00 /RU SYSTEM /F",
                ProcessTimeoutMs);
            if (create != 0)
            {
                steps.Add($"schtasks-create failed exit={create} (need elevation for /RU SYSTEM)");
                return false;
            }

            var run = RunLogged(steps, "schtasks-run", "schtasks.exe",
                $"/Run /TN \"{task}\"", ProcessTimeoutMs);
            Thread.Sleep(SystemTaskSettleMs);
            RunLogged(steps, "schtasks-delete", "schtasks.exe",
                $"/Delete /TN \"{task}\" /F", ProcessTimeoutMs);

            steps.Add($"schtasks-run exit={run}");
            return run == 0;
        }
        catch (Exception ex)
        {
            steps.Add("schtasks exception: " + ex.Message);
            return false;
        }
    }

    private static int RunLogged(List<string> steps, string op, string fileName, string args, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (exit, stdout, stderr) = RunCapture(fileName, args, timeoutMs);
            var detail = (stdout + " " + stderr).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (detail.Length > 160) detail = detail[..160] + "…";
            steps.Add($"{op} exit={exit} {sw.ElapsedMilliseconds}ms{(detail.Length > 0 ? " " + detail : "")}");
            return exit;
        }
        catch (Exception ex)
        {
            steps.Add($"{op} error={ex.Message} {sw.ElapsedMilliseconds}ms");
            return -1;
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCapture(
        string fileName, string args, int timeoutMs)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        p.Start();
        // Avoid schtasks hanging on a password prompt when stdin is redirected.
        try { p.StandardInput.Close(); } catch { /* ignore */ }

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
            try { p.WaitForExit(3_000); } catch { /* ignore */ }
            return (-1, "", $"timeout_killed_after_{timeoutMs}ms");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return (p.ExitCode, stdout ?? "", stderr ?? "");
    }
}
