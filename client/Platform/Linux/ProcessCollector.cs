using System.Diagnostics;
using System.Runtime.InteropServices;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Platform.Linux;

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

        var processes = Process.GetProcesses();

        foreach (var proc in processes)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var name = proc.ProcessName;
                var pid = proc.Id;
                if (pid == 0 || string.IsNullOrWhiteSpace(name)) continue;

                long mem = 0;
                try { mem = proc.WorkingSet64; } catch { }

                var isForeground = IsForegroundProcess(pid);

                var title = isForeground ? GetActiveWindowTitle() : null;

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
                    Platform = "Linux"
                });
            }
            catch
            {
                // process exited
            }
        }

        return Task.FromResult<IReadOnlyList<ActivityLog>>(logs);
    }

    private static bool IsForegroundProcess(int pid)
    {
        try
        {
            var activePid = GetActiveWindowPid();
            return activePid == pid;
        }
        catch
        {
            return false;
        }
    }

    private static int GetActiveWindowPid()
    {
        try
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xdotool",
                    Arguments = "getactivewindow getwindowpid",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);
            return int.TryParse(output, out var pid) ? pid : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string? GetActiveWindowTitle()
    {
        try
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xdotool",
                    Arguments = "getactivewindow getwindowname",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var title = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch
        {
            return null;
        }
    }
}
