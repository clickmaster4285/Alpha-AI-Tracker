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

    public bool IsGuiApplication(string processName, string? executablePath)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(processName)) return false;

        // Fast path 1: exact match in known apps (from .desktop / registry / .app scan)
        if (_knownApps.Contains(processName)) return true;

        // Fast path 2: fuzzy match against known binary names from GUI .desktop entries
        if (ResolveDisplayName(processName) != null) return true;

        // Slow path: scan .desktop / .app bundle / Start Menu for this specific binary
        if (!string.IsNullOrEmpty(executablePath))
            return CheckGuiPath(executablePath, processName);

        return false;
    }

    /// <summary>
    /// Check whether a given executable path and process name correspond
    /// to a GUI application (has a .desktop file, .app bundle, or Start Menu entry).
    /// Unlike CheckPath which returns true for ANY binary in standard paths,
    /// this only returns true for applications with actual GUI desktop metadata.
    /// </summary>
    private bool CheckGuiPath(string path, string processName)
    {
        if (OperatingSystem.IsLinux())
        {
            // Check if there is a .desktop file whose Exec= references this binary
            var desktopDirs = GetLinuxDesktopApplicationDirs();
            foreach (var dir in desktopDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.GetFiles(dir, "*.desktop"))
                    {
                        try
                        {
                            var lines = File.ReadAllLines(file);
                            foreach (var line in lines)
                            {
                                if (line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase))
                                {
                                    var exec = line["Exec=".Length..].Trim();
                                    var binary = ExtractBinaryFromExec(exec);
                                    if (string.Equals(binary, processName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        // Verify it's a real app (Type=Application, NoDisplay!=true)
                                        var noDisplay = lines.Any(l => l.StartsWith("NoDisplay=true", StringComparison.OrdinalIgnoreCase));
                                        var isApp = lines.Any(l => l.Trim().Equals("Type=Application", StringComparison.OrdinalIgnoreCase));
                                        if (!noDisplay && isApp)
                                            return true;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            // GUI apps are in Program Files, WindowsApps, or have Start Menu entries
            var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (!string.IsNullOrEmpty(progFiles) && path.StartsWith(progFiles, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(progFilesX86) && path.StartsWith(progFilesX86, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(localAppData) && path.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
                return true;
            if (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        if (OperatingSystem.IsMacOS())
        {
            // GUI apps are .app bundles
            return path.Contains(".app/", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith("/Applications/", StringComparison.Ordinal);
        }

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
            bool noDisplay = false, isApplication = false, typeFound = false;
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
                if (line.StartsWith("NoDisplay=", StringComparison.OrdinalIgnoreCase))
                    noDisplay = line["NoDisplay=".Length..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                if (line.StartsWith("Type=", StringComparison.OrdinalIgnoreCase) && !typeFound)
                {
                    typeFound = true;
                    isApplication = line["Type=".Length..].Trim().Equals("Application", StringComparison.OrdinalIgnoreCase);
                }
            }
            // Skip hidden/system .desktop files: NoDisplay=true or Type != Application
            if (noDisplay || (typeFound && !isApplication))
                return;
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
                    DesktopId = Path.GetFileNameWithoutExtension(filePath),
                    Categories = categories ?? "",
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
                    DesktopId = subKeyName, // registry uninstall key name as stable Windows identity
                    Categories = isBrowser ? "WebBrowser" : "",
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
            // Discover .desktop files from all application directories.
            // Sources (in priority order, deduplicated by resolved real path):
            //   1. $XDG_DATA_HOME/applications           (user override)
            //   2. ~/.local/share/applications           (user default)
            //   3. each $XDG_DATA_DIRS entry + /applications
            //      (covers /usr/share, /usr/local/share, snap: /var/lib/snapd/desktop,
            //       flatpak exports: /var/lib/flatpak/exports/share, ~/.local/share/flatpak/exports/share)
            //   4. /usr/share/applications, /usr/local/share/applications  (baseline always included)
            //
            // This fixes the Firefox-snap misclassification: the snap .desktop lives at
            // /var/lib/snapd/desktop/applications/firefox_firefox.desktop which was previously
            // outside the scan path, so Firefox never entered installed_applications and fell
            // through to PackageDetector.ScanSnap as a generic tool package.
            var desktopPaths = GetLinuxDesktopApplicationDirs();

            var seenDirs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var desktopPath in desktopPaths)
            {
                if (string.IsNullOrEmpty(desktopPath)) continue;
                try
                {
                    var resolved = Path.GetFullPath(desktopPath.TrimEnd('/'));
                    if (!seenDirs.Add(resolved)) continue; // skip duplicate directory
                    if (!Directory.Exists(resolved)) continue;
                    foreach (var file in Directory.GetFiles(resolved, "*.desktop"))
                    {
                        AddAppFromDesktopFile(file);
                    }
                }
                catch { /* ignore unreadable dirs */ }
            }

            // NOTE: dpkg, snap, flatpak package detection moved to PackageDetector
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Linux app detection error: {ex.Message}");
        }
    }

    /// <summary>
    /// Build the list of application directories to scan for .desktop files on Linux.
    /// Derived from XDG_DATA_HOME / XDG_DATA_DIRS plus the standard baseline paths.
    /// </summary>
    private static List<string> GetLinuxDesktopApplicationDirs()
    {
        var dirs = new List<string>();

        // User application dir: XDG_DATA_HOME or default ~/.local/share
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
            dirs.Add(Path.Combine(xdgDataHome, "applications"));
        dirs.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "applications"));

        // XDG_DATA_DIRS entries (colon-separated) + /applications
        var xdgDataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (!string.IsNullOrWhiteSpace(xdgDataDirs))
        {
            foreach (var entry in xdgDataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                dirs.Add(Path.Combine(entry, "applications"));
            }
        }

        // Baseline standard directories — always included even if XDG_DATA_DIRS is unset/minimal
        dirs.Add("/usr/share/applications");
        dirs.Add("/usr/local/share/applications");

        return dirs;
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
                        var (isBrowserApp, bundleId) = InspectMacOSBundle(app);
                        _knownApps.Add(name);
                        _knownApps.Add(binaryName);
                        _binaryToDisplayName[binaryName] = name;

                        _installedApps.Add(new InstalledApplication
                        {
                            AppName = name,
                            BinaryName = binaryName,
                            DesktopId = bundleId,         // CFBundleIdentifier as stable identity
                            Categories = bundleId,         // store bundle id for classification
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
                        var (isBrowserApp, bundleId) = InspectMacOSBundle(app);
                        _knownApps.Add(name);
                        _knownApps.Add(binaryName);
                        _binaryToDisplayName[binaryName] = name;

                        _installedApps.Add(new InstalledApplication
                        {
                            AppName = name,
                            BinaryName = binaryName,
                            DesktopId = bundleId,
                            Categories = bundleId,
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
    /// Inspect a macOS .app bundle's Info.plist to determine (a) whether it is a browser
    /// (CFBundleURLSchemes contains http and https) and (b) its stable bundle identifier
    /// (CFBundleIdentifier). Returns ("", "") if the plist is missing/unreadable.
    /// </summary>
    private static (bool isBrowser, string bundleId) InspectMacOSBundle(string appPath)
    {
        try
        {
            var plistPath = Path.Combine(appPath, "Contents", "Info.plist");
            if (!File.Exists(plistPath)) return (false, "");

            var plist = File.ReadAllText(plistPath);

            // Extract CFBundleIdentifier (stable identity)
            string bundleId = ExtractPlistString(plist, "CFBundleIdentifier");

            // Detect browser: CFBundleURLSchemes containing both http and https
            var isBrowser = false;
            if (plist.Contains("CFBundleURLSchemes", StringComparison.OrdinalIgnoreCase))
            {
                var httpCount = 0;
                if (plist.Contains("<string>http</string>", StringComparison.OrdinalIgnoreCase)) httpCount++;
                if (plist.Contains("<string>https</string>", StringComparison.OrdinalIgnoreCase)) httpCount++;
                isBrowser = httpCount >= 2;
            }
            return (isBrowser, bundleId);
        }
        catch { }
        return (false, "");
    }

    /// <summary>Naive extraction of a &lt;key&gt;Name&lt;/key&gt;&lt;string&gt;value&lt;/string&gt; pair from a plist.</summary>
    private static string ExtractPlistString(string plist, string key)
    {
        try
        {
            var keyIdx = plist.IndexOf($"<key>{key}</key>", StringComparison.OrdinalIgnoreCase);
            if (keyIdx < 0) return "";
            var stringStart = plist.IndexOf("<string>", keyIdx, StringComparison.OrdinalIgnoreCase);
            if (stringStart < 0) return "";
            stringStart += "<string>".Length;
            var stringEnd = plist.IndexOf("</string>", stringStart, StringComparison.OrdinalIgnoreCase);
            if (stringEnd < 0) return "";
            return plist.Substring(stringStart, stringEnd - stringStart).Trim();
        }
        catch { return ""; }
    }

}
