using System.Diagnostics;
using System.Text.RegularExpressions;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Platform.Linux;

public partial class ProcessCollector : IActivityCollector
{
    private readonly string _machineId;
    private static readonly HashSet<string> PermissionCache = new();

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
                    Platform = "Linux",
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
        status["xprop"] = HasTool("xprop");
        status["xdotool"] = HasTool("xdotool");
        status["gdbus"] = HasTool("gdbus");
        status["xdg_portal"] = GetActiveViaPortal() != null;
        status["shell_introspect"] = GetActiveViaShellIntrospect() != null;
        status["atspi"] = GetActiveViaAtSpi() != null;
        status["xprop_x11"] = CheckX11();
        return status;
    }

    private static bool HasTool(string name)
    {
        if (PermissionCache.Contains(name)) return true;
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = name,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var ok = proc.WaitForExit(1000) && proc.ExitCode == 0;
            if (ok) PermissionCache.Add(name);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckX11()
    {
        try
        {
            var outRaw = Run("xprop", "-root -notype _NET_SUPPORTING_WM_CHECK");
            return outRaw != null;
        }
        catch
        {
            return false;
        }
    }

    private static (int pid, string? title)? GetActiveWindowInfo()
    {
        if (GetActiveViaXprop() is {} x11) return x11;
        if (GetActiveViaPortal() is {} portal) return portal;
        if (GetActiveViaShellIntrospect() is {} shell) return shell;
        if (GetActiveViaAtSpi() is {} atspi) return atspi;
        if (GetActiveViaXdotool() is {} xt) return xt;
        return GetActiveViaHeuristic();
    }

    private static (int pid, string? title)? GetActiveViaXprop()
    {
        try
        {
            var rawId = Run("xprop", "-root -notype _NET_ACTIVE_WINDOW");
            if (rawId == null) return null;

            var idMatch = WindowIdRegex().Match(rawId);
            if (!idMatch.Success) return null;

            var wid = idMatch.Value;
            if (wid == "0x0") return null;

            var rawPid = Run("xprop", $"-id {wid} _NET_WM_PID");
            if (rawPid == null) return null;
            var pidMatch = PidRegex().Match(rawPid);
            if (!pidMatch.Success) return null;
            var pid = int.Parse(pidMatch.Groups[1].Value);

            string? title = null;
            var rawTitle = Run("xprop", $"-id {wid} _NET_WM_NAME");
            if (rawTitle != null)
            {
                var tMatch = TitleRegex().Match(rawTitle);
                if (tMatch.Success)
                    title = tMatch.Groups[1].Value;
            }

            return (pid, title);
        }
        catch
        {
            return null;
        }
    }

    private static (int pid, string? title)? GetActiveViaPortal()
    {
        if (!HasTool("gdbus")) return null;

        try
        {
            var outRaw = Run("gdbus", "call --session --dest org.freedesktop.portal.Desktop --object-path /org/freedesktop/portal/desktop --method org.freedesktop.DBus.Properties.GetAll \"org.freedesktop.portal.Window\"");
            if (outRaw == null) return null;

            var titleRaw = Run("gdbus", "call --session --dest org.freedesktop.portal.Desktop --object-path /org/freedesktop/portal/desktop --method org.freedesktop.DBus.Properties.Get \"org.freedesktop.portal.Window\" \"ActiveWindow\"");
            if (titleRaw == null || !titleRaw.StartsWith("("))
                return null;

            var match = WindowTitleFromVariant().Match(titleRaw);
            if (!match.Success) return null;

            var appId = match.Groups[1].Value;
            if (string.IsNullOrEmpty(appId)) return null;

            var pid = ResolveAppIdToPid(appId);
            return (pid, appId);
        }
        catch
        {
            return null;
        }
    }

    private static (int pid, string? title)? GetActiveViaShellIntrospect()
    {
        if (!HasTool("gdbus")) return null;

        try
        {
            var outRaw = Run("gdbus", "call --session --dest org.gnome.Shell --object-path /org/gnome/Shell/Introspect --method org.gnome.Shell.Introspect.GetWindows");
            if (outRaw == null || outRaw.Contains("Access denied"))
                return null;

            var pidMatch = ShellPidFromVariant().Match(outRaw);
            if (!pidMatch.Success) return null;
            var pid = int.Parse(pidMatch.Groups[1].Value);

            var titleMatch = ShellTitleFromVariant().Match(outRaw);
            var title = titleMatch.Success ? titleMatch.Groups[1].Value : null;

            return (pid, title);
        }
        catch
        {
            return null;
        }
    }

    private static (int pid, string? title)? GetActiveViaAtSpi()
    {
        if (!HasTool("gdbus")) return null;

        try
        {
            var addrRaw = Run("gdbus", "call --session --dest org.a11y.Bus --object-path /org/a11y/bus --method org.a11y.Bus.GetAddress");
            if (addrRaw == null) return null;

            var addrMatch = BusAddressRegex().Match(addrRaw);
            if (!addrMatch.Success) return null;
            var busAddr = addrMatch.Groups[1].Value;

            var namesRaw = Run("gdbus", $"call --bus \"{busAddr}\" --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus --method org.freedesktop.DBus.ListNames");
            if (namesRaw == null) return null;

            var nameMatches = AtSpiNameRegex().Matches(namesRaw);
            foreach (Match nameMatch in nameMatches)
            {
                var appName = nameMatch.Groups[1].Value;
                if (appName == ":1.0" || appName.Contains("Registry")) continue;

                try
                {
                    var stateRaw = Run("gdbus", $"call --bus \"{busAddr}\" --dest {appName} --object-path /org/a11y/atspi/accessible/root --method org.freedesktop.DBus.Properties.Get \"org.a11y.atspi.Accessible\" \"AccessibleState\"");
                    if (stateRaw == null) continue;

                    if (stateRaw.Contains("8") || stateRaw.Contains("focused"))
                    {
                        var titleRaw = Run("gdbus", $"call --bus \"{busAddr}\" --dest {appName} --object-path /org/a11y/atspi/accessible/root --method org.freedesktop.DBus.Properties.Get \"org.a11y.atspi.Accessible\" \"Name\"");
                        var title = titleRaw != null ? ExtractStringValue(titleRaw) : null;

                        var pidRaw = Run("gdbus", $"call --bus \"{busAddr}\" --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus --method org.freedesktop.DBus.GetConnectionUnixProcessID \"{appName}\"");
                        var pid = pidRaw != null && int.TryParse(ExtractStringValue(pidRaw), out var p) ? p : 0;

                        if (pid > 0)
                            return (pid, title);
                    }
                }
                catch
                {
                    continue;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static (int pid, string? title)? GetActiveViaXdotool()
    {
        try
        {
            var pidOut = Run("xdotool", "getactivewindow getwindowpid");
            if (pidOut == null || !int.TryParse(pidOut, out var pid)) return null;

            var title = Run("xdotool", "getactivewindow getwindowname");
            return (pid, string.IsNullOrWhiteSpace(title) ? null : title);
        }
        catch
        {
            return null;
        }
    }

    private static (int pid, string? title)? GetActiveViaHeuristic()
    {
        try
        {
            var processes = Process.GetProcesses();
            (int pid, string? title, DateTime start) best = (0, null, DateTime.MinValue);

            foreach (var proc in processes)
            {
                try
                {
                    if (!ProcessFilter.IsUserProcess(proc)) continue;
                    var hasWindow = proc.MainWindowHandle != IntPtr.Zero;
                    if (!hasWindow) continue;

                    var startTime = proc.StartTime;
                    if (startTime > best.start)
                    {
                        string? title = null;
                        try { title = proc.MainWindowTitle; } catch { }
                        best = (proc.Id, title, startTime);
                    }
                }
                catch { }
            }

            return best.pid > 0 ? (best.pid, best.title) : null;
        }
        catch
        {
            return null;
        }
    }

    private static int ResolveAppIdToPid(string appId)
    {
        try
        {
            var processes = Process.GetProcessesByName(appId);
            if (processes.Length > 0)
                return processes[0].Id;

            if (appId.Contains('.'))
            {
                var shortName = appId.Split('.').Last().ToLowerInvariant();
                var procs = Process.GetProcessesByName(shortName);
                if (procs.Length > 0)
                    return procs[0].Id;
            }
        }
        catch { }
        return 0;
    }

    private static string? ExtractStringValue(string gvariantOutput)
    {
        var match = StringValueRegex().Match(gvariantOutput);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? Run(string file, string args)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3000);
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"0x[0-9a-f]+", RegexOptions.IgnoreCase)]
    private static partial Regex WindowIdRegex();

    [GeneratedRegex(@"=\s+(\d+)")]
    private static partial Regex PidRegex();

    [GeneratedRegex(@"""(.+?)""")]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"ActiveWindow\s+:\s+'([^']+)'")]
    private static partial Regex WindowTitleFromVariant();

    [GeneratedRegex(@"pid\s*:?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ShellPidFromVariant();

    [GeneratedRegex(@"title\s*:?\s*'([^']*)'", RegexOptions.IgnoreCase)]
    private static partial Regex ShellTitleFromVariant();

    [GeneratedRegex(@"\(true,\s*'([^']*)'\)")]
    private static partial Regex GdbusStringResult();

    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex BusAddressRegex();

    [GeneratedRegex(@"'(:?\d[\w.]*)'")]
    private static partial Regex AtSpiNameRegex();

    [GeneratedRegex(@"<([^>]*)>")]
    private static partial Regex StringValueRegex();
}
