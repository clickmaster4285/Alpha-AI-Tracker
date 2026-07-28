using System.Diagnostics;
using System.Text.RegularExpressions;
using client.Core.Models;

namespace client.Core;

/// <summary>
/// Heuristics-based installed application detector.
/// Works cross-platform by checking executable paths, desktop files,
/// package manager databases, and common install locations.
/// </summary>
public partial class InstalledAppDetector : Abstractions.IInstalledAppDetector
{
    private readonly HashSet<string> _knownApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _binaryToDisplayName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _missingPerms = new();
    private readonly List<string> _permInstructions = new();
    private bool _initialized;
    private readonly object _lock = new();

    /// <summary>Resolve the display name for a process by its executable binary name (e.g., \"code\" → \"Visual Studio Code\")</summary>
    public string? ResolveDisplayName(string processName)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(processName)) return null;
        
        // Exact match first
        if (_binaryToDisplayName.TryGetValue(processName, out var displayName))
            return displayName;

        // 🟡 Fuzzy fallback: processName="chrome" but binary_name="google-chrome-stable"
        // Check if any binary name contains the process name or vice versa
        foreach (var kvp in _binaryToDisplayName)
        {
            if (kvp.Key.Contains(processName, StringComparison.OrdinalIgnoreCase) ||
                processName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        return null;
    }

    public IReadOnlyList<InstalledApplication> GetAllInstalledApplications()
    {
        EnsureInitialized();
        return _installedApps.ToList();
    }

    private readonly List<InstalledApplication> _installedApps = new();

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

    private void AddAppFromDesktopFile(string filePath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            string? name = null, exec = null;
            string? categories = null, mimeType = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase) && name == null)
                    name = line["Name=".Length..].Trim();
                if (line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase))
                    exec = line["Exec=".Length..].Trim();
                if (line.StartsWith("Categories=", StringComparison.OrdinalIgnoreCase))
                    categories = line["Categories=".Length..].Trim();
                if (line.StartsWith("MimeType=", StringComparison.OrdinalIgnoreCase))
                    mimeType = line["MimeType=".Length..].Trim();
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                var binaryName = ExtractBinaryFromExec(exec);
                _knownApps.Add(name);
                if (!string.IsNullOrEmpty(binaryName))
                {
                    _knownApps.Add(binaryName);
                    _binaryToDisplayName[binaryName] = name;
                }

                // Detect browser: Categories=WebBrowser or MimeType includes http/https URL schemes
                var isBrowser = false;
                if (!string.IsNullOrEmpty(categories))
                {
                    var cats = categories.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (cats.Contains("WebBrowser", StringComparer.OrdinalIgnoreCase))
                        isBrowser = true;
                }
                if (!isBrowser && !string.IsNullOrEmpty(mimeType))
                {
                    var mimes = mimeType.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (mimes.Contains("x-scheme-handler/http", StringComparer.OrdinalIgnoreCase) ||
                        mimes.Contains("x-scheme-handler/https", StringComparer.OrdinalIgnoreCase))
                        isBrowser = true;
                }

                _installedApps.Add(new InstalledApplication
                {
                    AppName = name,
                    BinaryName = binaryName ?? "",
                    InstallPath = exec ?? filePath,
                    Publisher = "",
                    AppVersion = "",
                    ChangeType = "seen",
                    IsBrowser = isBrowser,
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch { }
    }

    private static string? ExtractBinaryFromExec(string? exec)
    {
        if (string.IsNullOrWhiteSpace(exec)) return null;
        // Exec=code %F  →  "code"
        // Exec=/usr/bin/firefox %u  →  "firefox"
        // Exec="my app" --flag  →  "my app"
        exec = exec.Trim();
        // Remove trailing % flags like %F, %u, %U, %f
        var spaceIdx = exec.IndexOf(' ');
        var firstPart = spaceIdx > 0 ? exec[..spaceIdx] : exec;
        // Strip quotes
        firstPart = firstPart.Trim('"');
        // If it's a full path, get the file name without extension
        var binary = Path.GetFileNameWithoutExtension(firstPart);
        return string.IsNullOrWhiteSpace(binary) ? null : binary;
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
                    {
                        _knownApps.Add(name);
                        _binaryToDisplayName[name] = name;
                    }
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
                    {
                        _knownApps.Add(name);
                        _binaryToDisplayName[name] = name;
                    }
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
                    {
                        _knownApps.Add(name);
                        _binaryToDisplayName[name] = name;
                    }
                }
            }

            // 3. Try to enumerate installed apps via registry (may require admin)
            //    This provides the richest metadata (version, publisher, path, uninstall string)
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (key != null)
                {
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey == null) continue;
                        if (subKey.GetValue("DisplayName") is string displayName &&
                            !string.IsNullOrWhiteSpace(displayName))
                        {                // Extract binary name from install location or display icon
                var installPath = subKey.GetValue("InstallLocation") as string ?? "";
                var displayIcon = subKey.GetValue("DisplayIcon") as string ?? "";
                var binaryName = ExtractBinaryFromPath(installPath) ?? ExtractBinaryFromPath(displayIcon) ?? "";

                // Detect browser: check if app has URL Protocols registered (http, https)
                var isBrowser = false;
                try
                {
                    using var urlAssoc = subKey.OpenSubKey("URLAssociations");
                    if (urlAssoc != null)
                    {
                        var http = urlAssoc.GetValue("http");
                        var https = urlAssoc.GetValue("https");
                        if (http != null || https != null)
                            isBrowser = true;
                    }
                }
                catch { }

                _knownApps.Add(displayName);
                if (!string.IsNullOrEmpty(binaryName))
                {
                    _knownApps.Add(binaryName);
                    _binaryToDisplayName[binaryName] = displayName;
                }
                _installedApps.Add(new InstalledApplication
                {
                    AppName = displayName,
                    BinaryName = binaryName,
                    AppVersion = subKey.GetValue("DisplayVersion") as string ?? "",
                    Publisher = subKey.GetValue("Publisher") as string ?? "",
                    InstallPath = installPath,
                    UninstallString = subKey.GetValue("UninstallString") as string ?? "",
                    ChangeType = "installed",
                    IsBrowser = isBrowser,
                    DetectedAt = DateTime.UtcNow,
                });
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch { }
        }
        catch (UnauthorizedAccessException) { }
        catch { }
    }

    private static string? ExtractBinaryFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            // Try to find an .exe in the path
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(path);
            // If it's a directory, look for common exe names
            if (Directory.Exists(path))
            {
                var dirName = Path.GetFileName(path);
                var exeCandidate = Path.Combine(path, dirName + ".exe");
                if (File.Exists(exeCandidate))
                    return dirName;
            }
        }
        catch { }
        return null;
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
                    AddAppFromDesktopFile(file);
                }
            }

            // NOTE: dpkg, snap, flatpak package detection moved to PackageDetector
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
                    {
                        var binaryName = name.ToLowerInvariant();
                        _knownApps.Add(name);
                        _knownApps.Add(binaryName);
                        _binaryToDisplayName[binaryName] = name;

                        var isBrowserApp = IsMacOSBrowserApp(app);
                        _installedApps.Add(new InstalledApplication
                        {
                            AppName = name,
                            BinaryName = binaryName,
                            InstallPath = app,
                            ChangeType = "installed",
                            IsBrowser = isBrowserApp,
                            DetectedAt = DateTime.UtcNow,
                        });
                    }
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
                    {
                        var binaryName = name.ToLowerInvariant();
                        _knownApps.Add(name);
                        _knownApps.Add(binaryName);
                        _binaryToDisplayName[binaryName] = name;

                        var isBrowserApp = IsMacOSBrowserApp(app);
                        _installedApps.Add(new InstalledApplication
                        {
                            AppName = name,
                            BinaryName = binaryName,
                            InstallPath = app,
                            ChangeType = "installed",
                            IsBrowser = isBrowserApp,
                            DetectedAt = DateTime.UtcNow,
                        });
                    }
                }
            }

            // NOTE: brew and macports package detection moved to PackageDetector
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"macOS app detection error: {ex.Message}");
        }
    }

    /// <summary>
    /// Detect if a macOS .app bundle is a browser by reading its Info.plist
    /// and checking CFBundleURLTypes for http/https URL scheme handling.
    /// </summary>
    private static bool IsMacOSBrowserApp(string appPath)
    {
        try
        {
            var plistPath = Path.Combine(appPath, "Contents", "Info.plist");
            if (!File.Exists(plistPath)) return false;

            var plist = File.ReadAllText(plistPath);
            // Check for CFBundleURLSchemes containing http and https
            if (plist.Contains("CFBundleURLSchemes", StringComparison.OrdinalIgnoreCase) &&
                plist.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                // Simple heuristic: look for http and https URL schemes
                var httpCount = 0;
                if (plist.Contains("<string>http</string>", StringComparison.OrdinalIgnoreCase)) httpCount++;
                if (plist.Contains("<string>https</string>", StringComparison.OrdinalIgnoreCase)) httpCount++;
                return httpCount >= 2;
            }
        }
        catch { }
        return false;
    }

}
