using System.Diagnostics;

namespace client.Core.Browser;

/// <summary>
/// Detects browsers that are RUNNING right now but missing from the installed-apps catalog.
/// The catalog scan runs every ~15 min — a browser installed, used and uninstalled inside
/// that window would otherwise be invisible to the tracker. The probe gives the adapter a
/// binary identity + real profile so the hijack/attach path can start tracking immediately.
/// Main process only: Chromium subprocesses (--type=renderer/gpu/utility/zygote) and headless
/// runs are skipped.
/// </summary>
public static class RunningBrowserProbe
{
    public sealed class RunningBrowserHit
    {
        public required string BinaryPath { get; init; }
        public required string BinaryName { get; init; }
        public required string DisplayName { get; init; }
    }

    public static IReadOnlyList<RunningBrowserHit> Detect(BrowserEngine engine)
    {
        var hits = new List<RunningBrowserHit>();
        foreach (var cmdline in BrowserProcessProbe.EnumerateProcessCmdlines())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cmdline)) continue;
                if (cmdline.Contains("--type=", StringComparison.Ordinal)) continue;
                if (cmdline.Contains("--headless", StringComparison.OrdinalIgnoreCase)) continue;

                var firstArg = cmdline.Split(' ', 2)[0].Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(firstArg)) continue;
                var fileName = Path.GetFileName(firstArg);
                if (string.IsNullOrWhiteSpace(fileName)) continue;
                if (BrowserEngineFamily.Classify(fileName) != engine) continue;

                var binaryPath = firstArg;
                if (!File.Exists(binaryPath))
                    binaryPath = ResolveInPath(fileName) ?? string.Empty;
                if (string.IsNullOrEmpty(binaryPath)) continue;

                hits.Add(new RunningBrowserHit
                {
                    BinaryPath = binaryPath,
                    BinaryName = Path.GetFileNameWithoutExtension(binaryPath),
                    DisplayName = Path.GetFileNameWithoutExtension(binaryPath),
                });
            }
            catch { }
        }
        return hits
            .GroupBy(h => h.BinaryPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static string? ResolveInPath(string binaryName)
    {
        var cmd = OperatingSystem.IsWindows() ? "where" : "which";
        try
        {
            var psi = new ProcessStartInfo(cmd, binaryName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            if (p.ExitCode == 0 && output.Length > 0)
                return output.Split('\n')[0].Trim();
        }
        catch { }
        return null;
    }
}
