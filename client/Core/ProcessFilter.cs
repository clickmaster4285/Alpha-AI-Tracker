using System.Diagnostics;
using System.Text.RegularExpressions;

namespace client.Core;

public static partial class ProcessFilter
{
    private static readonly HashSet<string> KernelNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "kthreadd", "kworker", "ksoftirqd", "migration", "idle_inject",
            "cpuhp", "rcu", "pool_workqueue_release", "kprobe", "kdevtmpfs",
            "netns", "xenbus", "watchdogd", "systemd", "systemd-udevd",
            "systemd-journald", "systemd-logind", "systemd-resolved",
            "systemd-timesyncd", "systemd-networkd", "systemd-oomd",
            "systemd-userdbd", "systemd-homed", "systemd-hostnamed",
            "systemd-localed", "systemd-timedated", "systemd-machined",
            "systemd-importd", "systemd-bootcheck", "journald", "auditd",
            "accounts-daemon", "acpid", "anacron", "atd", "cron", "dbus",
            "dbus-daemon", "irqbalance", "networkd-dispat", "polkitd",
            "rsyslogd", "sshd", "syslogd", "syslog-ng", "thermald",
            "udevd", "upowerd", "wpa_supplicant", "kerneloops", "psimon",
            "kworker/R-", "gjs", "pipewire", "wireplumber", "anydesk",
            "postgres", "apache2", "xdg-permission-store", "xdg-document-portal",
            "xdg-desktop-portal-gtk", "xdg-desktop-portal-gnome",
            "xdg-desktop-portal", "gnome-session-binary", "gnome-keyring-d",
            "at-spi-bus-launcher", "gvfsd", "gvfs-", "goa-daemon",
            "goa-identity-se", "gsd-", "evolution-", "ibus-", "ibus-daemon",
            "pulseaudio", "rtkit-daemon", "colord", "cupsd", "cups-browsed",
            "fwupd", "geoclue", "ModemManager", "NetworkManager",
            "packagekitd", "power-profiles-daemon", "snapd", "switcheroo",
            "systemd-logind", "udisksd", "upowerd"
        };

    private static readonly int CurrentSessionId = GetCurrentSessionId();

    private static readonly Regex KernelPattern = KernelNamePattern();

    [GeneratedRegex(@"^(kworker|kthread|migration|idle_inject|cpuhp|ksoftirqd|rcu_|pool_workqueue)", RegexOptions.IgnoreCase)]
    private static partial Regex KernelNamePattern();

    private static int GetCurrentSessionId()
    {
        try { return Process.GetCurrentProcess().SessionId; }
        catch { return -1; }
    }

    public static bool IsUserProcess(Process proc)
    {
        try
        {
            var pid = proc.Id;
            var name = proc.ProcessName;

            if (pid == 0) return false;
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (pid < 100) return false;

            if (KernelNames.Contains(name)) return false;
            if (KernelPattern.IsMatch(name)) return false;

            if (proc.SessionId == 0) return false;
            if (CurrentSessionId > 0 && proc.SessionId != CurrentSessionId) return false;

            var mem = 0L;
            try { mem = proc.WorkingSet64; } catch { }

            var hasWindow = false;
            try { hasWindow = proc.MainWindowHandle != IntPtr.Zero; } catch { }

            if (hasWindow) return true;
            if (mem <= 0) return false;

            try
            {
                var startTime = proc.StartTime;
                if ((DateTime.UtcNow - startTime.ToUniversalTime()).TotalSeconds < 5)
                    return false;
            }
            catch
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
