using System.Diagnostics;
using System.Text.RegularExpressions;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Platform.Linux;

public partial class ProcessCollector : IActivityCollector
{
    private readonly string _machineId;
    private static bool _atspiChecked;
    private static bool _atspiAvailable;

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

            try
            {
                var name = proc.ProcessName;
                var pid = proc.Id;
                if (pid == 0 || string.IsNullOrWhiteSpace(name)) continue;

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

    private static (int pid, string? title)? GetActiveWindowInfo()
    {
        if (GetActiveWindowXprop() is {} x11) return x11;
        if (GetActiveWindowAtSpi() is {} atspi) return atspi;
        if (GetActiveWindowGnomeEval() is {} ge) return ge;
        if (GetActiveWindowXdotool() is {} xt) return xt;
        return null;
    }

    private static (int pid, string? title)? GetActiveWindowXprop()
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

    private static (int pid, string? title)? GetActiveWindowAtSpi()
    {
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (!string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!_atspiChecked)
        {
            var check = Run("gsettings", "get org.gnome.desktop.interface toolkit-accessibility");
            _atspiAvailable = check == "true";
            _atspiChecked = true;
        }

        if (!_atspiAvailable)
            return null;

        try
        {
            var scriptPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Platform", "Linux", "alpha_atspi.py");
            if (!File.Exists(scriptPath))
                return null;

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = $"\"{scriptPath}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            if (!proc.WaitForExit(2000)) { proc.Kill(); return null; }
            if (proc.ExitCode != 0 || string.IsNullOrEmpty(output)) return null;

            var parts = output.Split('|', 2);
            if (parts.Length < 2) return null;

            var pid = int.TryParse(parts[0], out var p) ? p : (int?)null;
            var title = string.IsNullOrEmpty(parts[1]) ? null : parts[1];
            if (pid == null) return null;

            return (pid.Value, title);
        }
        catch
        {
            return null;
        }
    }

    private static (int pid, string? title)? GetActiveWindowGnomeEval()
    {
        try
        {
            var evalOut = Run("gdbus", "call --session --dest org.gnome.Shell --object-path /org/gnome/Shell --method org.gnome.Shell.Eval \"global.display.focus_window?.get_pid()\"");
            if (evalOut == null || !evalOut.StartsWith("(true, '"))
                return null;

            var pidStr = evalOut.Split('\'')[1];
            if (!int.TryParse(pidStr, out var pid)) return null;

            var titleOut = Run("gdbus", "call --session --dest org.gnome.Shell --object-path /org/gnome/Shell --method org.gnome.Shell.Eval \"global.display.focus_window?.get_title()\"");
            string? title = null;
            if (titleOut != null && titleOut.StartsWith("(true, '"))
            {
                var parts = titleOut.Split('\'');
                if (parts.Length >= 2) title = parts[1];
            }

            return (pid, title);
        }
        catch
        {
            return null;
        }
    }

    private static (int pid, string? title)? GetActiveWindowXdotool()
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
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);
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
}
