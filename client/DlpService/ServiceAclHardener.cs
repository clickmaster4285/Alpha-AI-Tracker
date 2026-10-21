using System.Diagnostics;
using System.Runtime.Versioning;

namespace AlphaAITracker.Service;

/// <summary>
/// Locks the Windows service DACL for AlphaAITracker.Service only.
/// While locked, even Builtin Administrators cannot stop/delete the service from
/// services.msc / sc stop — only LocalSystem (the service itself after a verified
/// removal code) unlocks and stops. Other products on the machine are untouched.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceAclHardener
{
    public const string ServiceName = "AlphaAITracker.Service";

    /// <summary>
    /// SY: full control (incl. stop/delete/change DACL).
    /// BA: query/enumerate/read only — no SERVICE_STOP, DELETE, or CHANGE_CONFIG.
    /// </summary>
    public const string LockedSddl =
        "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWLOCRRC;;;BA)";

    /// <summary>
    /// After a verified removal code: SY + BA full so the service can stop and
    /// the Inno uninstaller can sc delete.
    /// </summary>
    public const string OpenSddl =
        "D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)";

    public static bool TryApplyLocked(ILogger? logger = null)
    {
        return TrySdSet(LockedSddl, logger);
    }

    public static bool TryApplyOpen(ILogger? logger = null)
    {
        return TrySdSet(OpenSddl, logger);
    }

    private static bool TrySdSet(string sddl, ILogger? logger)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"sdset {ServiceName} \"{sddl}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            p.WaitForExit(10_000);
            if (p.ExitCode != 0)
            {
                var err = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                logger?.LogWarning("sc sdset exit {Code}: {Err}", p.ExitCode, err.Trim());
                return false;
            }
            logger?.LogInformation("Service DACL updated for {Service}", ServiceName);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to set service DACL (not installed as a service yet?)");
            return false;
        }
    }
}
