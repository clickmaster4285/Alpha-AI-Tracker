using System.Diagnostics;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Platform.MacOS;

public class ProcessCollector : IActivityCollector
{
    private readonly string _machineId;

    public ProcessCollector(string machineId)
    {
        _machineId = machineId;
    }

    public Task<IReadOnlyList<ActivityLog>> CollectAsync(CancellationToken ct)
    {
        var logs = new List<ActivityLog>();
        var now = DateTime.UtcNow;
        var currentUser = Environment.UserName;
        var foreground = GetActiveWindowInfo();

        var processes = Process.GetProcesses();

        foreach (var proc in processes)
        {
            if (ct.IsCancellationRequested) break;
            if (!ProcessFilter.IsUserProcess(proc)) continue;

            try
            {
                var name = proc.ProcessName;
                var pid = proc.Id;

                long mem = 0;
                try { mem = proc.WorkingSet64; } catch { }

                var isForeground = foreground?.pid == pid;
                var title = isForeground ? foreground?.title : null;

                logs.Add(new ActivityLog
                {
                    MachineId = _machineId,
                    Timestamp = now,
                    ProcessName = name,
                    WindowTitle = title,
                    ProcessId = pid,
                    CpuPercent = 0,
                    MemoryBytes = mem,
                    IsForeground = isForeground,
                    UserName = currentUser,
                    Platform = "macOS",
                    SessionId = SessionInfo.SessionId
                });
            }
            catch
            {
                // process exited
            }
        }

        return Task.FromResult<IReadOnlyList<ActivityLog>>(logs);
    }

    public static IReadOnlyDictionary<string, bool> GetPermissionStatus()
    {
        var status = new Dictionary<string, bool>();
        status["osascript"] = HasOsascript();
        status["accessibility_permission"] = CheckAccessibilityPermission();
        return status;
    }

    private static bool HasOsascript()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "osascript",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            return proc.WaitForExit(1000) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckAccessibilityPermission()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = "-e 'tell application \"System Events\" to get name of first application process whose frontmost is true'",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0 && string.IsNullOrEmpty(err);
        }
        catch
        {
            return false;
        }
    }

    private static (int pid, string? title)? GetActiveWindowInfo()
    {
        var viaOsascript = GetActiveViaOsascript();
        if (viaOsascript != null) return viaOsascript;

        return null;
    }

    private static (int pid, string? title)? GetActiveViaOsascript()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "osascript",
                    Arguments = "-e 'tell application \"System Events\" to set frontApp to first application process whose frontmost is true' -e 'set appName to name of frontApp' -e 'set appTitle to title of frontApp' -e 'set appPID to unix id of frontApp' -e 'return appName & \"|\" & appTitle & \"|\" & (appPID as string)'",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0 || string.IsNullOrEmpty(output))
                return null;

            var parts = output.Split('|', 3);
            if (parts.Length < 3) return null;

            if (!int.TryParse(parts[2].Trim(), out var pid) || pid <= 0)
                return null;

            var title = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1].Trim();

            return (pid, title);
        }
        catch
        {
            return null;
        }
    }
}
