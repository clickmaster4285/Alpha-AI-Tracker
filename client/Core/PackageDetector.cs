using System.Diagnostics;
using System.Text.Json;
using client.Core.Models;

namespace client.Core;

public partial class PackageDetector : Abstractions.IPackageDetector
{
    private readonly HashSet<string> _knownPackages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _missingPerms = new();
    private readonly List<string> _permInstructions = new();
    private readonly List<InstalledPackage> _packages = new();
    private bool _initialized;
    private readonly object _lock = new();

    public IReadOnlyList<InstalledPackage> GetAllInstalledPackages()
    {
        EnsureInitialized();
        return _packages.ToList();
    }

    public IReadOnlySet<string> KnownPackageNames
    {
        get
        {
            EnsureInitialized();
            return _knownPackages;
        }
    }

    public bool IsKnownPackage(string packageName)
    {
        EnsureInitialized();
        return _knownPackages.Contains(packageName);
    }

    public IReadOnlyList<string> MissingPermissions
    {
        get
        {
            EnsureInitialized();
            return _missingPerms;
        }
    }

    public IReadOnlyList<string> PermissionGrantInstructions
    {
        get
        {
            EnsureInitialized();
            return _permInstructions;
        }
    }

    public void ForceRecheck()
    {
        lock (_lock)
        {
            _initialized = false;
            _knownPackages.Clear();
            _missingPerms.Clear();
            _permInstructions.Clear();
            _packages.Clear();
        }
        EnsureInitialized();
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            try
            {
                ScanPackageManagers();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PackageDetector init error: {ex.Message}");
            }

            _knownPackages.AddRange(CliKnownPackages);
            _initialized = true;
        }
    }

    private void ScanPackageManagers()
    {
        // Language-specific package managers (run on all platforms)
        ScanNpmGlobal();
        ScanPip();

        // Platform-specific
        if (OperatingSystem.IsWindows())
        {
            ScanChocolatey();
            ScanWinget();
            ScanScoop();
        }
        else if (OperatingSystem.IsLinux())
        {
            ScanDpkg();
            ScanSnap();
            ScanFlatpak();
        }
        else if (OperatingSystem.IsMacOS())
        {
            ScanBrew();
            ScanMacPorts();
        }
    }

    // ── npm global packages ──

    private void ScanNpmGlobal()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "npm",
                Arguments = "list -g --json --depth=0",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps)) return;

            foreach (var dep in deps.EnumerateObject())
            {
                var pkg = ParseNpmDep(dep);
                if (pkg != null) AddPackage(pkg);
            }
        }
        catch (InvalidOperationException) { }
        catch (JsonException) { }
        catch { }
    }

    private static InstalledPackage? ParseNpmDep(JsonProperty dep)
    {
        try
        {
            var version = dep.Value.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            return new InstalledPackage
            {
                PackageName = dep.Name,
                Version = version,
                Category = CategorizeNpmPackage(dep.Name),
                SourceManager = "npm",
                Description = $"{dep.Name}@{version}",
                DetectedAt = DateTime.UtcNow,
            };
        }
        catch { return null; }
    }

    private static string CategorizeNpmPackage(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower is "node" or "nodejs") return "runtime";
        if (lower.StartsWith("@types/")) return "library";
        if (lower is "typescript" or "ts-node" or "tsx") return "tool";
        return "library";
    }

    // ── pip packages ──

    private void ScanPip()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pip",
                Arguments = "list --format=json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            using var doc = JsonDocument.Parse(output);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var version = item.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;

                AddPackage(new InstalledPackage
                {
                    PackageName = name,
                    Version = version,
                    Category = CategorizePipPackage(name),
                    SourceManager = "pip",
                    Description = $"{name}=={version}",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch (InvalidOperationException) { }
        catch (JsonException) { }
        catch { }
    }

    private static string CategorizePipPackage(string name)
    {
        if (name.Equals("pip", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("setuptools", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("wheel", StringComparison.OrdinalIgnoreCase))
            return "tool";
        if (name.Equals("python", StringComparison.OrdinalIgnoreCase))
            return "runtime";
        return "library";
    }

    // ── dpkg (Debian/Ubuntu) ──

    private void ScanDpkg()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dpkg-query",
                // The -f format MUST be double-quoted: .NET's Unix argument parser splits
                // on tabs as well as spaces, so an unquoted format with literal tab
                // characters made dpkg-query receive ${Version}/${Maintainer}/${Section}
                // as package-name PATTERNS — "no packages found matching ${Version}"
                // (3 stderr errors on every startup). Quoted, dpkg-query gets the whole
                // format as ONE argument and interprets the \t / \n escapes itself.
                Arguments = "-W -f=\"${Package}\\t${Version}\\t${Maintainer}\\t${Section}\\n\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            // Drain stderr (dpkg-query warnings) so they never pollute the app log.
            _ = proc.StandardError.ReadToEnd();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 1) continue;

                var name = parts[0].Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var section = parts.Length >= 4 ? parts[3].Trim().ToLowerInvariant() : "";
                var category = section switch
                {
                    "libs" or "libdevel" => "library",
                    "devel" or "interpreters" => "tool",
                    "admin" or "utils" or "text" or "net" => "tool",
                    "kernel" or "system" => "system",
                    _ => "tool"
                };

                AddPackage(new InstalledPackage
                {
                    PackageName = name,
                    Version = parts.Length >= 2 ? parts[1].Trim() : "",
                    Publisher = parts.Length >= 3 ? parts[2].Trim() : "",
                    Category = category,
                    SourceManager = "apt",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            _missingPerms.Add("dpkg_query");
        }
        catch (InvalidOperationException) { }
        catch { }
    }

    // ── snap packages ──

    private void ScanSnap()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "snap",
                Arguments = "list",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            // Snap .desktop files live here (e.g. firefox_firefox.desktop).
            // Any snap that has a desktop entry is a GUI application discovered by
            // InstalledAppDetector via $XDG_DATA_DIRS — skip it here to avoid the
            // Firefox-as-package misclassification (one software = one identity).
            var snapDesktopDir = "/var/lib/snapd/desktop/applications";

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1 || parts[0].Equals("Name", StringComparison.OrdinalIgnoreCase))
                    continue;

                var snapName = parts[0];
                if (HasSnapDesktopEntry(snapName, snapDesktopDir))
                    continue; // GUI app — handled by InstalledAppDetector

                AddPackage(new InstalledPackage
                {
                    PackageName = snapName,
                    Version = parts.Length >= 2 ? parts[1] : "",
                    Category = "tool",
                    SourceManager = "snap",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    /// <summary>True if a .desktop file exists for the given snap name in the snap desktop dir.</summary>
    private static bool HasSnapDesktopEntry(string snapName, string snapDesktopDir)
    {
        try
        {
            if (!Directory.Exists(snapDesktopDir)) return false;
            // snap desktop files are named {snap}_{snap}.desktop (e.g. firefox_firefox.desktop)
            return Directory.GetFiles(snapDesktopDir, $"{snapName}_*.desktop").Length > 0;
        }
        catch { return false; }
    }

    // ── flatpak packages ──

    private void ScanFlatpak()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "flatpak",
                Arguments = "list --app --columns=application,version,runtime",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            // Flatpak app .desktop files are exported to the flatpak share dir and surfaced
            // via $XDG_DATA_DIRS (e.g. /var/lib/flatpak/exports/share/applications).
            // Any flatpak app with an exported desktop entry is a GUI application discovered
            // by InstalledAppDetector — skip it here (one software = one identity).
            var flatpakDesktopDirs = GetFlatpakDesktopDirs();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1 || parts[0].Equals("Application", StringComparison.OrdinalIgnoreCase))
                    continue;

                var appId = parts[0];
                if (HasFlatpakDesktopEntry(appId, flatpakDesktopDirs))
                    continue; // GUI app — handled by InstalledAppDetector

                AddPackage(new InstalledPackage
                {
                    PackageName = appId,
                    Version = parts.Length >= 2 ? parts[1] : "",
                    Category = "tool",
                    SourceManager = "flatpak",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    /// <summary>Flatpak export dirs that may contain app .desktop files.</summary>
    private static List<string> GetFlatpakDesktopDirs()
    {
        var dirs = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, ".local", "share", "flatpak", "exports", "share", "applications"));
        dirs.Add("/var/lib/flatpak/exports/share/applications");
        return dirs;
    }

    /// <summary>True if a .desktop file exists for the given flatpak app id in any flatpak export dir.</summary>
    private static bool HasFlatpakDesktopEntry(string appId, List<string> dirs)
    {
        try
        {
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                // flatpak desktop files are named {appId}.desktop
                if (File.Exists(Path.Combine(dir, $"{appId}.desktop")))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    // ── Homebrew (macOS) ──

    private void ScanBrew()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "brew",
                Arguments = "list --formula --quiet",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = line.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                AddPackage(new InstalledPackage
                {
                    PackageName = name,
                    Category = "tool",
                    SourceManager = "brew",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    // ── MacPorts (macOS alternative) ──

    private void ScanMacPorts()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "port",
                Arguments = "installed",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1) continue;
                var name = parts[0].Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Equals("The", StringComparison.OrdinalIgnoreCase))
                    continue;

                AddPackage(new InstalledPackage
                {
                    PackageName = name,
                    Version = parts.Length >= 2 ? parts[1].Trim('@') : "",
                    Category = "tool",
                    SourceManager = "macports",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    // ── Chocolatey (Windows) ──

    private void ScanChocolatey()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "choco",
                Arguments = "list --local-only --limit-output",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('|');
                if (parts.Length < 1) continue;

                AddPackage(new InstalledPackage
                {
                    PackageName = parts[0].Trim(),
                    Version = parts.Length >= 2 ? parts[1].Trim() : "",
                    Category = "tool",
                    SourceManager = "choco",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    // ── Winget (Windows) ──

    private void ScanWinget()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                Arguments = "list --accept-source-agreements",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // winget table format: Name  Id  Version  Available  Source
                // Skip header and separator lines
                if (line.StartsWith("Name", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("---", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1) continue;

                AddPackage(new InstalledPackage
                {
                    PackageName = parts[0],
                    Version = parts.Length >= 3 ? parts[2] : "",
                    Category = "tool",
                    SourceManager = "winget",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    // ── Scoop (Windows) ──

    private void ScanScoop()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "scoop",
                Arguments = "list",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1) continue;
                var name = parts[0].Trim();
                if (name.Equals("Installed", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("----", StringComparison.OrdinalIgnoreCase))
                    continue;

                AddPackage(new InstalledPackage
                {
                    PackageName = name,
                    Version = parts.Length >= 2 ? parts[1].Trim('(', ')') : "",
                    Category = "tool",
                    SourceManager = "scoop",
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    private void AddPackage(InstalledPackage pkg)
    {
        _packages.Add(pkg);
        _knownPackages.Add(pkg.PackageName);
    }

    // CLI tools and runtimes that should always be recognized as packages
    private static readonly string[] CliKnownPackages =
    [
        // Runtimes
        "node", "nodejs", "python", "python3", "java", "javac", "dotnet", "go", "rustc",
        // Package managers
        "npm", "yarn", "pnpm", "pip", "pip3", "cargo", "nuget", "gem",
        // Build tools
        "gcc", "g++", "clang", "make", "cmake", "mvn", "maven", "gradle", "msbuild",
        // CLI tools
        "git", "docker", "kubectl", "helm", "terraform", "ansible", "pulumi",
        "curl", "wget", "jq", "yq", "ripgrep", "rg", "fd", "fzf", "bat",
        "htop", "top", "iotop", "iftop", "nload", "ncdu", "duf", "dust",
        "tmux", "screen", "ssh", "scp", "rsync",
        "aws", "gcloud", "az", "doctl",
        "vim", "nvim", "emacs", "nano",
        "sqlite3", "psql", "mysql", "redis-cli",
    ];
}
