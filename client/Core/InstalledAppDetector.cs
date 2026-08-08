using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using client.Core.BrowserAccessibility;
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

        // Exact overrides for Windows system binaries whose names collide with other apps.
        // Without these the fuzzy matcher maps explorer.exe → "Internet Explorer" and
        // RuntimeBroker → "Run" (one name is a substring of the other).
        if (DisplayNameOverrides.TryGetValue(processName, out var overrideName))
            return overrideName;

        // 🟡 Fuzzy fallback: processName="chrome" but binary_name="google-chrome-stable"
        // Check if any binary name contains the process name or vice versa.
        // Guard: BOTH sides must be ≥4 chars — otherwise 3-letter names like "Run"
        // substring-match unrelated processes ("RuntimeBroker" contains "Run").
        if (processName.Length < 4)
            return null;
        foreach (var kvp in _binaryToDisplayName)
        {
            if (kvp.Key.Length < 4) continue;
            if (kvp.Key.Contains(processName, StringComparison.OrdinalIgnoreCase) ||
                processName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        return null;
    }

    /// <summary>Stable display names for Windows system binaries (checked before the fuzzy matcher).</summary>
    private static readonly Dictionary<string, string> DisplayNameOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["explorer"] = "File Explorer",
        ["windows explorer"] = "File Explorer",
        ["svchost"] = "Windows Services",
        ["winlogon"] = "Windows Logon",
        ["lsass"] = "Windows Security",
        ["csrss"] = "Windows System",
        ["services"] = "Windows Services",
        ["dllhost"] = "Windows DCOM Host",
        ["conhost"] = "Windows Console Host",
    };

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

    [SupportedOSPlatform("windows")]
    private void DetectInstalledWindows()
    {
        try
        {
            // 1. Start Menu shortcuts — the Windows analog of Linux .desktop files.
            //    Each shortcut's target executable is resolved (Code.exe → "Visual Studio Code"),
            //    giving clean display names + a binary→name map, exactly like Exec= on Linux.
            ScanStartMenuShortcuts();

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

            // 3. Enumerate installed apps via the registry Uninstall keys. The old code read only
            //    HKLM\…\Uninstall (the 64-bit view), which missed 32-bit installs (WOW6432Node) and
            //    per-user installs (HKCU) — that is why only 3 apps (Office/VLC/WinRAR) were found.
            //    All three nodes are scanned now, with a junk filter that drops runtimes, redists,
            //    drivers, updates, and system components (they are packages/system, not apps).
            ScanRegistryUninstallNode(Microsoft.Win32.Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            ScanRegistryUninstallNode(Microsoft.Win32.Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
            ScanRegistryUninstallNode(Microsoft.Win32.Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        }
        catch (UnauthorizedAccessException) { }
        catch { }
    }

    /// <summary>
    /// Start Menu .lnk scanning — the Windows equivalent of the Linux .desktop scan.
    /// Resolves each shortcut's target executable via WScript.Shell (COM) and records
    /// { clean display name → binary name } — "Visual Studio Code.lnk" → Code.exe,
    /// the clean name that fixes the registry's "Microsoft Visual Studio Code (User)".
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void ScanStartMenuShortcuts()
    {
        try
        {
            // Base64-encoded UTF-16LE -EncodedCommand: avoids every quoting/escaping issue
            // when embedding PowerShell with single/double quotes and tabs.
            var script = @"
$ws = New-Object -ComObject WScript.Shell
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$dirs = @(
  (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
  (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs')
)
foreach ($d in $dirs) {
  if (-not (Test-Path -LiteralPath $d)) { continue }
  Get-ChildItem -LiteralPath $d -Filter *.lnk -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
    try {
      $s = $ws.CreateShortcut($_.FullName)
      ('{0}' + [char]9 + '{1}') -f $_.BaseName, $s.TargetPath
    } catch {}
  }
}
";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(20000);
            _ = proc.StandardError.ReadToEnd();

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                var lnkName = parts[0].Trim();
                var target = parts[1].Trim().Trim('"', '\'');
                if (string.IsNullOrWhiteSpace(lnkName) || string.IsNullOrWhiteSpace(target)) continue;

                // Skip non-application shortcuts: URLs, AppX/Store launchers, junk names.
                if (target.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
                    target.Contains("shell:AppsFolder", StringComparison.OrdinalIgnoreCase) ||
                    target.Contains("ms-windows-store", StringComparison.OrdinalIgnoreCase) ||
                    target.Contains("ms-appx", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsLnkJunkName(lnkName)) continue;

                _knownApps.Add(lnkName);

                var binaryName = ExtractBinaryFromShortcutTarget(target);
                if (string.IsNullOrEmpty(binaryName)) continue;

                _knownApps.Add(binaryName);
                if (!_binaryToDisplayName.ContainsKey(binaryName))
                    _binaryToDisplayName[binaryName] = lnkName;

                var isBrowser = BrowserAccessibilityHelpers.IsBrowserProcess(binaryName);
                _installedApps.Add(new InstalledApplication
                {
                    AppName = lnkName,
                    BinaryName = binaryName,
                    DesktopId = lnkName, // stable identity; registry rows for the same binary merge onto this name
                    Categories = isBrowser ? "WebBrowser" : "",
                    InstallPath = target,
                    ChangeType = "installed",
                    IsBrowser = isBrowser,
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch (UnauthorizedAccessException) { }
        catch { }
    }

    /// <summary>Shortcut names that are navigation helpers, not applications.</summary>
    private static bool IsLnkJunkName(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower == "uninstall" || lower.StartsWith("uninstall ", StringComparison.Ordinal) ||
               lower.StartsWith("getting started", StringComparison.Ordinal) ||
               lower.StartsWith("readme", StringComparison.Ordinal) ||
               lower.StartsWith("visit website", StringComparison.Ordinal) ||
               lower.StartsWith("online support", StringComparison.Ordinal) ||
               lower.StartsWith("what's new", StringComparison.Ordinal) ||
               lower.StartsWith("whats new", StringComparison.Ordinal) ||
               lower.StartsWith("documentation", StringComparison.Ordinal) ||
               lower.StartsWith("license", StringComparison.Ordinal);
    }

    private static string? ExtractBinaryFromShortcutTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var binary = Path.GetFileNameWithoutExtension(Path.GetFileName(target));
            return string.IsNullOrWhiteSpace(binary) ? null : binary;
        }
        catch { return null; }
    }

    /// <summary>
    /// Strip noisy per-user/arch suffixes installers append to registry DisplayName
    /// ("Visual Studio Code (User)" → "Visual Studio Code", "… (64-bit)" → "…") so the
    /// name can dedup against the clean Start Menu shortcut name on app_name.
    /// </summary>
    private static string CleanRegistryDisplayName(string displayName)
    {
        var name = displayName.Trim();
        while (true)
        {
            var open = name.LastIndexOf('(');
            var close = name.LastIndexOf(')');
            if (open < 0 || close < open) break;
            var inner = name.Substring(open + 1, close - open - 1).Trim();
            var lower = inner.ToLowerInvariant();
            if (!(lower is "user" or "system" or "x64" or "x86" or "32-bit" or "64-bit" or
                      "per-machine" or "per-user" or "machine"))
                break;
            name = name[..open].TrimEnd();
        }
        return name;
    }

    [SupportedOSPlatform("windows")]
    private void ScanRegistryUninstallNode(Microsoft.Win32.RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key == null) return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null) continue;
                    if (!(subKey.GetValue("DisplayName") is string displayName) ||
                        string.IsNullOrWhiteSpace(displayName))
                        continue;

                    // Skip runtimes, redists, drivers, updates, and system components.
                    if (IsJunkRegistryEntry(subKey, displayName))
                        continue;

                    // Extract binary name from install location or display icon
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

                    var appVersion = subKey.GetValue("DisplayVersion") as string ?? "";
                    var publisher = subKey.GetValue("Publisher") as string ?? "";
                    var uninstallString = subKey.GetValue("UninstallString") as string ?? "";

                    // Prefer the clean Start Menu shortcut name over the registry DisplayName
                    // ("Microsoft Visual Studio Code (User)" → "Visual Studio Code"). When the
                    // shortcut scan already mapped this binary, merge the registry metadata into
                    // that row instead of creating a second app_name — installed_applications is
                    // keyed on app_name, so this gives ONE row per software with both sources.
                    var appName = CleanRegistryDisplayName(displayName);
                    if (!string.IsNullOrEmpty(binaryName) &&
                        _binaryToDisplayName.TryGetValue(binaryName, out var shortcutName))
                    {
                        appName = shortcutName;
                        var existing = _installedApps.FirstOrDefault(a =>
                            string.Equals(a.AppName, shortcutName, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            if (string.IsNullOrEmpty(existing.AppVersion)) existing.AppVersion = appVersion;
                            if (string.IsNullOrEmpty(existing.Publisher)) existing.Publisher = publisher;
                            if (string.IsNullOrEmpty(existing.InstallPath)) existing.InstallPath = installPath;
                            existing.UninstallString = uninstallString;
                            existing.IsBrowser |= isBrowser;
                            if (isBrowser && !existing.Categories.Contains("WebBrowser", StringComparison.OrdinalIgnoreCase))
                                existing.Categories = "WebBrowser";
                            continue; // shortcut row already added — no duplicate row
                        }
                    }

                    _knownApps.Add(appName);
                    if (!string.IsNullOrEmpty(binaryName))
                    {
                        _knownApps.Add(binaryName);
                        if (!_binaryToDisplayName.ContainsKey(binaryName))
                            _binaryToDisplayName[binaryName] = appName;
                    }
                    _installedApps.Add(new InstalledApplication
                    {
                        AppName = appName,
                        BinaryName = binaryName,
                        DesktopId = subKeyName, // registry uninstall key name as stable Windows identity
                        Categories = isBrowser ? "WebBrowser" : "",
                        AppVersion = appVersion,
                        Publisher = publisher,
                        InstallPath = installPath,
                        UninstallString = uninstallString,
                        ChangeType = "installed",
                        IsBrowser = isBrowser,
                        DetectedAt = DateTime.UtcNow,
                    });
                }
                catch { }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch { }
    }

    /// <summary>
    /// True for registry Uninstall entries that are NOT user-facing applications:
    /// Windows updates/patches, drivers, runtimes, redistributables, SDKs, and developer
    /// tools (Node/Git/Go/Python/OpenSSL…) that belong in installed_packages, not apps.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool IsJunkRegistryEntry(Microsoft.Win32.RegistryKey subKey, string displayName)
    {
        try
        {
            // System components and child packages marked by the OS/installer.
            if (subKey.GetValue("SystemComponent") is int sc && sc == 1) return true;
            if (subKey.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent)) return true;
            if (subKey.GetValue("ReleaseType") is string rt &&
                (rt.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                 rt.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
                return true;

            // Windows updates, patches, drivers.
            if (displayName.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains("Security Update", StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains("Update for ", StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
                return true;

            // Installer helper rows that are not applications.
            if (displayName.Contains("LocalServiceComponents", StringComparison.OrdinalIgnoreCase) ||
                displayName.EndsWith(" Uninstaller", StringComparison.OrdinalIgnoreCase) ||
                displayName.EndsWith(" Uninstall", StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains(" Uninstaller ", StringComparison.OrdinalIgnoreCase))
                return true;

            // Runtimes / redistributables / SDKs / frameworks.
            foreach (var pattern in JunkRegistryNamePatterns)
            {
                if (displayName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Developer CLI tools/runtimes that belong in installed_packages (tool category).
            if (IsDevToolRegistryName(displayName)) return true;
        }
        catch { return false; }
        return false;
    }

    private static readonly string[] JunkRegistryNamePatterns =
    {
        ".NET", "Visual C++", "Redistributable", "Runtime", "Software Development Kit",
        "Windows App Runtime", "WinAppRuntime", "Microsoft UI Xaml", "VC_redist",
        "DirectX", "Microsoft Edge Update", "Microsoft Update Health", "WebView2 Runtime",
        "Xbox Game Bar", "HEIF Image", "VP9 Video", "WebP Image Extension",
        "Media Feature Pack", "Windows Web Experience Pack", "Windows SDK",
        "Microsoft Edge WebView", "Visual C++ 20", "Microsoft Visual C++",
        "Driver", "Audio COM Components", "COM Components", "Web Plugins",
    };

    /// <summary>Exact/prefix checks for dev tools that are CLI/runtime software, not GUI applications.</summary>
    private static bool IsDevToolRegistryName(string displayName)
    {
        var lower = displayName.ToLowerInvariant();
        if (lower == "git" || lower.StartsWith("git version", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("go programming language", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("node.js", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("python", StringComparison.Ordinal)) return true;
        if (lower.Contains("openssl", StringComparison.Ordinal)) return true;
        if (lower == "nmap" || lower.StartsWith("nmap ", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("npcap", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("postgresql ", StringComparison.Ordinal)) return true;
        if (lower.StartsWith("redis ", StringComparison.Ordinal)) return true;
        if (lower.Contains("jdk", StringComparison.Ordinal) ||
            lower.Contains("temurin", StringComparison.Ordinal) ||
            lower.Contains("java", StringComparison.Ordinal)) return true;
        return false;
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
