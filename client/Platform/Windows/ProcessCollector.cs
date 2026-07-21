using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Platform.Windows;

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
        var foregroundPid = GetForegroundProcessId();
        var currentUser = Environment.UserName;

        var processes = Process.GetProcesses();

        foreach (var proc in processes)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var name = proc.ProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var pid = proc.Id;
                if (pid == 0) continue;

                long mem = 0;
                double cpu = 0;

                try { mem = proc.WorkingSet64; } catch { }
                try
                {
                    var startTime = proc.StartTime;
                    var elapsed = now.ToLocalTime() - startTime;
                    if (elapsed.TotalSeconds > 1)
                    {
                        try
                        {
                            var prevCpu = proc.TotalProcessorTime;
                            Thread.Sleep(100);
                            var currCpu = proc.TotalProcessorTime;
                            cpu = (currCpu - prevCpu).TotalSeconds / (Environment.ProcessorCount * 0.1) * 100;
                        }
                        catch { }
                    }
                }
                catch { }

                string? windowTitle = null;
                if (pid == foregroundPid)
                {
                    try { windowTitle = GetWindowTitle(proc); } catch { }
                }

                logs.Add(new ActivityLog
                {
                    MachineId = _machineId,
                    Timestamp = now,
                    ProcessName = name,
                    WindowTitle = windowTitle ?? proc.MainWindowTitle,
                    ProcessId = pid,
                    CpuPercent = Math.Round(cpu, 1),
                    MemoryBytes = mem,
                    IsForeground = pid == foregroundPid,
                    UserName = currentUser,
                    Platform = "Windows"
                });
            }
            catch
            {
                // process may have exited — skip
            }
        }

        return Task.FromResult<IReadOnlyList<ActivityLog>>(logs);
    }

    private static int GetForegroundProcessId()
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return -1;
            GetWindowThreadProcessId(hWnd, out var pid);
            return pid;
        }
        catch
        {
            return -1;
        }
    }

    private static string? GetWindowTitle(Process proc)
    {
        try
        {
            var hWnd = proc.MainWindowHandle;
            if (hWnd == IntPtr.Zero) return null;
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
}
