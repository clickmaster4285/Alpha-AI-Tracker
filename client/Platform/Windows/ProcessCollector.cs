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

    public async Task<IReadOnlyList<ActivityLog>> CollectAsync(CancellationToken ct)
    {
        var logs = new List<ActivityLog>();
        var now = DateTime.UtcNow;
        var foregroundPid = GetForegroundProcessId();
        var currentUser = Environment.UserName;

        // Enumerate ALL visible top-level windows → PID → title mapping
        // This fixes the file manager issue: every process with a visible window
        // gets its title captured, not just the foreground one.
        var knownTitles = EnumerateAllWindowTitles();

        var processes = Process.GetProcesses();
        var procTree = ParentProcessResolver.BuildProcessTree();

        // CPU measurement: single 100ms gap instead of per-process sleep
        var cpuBefore = SnapshotCpuTimes(processes);
        await Task.Delay(100, ct);
        // Refresh process list in case some exited
        processes = Process.GetProcesses();
        var cpuAfter = SnapshotCpuTimes(processes);

        foreach (var proc in processes)
        {
            if (ct.IsCancellationRequested) break;
            if (!ProcessFilter.IsUserProcess(proc)) continue;

            try
            {
                var name = proc.ProcessName;
                var pid = proc.Id;
                var isForeground = pid == foregroundPid;

                long mem = 0;
                try { mem = proc.WorkingSet64; } catch { }

                double cpu = 0;
                if (cpuBefore.TryGetValue(pid, out var prev) &&
                    cpuAfter.TryGetValue(pid, out var curr))
                {
                    cpu = (curr - prev).TotalSeconds / (Environment.ProcessorCount * 0.1) * 100;
                }

                var resolvedTitle = ParentProcessResolver.ResolveWindowTitle(
                    pid, name, procTree, knownTitles, foregroundPid);

                var profile = ParentProcessResolver.GetBrowserProfile(name, pid);

                var title = profile != null && resolvedTitle != null
                    ? $"{resolvedTitle} [{profile}]"
                    : resolvedTitle ?? profile;

                logs.Add(new ActivityLog
                {
                    MachineId = _machineId,
                    Timestamp = now,
                    ProcessName = name,
                    WindowTitle = title,
                    ProcessId = pid,
                    CpuPercent = Math.Round(cpu, 1),
                    MemoryBytes = mem,
                    IsForeground = isForeground,
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

        return logs;
    }

    private static Dictionary<int, TimeSpan> SnapshotCpuTimes(Process[] processes)
    {
        var snap = new Dictionary<int, TimeSpan>();
        foreach (var proc in processes)
        {
            try { snap[proc.Id] = proc.TotalProcessorTime; } catch { }
        }
        return snap;
    }

    internal static Dictionary<int, string?> EnumerateAllWindowTitles()
    {
        var map = new Dictionary<int, string?>();
        var seenPids = new HashSet<int>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid <= 0 || !seenPids.Add(pid)) return true;

            var sb = new StringBuilder(256);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            var title = sb.ToString();
            map[pid] = string.IsNullOrWhiteSpace(title) ? null : title;
            return true;
        }, IntPtr.Zero);

        return map;
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
            status["user32_enumwindows"] = true;
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}
