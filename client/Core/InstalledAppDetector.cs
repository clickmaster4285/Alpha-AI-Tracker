using System.Diagnostics;
using System.Text.RegularExpressions;

namespace client.Core;

/// <summary>
/// Heuristics-based installed application detector.
/// Works cross-platform by checking executable paths, desktop files,
/// package manager databases, and common install locations.
/// </summary>
public partial class InstalledAppDetector : Abstractions.IInstalledAppDetector
{
    private readonly HashSet<string> _knownApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _missingPerms = new();
    private readonly List<string> _permInstructions = new();
    private bool _initialized;
    private readonly object _lock = new();

    public IReadOnlySet<string> KnownInstalledAppNames
    {
        get
        {
            EnsureInitialized();
            return _knownApps;
        }
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
            _knownApps.Clear();
            _missingPerms.Clear();
            _permInstructions.Clear();
        }
        EnsureInitialized();
    }

    public bool IsInstalledApplication(string processName, string? executablePath)
    {
        EnsureInitialized();

        if (string.IsNullOrWhiteSpace(processName))
            return false;

        // Fast path: check known apps
        if (_knownApps.Contains(processName))
            return true;

        // If we have the path, do deeper checking
        if (!string.IsNullOrEmpty(executablePath))
            return CheckPath(executablePath);

        return false;
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            try
            {
                if (OperatingSystem.IsWindows())
                    DetectInstalledWindows();
                else if (OperatingSystem.IsLinux())
                    DetectInstalledLinux();
                else if (OperatingSystem.IsMacOS())
                    DetectInstalledMacOS();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InstalledAppDetector init error: {ex.Message}");
            }

            // Always add common known apps
            _knownApps.AddRange(CommonKnownApps);
            _initialized = true;
        }
    }

    private bool CheckPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Installed apps are typically in Program Files, Windows Apps, or registered paths
                var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                if (!string.IsNullOrEmpty(progFiles) && path.StartsWith(progFiles, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(progFilesX86) && path.StartsWith(progFilesX86, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(localAppData) && path.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
                    return true;
                // Windows Apps (AppX)
                if (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    return true;
                // Common install locations
                if (path.Contains("\\Microsoft\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (OperatingSystem.IsLinux())
            {
                // Standard Linux install paths
                if (path.StartsWith("/usr/bin/", StringComparison.Ordinal) ||
                    path.StartsWith("/usr/local/bin/", StringComparison.Ordinal) ||
                    path.StartsWith("/opt/", StringComparison.Ordinal) ||
                    path.StartsWith("/snap/bin/", StringComparison.Ordinal) ||
                    path.StartsWith("/var/lib/snapd/", StringComparison.Ordinal) ||
                    path.StartsWith("/app/", StringComparison.Ordinal) ||
                    path.Contains("/flatpak/", StringComparison.OrdinalIgnoreCase))
                    return true;

                // .desktop files in standard locations
                if (path.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) &&
                    (path.StartsWith("/usr/share/applications/", StringComparison.Ordinal) ||
                     path.StartsWith("/usr/local/share/applications/", StringComparison.Ordinal) ||
                     path.Contains("/.local/share/applications/", StringComparison.Ordinal)))
                    return true;
            }
            else if (OperatingSystem.IsMacOS())
            {
                // macOS apps are in /Applications or have .app bundle
                if (path.Contains(".app/", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/Applications/", StringComparison.Ordinal) ||
                    path.Contains("/Applications/", StringComparison.Ordinal))
                    return true;
            }
        }
        catch
        {
            // If we can't check, err on the side of including
            return true;
        }

        return false;
    }

    private void DetectInstalledWindows()
    {
        try
        {
            // 1. Enumerate Start Menu programs
            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            if (Directory.Exists(startMenu))
            {
                foreach (var lnk in Directory.GetFiles(startMenu, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (!string.IsNullOrWhiteSpace(name))
                        _knownApps.Add(name);
                }
            }

            // 2. Enumerate common install locations for .exe files (non-recursive, just top-level)
            var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(progFiles) && Directory.Exists(progFiles))
            {
                foreach (var dir in Directory.GetDirectories(progFiles))
                {
                    var name = Path.GetFileName(dir);
                    if (!string.IsNullOrWhiteSpace(name))
                        _knownApps.Add(name);
                }
            }

            var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(progFilesX86) && Directory.Exists(progFilesX86) &&
                !string.Equals(progFilesX86, progFiles, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var dir in Directory.GetDirectories(progFilesX86))
                {
                    var name = Path.GetFileName(dir);
                    if (!string.IsNullOrWhiteSpace(name))
                        _knownApps.Add(name);
                }
            }

            // 3. Try to enumerate installed apps via registry (may require admin)
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key != null)
                {
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey?.GetValue("DisplayName") is string displayName &&
                            !string.IsNullOrWhiteSpace(displayName))
                        {
                            _knownApps.Add(displayName);
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore, standard users can't read all keys but we can read what is accessible
            }
            catch { }
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore, standard users can't read all start menu folders
        }
        catch { }
    }

    private void DetectInstalledLinux()
    {
        try
        {
            // 1. Enumerate .desktop files in standard locations
            var desktopPaths = new[]
            {
                "/usr/share/applications/",
                "/usr/local/share/applications/",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", "applications")
            };

            foreach (var desktopPath in desktopPaths)
            {
                if (!Directory.Exists(desktopPath)) continue;
                foreach (var file in Directory.GetFiles(desktopPath, "*.desktop"))
                {
                    try
                    {
                        var lines = File.ReadAllLines(file);
                        foreach (var line in lines)
                        {
                            if (line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase))
                            {
                                var name = line["Name=".Length..].Trim();
                                if (!string.IsNullOrWhiteSpace(name))
                                    _knownApps.Add(name);
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }

            // 2. Try dpkg for installed packages
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dpkg-query",
                    Arguments = "-W -f=${Package}\\n",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var pkg = line.Trim();
                        if (!string.IsNullOrWhiteSpace(pkg))
                            _knownApps.Add(pkg);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                _missingPerms.Add("dpkg_query");
                _permInstructions.Add(
                    "Run: sudo dpkg-query -W -f='${Package}\\n' to enumerate installed packages.\n" +
                    "Or grant the application permission: sudo setcap CAP_DAC_READ_SEARCH+ep $(readlink -f /proc/$(pidof AlphaAITracker)/exe)");
            }
            catch (InvalidOperationException)
            {
                // dpkg not available on this system (e.g., Fedora, Arch)
                // This is not a permission issue — silently skip
                Debug.WriteLine("dpkg-query not found — non-Debian system, skipping dpkg detection");
            }
            catch { }

            // 3. Try snap list
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
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 1 && !parts[0].Equals("Name", StringComparison.OrdinalIgnoreCase))
                            _knownApps.Add(parts[0]);
                    }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Linux app detection error: {ex.Message}");
        }
    }

    private void DetectInstalledMacOS()
    {
        try
        {
            // Enumerate /Applications directory
            var appsDir = "/Applications";
            if (Directory.Exists(appsDir))
            {
                foreach (var app in Directory.GetDirectories(appsDir, "*.app"))
                {
                    var name = Path.GetFileNameWithoutExtension(app);
                    if (!string.IsNullOrWhiteSpace(name))
                        _knownApps.Add(name);
                }
            }

            // User Applications
            var userApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
            if (Directory.Exists(userApps))
            {
                foreach (var app in Directory.GetDirectories(userApps, "*.app"))
                {
                    var name = Path.GetFileNameWithoutExtension(app);
                    if (!string.IsNullOrWhiteSpace(name))
                        _knownApps.Add(name);
                }
            }

            // Try brew list (if Homebrew is installed)
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
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var pkg = line.Trim();
                        if (!string.IsNullOrWhiteSpace(pkg))
                            _knownApps.Add(pkg);
                    }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"macOS app detection error: {ex.Message}");
        }
    }

    // Common known applications that should always be tracked
    private static readonly string[] CommonKnownApps =
    [
        // Browsers
        "chrome", "firefox", "msedge", "brave", "opera", "vivaldi", "safari", "tor",
        // Terminals
        "cmd", "powershell", "pwsh", "wsl", "bash", "zsh", "sh", "dash", "fish",
        "gnome-terminal", "konsole", "alacritty", "kitty", "iterm2", "terminal",
        "xterm", "rxvt", "urxvt", "st", "tmux", "screen",
        // IDEs / Editors
        "code", "cursor", "windsurf", "zed", "vim", "nvim", "emacs", "nano",
        "notepad", "notepad++", "sublime_text", "sublimetext", "atom", "brackets",
        "visualstudio", "devenv", "rider", "idea", "idea64", "pycharm", "webstorm",
        "android-studio", "xcode", "xed",
        // Office
        "winword", "excel", "powerpnt", "outlook", "teams", "slack", "discord",
        "zoom", "skype", "whatsapp", "telegram",
        // File managers
        "explorer", "nautilus", "nemo", "thunar", "pcmanfm", "dolphin", "krusader",
        "finder",
        // Design
        "photoshop", "illustrator", "figma", "sketch", "gimp", "inkscape", "blender",
        // Dev tools
        "git", "docker", "kubectl", "node", "npm", "yarn", "pnpm", "python", "python3",
        "java", "javac", "dotnet", "go", "rustc", "cargo", "gcc", "g++", "clang",
        "make", "cmake", "mvn", "gradle",
        // Media
        "vlc", "mpv", "spotify", "itunes", "music", "photos", "preview",
        // System
        "settings", "control", "msconfig", "regedit", "taskmgr", "perfmon",
        "resmon", "diskmgmt", "devmgmt", "services",
    ];
}

internal static class HashSetExtensions
{
    public static void AddRange<T>(this HashSet<T> set, IEnumerable<T> items)
    {
        foreach (var item in items)
            set.Add(item);
    }
}
