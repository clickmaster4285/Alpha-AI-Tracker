using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using client.Core;
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
        var procTree = ParentProcessResolver.BuildProcessTree();
        var knownTitles = new Dictionary<int, string?>();

        foreach (var proc in processes)
        {
            if (ct.IsCancellationRequested) break;
            if (!ProcessFilter.IsUserProcess(proc)) continue;

            try
            {
                var pid = proc.Id;
                var isForeground = pid == foregroundPid;
                if (isForeground)
                {
                    var title = GetWindowText(proc);
                    knownTitles[pid] = title;
                }
            }
            catch { }
        }

        foreach (var proc in processes)
        {
            if (ct.IsCancellationRequested) break;
            if (!ProcessFilter.IsUserProcess(proc)) continue;

            try
            {
                var name = proc.ProcessName;
                var pid = proc.Id;

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

                var resolvedTitle = ParentProcessResolver.ResolveWindowTitle(
                    pid, name, procTree, knownTitles, foregroundPid);

                var profile = ParentProcessResolver.GetChromeProfile(name, pid);

                var title = profile != null
                    ? $"{resolvedTitle} [{profile}]"
                    : resolvedTitle;

                logs.Add(new ActivityLog
                {
                    MachineId = _machineId,
                    Timestamp = now,
                    ProcessName = name,
                    WindowTitle = title,
                    ProcessId = pid,
                    CpuPercent = Math.Round(cpu, 1),
                    MemoryBytes = mem,
                    IsForeground = pid == foregroundPid,
                    UserName = currentUser,
                    Platform = "Windows",
                    SessionId = SessionInfo.SessionId
                });
            }
            catch
            {
                // process may have exited — skip
            }
        }

        return Task.FromResult<IReadOnlyList<ActivityLog>>(logs);
    }

    public static IReadOnlyDictionary<string, bool> GetPermissionStatus()
    {
        var status = new Dictionary<string, bool>();
        try
        {
            var hWnd = GetForegroundWindow();
            status["user32_getforegroundwindow"] = hWnd != IntPtr.Zero;
            if (hWnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hWnd, out _);
                status["user32_getwindowthreadprocessid"] = true;
            }
        }
        catch
        {
            status["user32"] = false;
        }
        return status;
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

    private static string? GetWindowText(Process proc)
    {
        try
        {
            var hWnd = proc.MainWindowHandle;
            if (hWnd == IntPtr.Zero) return null;
            var sb = new StringBuilder(256);
            GetWindowTextW(hWnd, sb, sb.Capacity);
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
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
}
