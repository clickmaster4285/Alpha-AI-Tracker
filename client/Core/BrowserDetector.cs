using System.Diagnostics;
using System.Text.Json;

namespace client.Core;

/// <summary>
/// A browser discovered via OS-level http/https handler registration.
/// No brand/binary-name list ever gates inclusion — Tor, LibreWolf, Waterfox,
/// Epiphany, Falkon, Zen Browser and any future browser are caught automatically
/// because they register as URL-scheme handlers with the OS.
/// </summary>
public class BrowserCandidate
{
    public string Name { get; set; } = string.Empty;
    public string BinaryPath { get; set; } = string.Empty;
    public string BinaryName { get; set; } = string.Empty;
    /// <summary>Stable identity: .desktop basename (Linux), registry subkey (Windows), bundle id (macOS).</summary>
    public string DesktopId { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    /// <summary>Set when the .desktop Exec runs through flatpak (e.g. org.gnome.Epiphany).</summary>
    public string? FlatpakAppId { get; set; }
}

/// <summary>
/// The ONLY source of "is this a browser". Detection is driven entirely by
/// OS-level browser registration:
///   Linux   → .desktop files whose MimeType contains x-scheme-handler/http(s)
///             or Categories contains WebBrowser, across $XDG_DATA_HOME,
///             ~/.local/share/applications, every $XDG_DATA_DIRS entry,
///             flatpak exports and snap desktop dirs.
///   Windows → HKLM\SOFTWARE\Clients\StartMenuInternet\* (the key the OS itself
///             maintains for "all installed browsers"), read via reg.exe
///             subprocess (consistent with the existing reg.exe/PowerShell
///             convention on the plain net10.0 TFM).
///   macOS   → all http/https handlers from the LaunchServices registration DB
///             (plutil → JSON), resolved via mdfind.
/// </summary>
public static class BrowserDetector
{
    public static List<BrowserCandidate> DetectAll()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return DetectWindows();
            if (OperatingSystem.IsMacOS()) return DetectMacOS();
            return DetectLinux();
        }
        catch
        {
            return new List<BrowserCandidate>();
        }
    }

    // ─── Linux ────────────────────────────────────────────────────────────

    private static List<BrowserCandidate> DetectLinux()
    {
        var candidates = new List<BrowserCandidate>();
        var seenBinaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaultDesktopId = GetDefaultBrowserDesktopIdLinux();

        foreach (var dir in GetLinuxDesktopApplicationDirs())
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.GetFiles(dir, "*.desktop"))
                {
                    var candidate = ParseLinuxDesktopFile(file, defaultDesktopId);
                    if (candidate == null) continue;
                    // Dedup by resolved binary path (symlink-safe).
                    var key = candidate.FlatpakAppId
                        ?? (candidate.BinaryPath.Length > 0 ? candidate.BinaryPath : candidate.DesktopId);
                    if (!seenBinaries.Add(key)) continue;
                    candidates.Add(candidate);
                }
            }
            catch { /* unreadable dir */ }
        }

        return candidates;
    }

    /// <summary>
    /// Parse a .desktop file into a browser candidate. Returns null when the file
    /// is not a browser (no http/https scheme handler, no WebBrowser category) or
    /// is hidden/non-application (same NoDisplay / Type gate as
    /// InstalledAppDetector.AddAppFromDesktopFile).
    /// </summary>
    private static BrowserCandidate? ParseLinuxDesktopFile(string filePath, string? defaultDesktopId)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            string? name = null, exec = null, icon = null;
            string? categories = null, mimeType = null;
            bool noDisplay = false, typeFound = false, isApplication = false;

            foreach (var line in lines)
            {
                if (line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase) && name == null)
                    name = line["Name=".Length..].Trim();
                if (line.StartsWith("Exec=", StringComparison.OrdinalIgnoreCase))
                    exec = line["Exec=".Length..].Trim();
                if (line.StartsWith("Icon=", StringComparison.OrdinalIgnoreCase))
                    icon = line["Icon=".Length..].Trim();
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

            // Same gate as the app detector: hidden or non-application entries are skipped.
            if (noDisplay || (typeFound && !isApplication)) return null;

            // Browser filter — the ONLY inclusion test. No name matching anywhere.
            var isBrowser = HasBrowserRegistration(categories, mimeType);
            if (!isBrowser) return null;
            if (string.IsNullOrWhiteSpace(name)) return null;

            var desktopId = Path.GetFileNameWithoutExtension(filePath);
            var (binaryPath, binaryName, flatpakAppId) = ResolveLinuxExec(exec);

            return new BrowserCandidate
            {
                Name = name,
                BinaryPath = binaryPath,
                BinaryName = binaryName,
                DesktopId = desktopId,
                Icon = icon ?? string.Empty,
                IsDefault = !string.IsNullOrEmpty(defaultDesktopId) &&
                            string.Equals(defaultDesktopId, desktopId, StringComparison.OrdinalIgnoreCase),
                FlatpakAppId = flatpakAppId,
            };
        }
        catch { return null; }
    }

    private static bool HasBrowserRegistration(string? categories, string? mimeType)
    {
        if (!string.IsNullOrEmpty(categories))
        {
            var cats = categories.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cats.Contains("WebBrowser", StringComparer.OrdinalIgnoreCase)) return true;
        }
        if (!string.IsNullOrEmpty(mimeType))
        {
            var mimes = mimeType.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (mimes.Contains("x-scheme-handler/http", StringComparer.OrdinalIgnoreCase) ||
                mimes.Contains("x-scheme-handler/https", StringComparer.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Turn a .desktop Exec line into an absolute binary path + binary name.
    /// Handles plain names (google-chrome-stable %U), absolute paths
    /// (/usr/bin/firefox %u), snap (/snap/bin/firefox %u) and flatpak
    /// (flatpak run org.gnome.Epiphany %u — binary name becomes the appid so
    /// profile roots derive to ~/.var/app/&lt;appid&gt;).
    /// </summary>
    private static (string binaryPath, string binaryName, string? flatpakAppId) ResolveLinuxExec(string? exec)
    {
        if (string.IsNullOrWhiteSpace(exec))
            return (string.Empty, string.Empty, null);

        var token = ExtractBinaryFromExec(exec);

        // flatpak run <appid>
        if (exec.Contains("flatpak", StringComparison.OrdinalIgnoreCase) &&
            exec.Contains("run", StringComparison.OrdinalIgnoreCase))
        {
            var appIdMatch = System.Text.RegularExpressions.Regex.Match(
                exec, @"flatpak\s+run\s+([a-zA-Z0-9_.\-]+\.[a-zA-Z0-9_.\-]+)");
            if (appIdMatch.Success)
            {
                var appId = appIdMatch.Groups[1].Value;
                var flatpakBinary = ResolveExecutablePath("flatpak");
                return (flatpakBinary, appId, appId);
            }
        }

        var resolved = ResolveExecutablePath(token);
        return (resolved, token ?? string.Empty, null);
    }

    private static string? GetDefaultBrowserDesktopIdLinux()
    {
        var xdg = RunAndCapture("xdg-settings", "get default-web-browser");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            var id = xdg.Trim();
            if (id.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
                id = id[..^".desktop".Length];
            return id;
        }
        var mime = RunAndCapture("xdg-mime", "query default x-scheme-handler/http");
        if (!string.IsNullOrWhiteSpace(mime))
        {
            var id = mime.Trim();
            if (id.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
                id = id[..^".desktop".Length];
            return id;
        }
        return null;
    }

    private static List<string> GetLinuxDesktopApplicationDirs()
    {
        var dirs = new List<string>();

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
            dirs.Add(Path.Combine(xdgDataHome, "applications"));
        dirs.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "applications"));

        var xdgDataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (!string.IsNullOrWhiteSpace(xdgDataDirs))
        {
            foreach (var entry in xdgDataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                dirs.Add(Path.Combine(entry, "applications"));
        }

        dirs.Add("/usr/share/applications");
        dirs.Add("/usr/local/share/applications");

        // Flatpak + snap exports are not always covered by XDG_DATA_DIRS.
        dirs.Add("/var/lib/flatpak/exports/share/applications");
        dirs.Add("/var/lib/snapd/desktop/applications");
        dirs.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "flatpak", "exports", "share", "applications"));

        return dirs;
    }

    // ─── Windows ──────────────────────────────────────────────────────────

    private static List<BrowserCandidate> DetectWindows()
    {
        var candidates = new List<BrowserCandidate>();
        try
        {
            const string root = @"HKLM\SOFTWARE\Clients\StartMenuInternet";
            var listing = RunAndCapture("reg.exe", $"query \"{root}\"");
            if (string.IsNullOrWhiteSpace(listing)) return candidates;

            var defaultExe = GetDefaultBrowserExeWindows();

            foreach (var line in listing.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                var subKey = trimmed[root.Length..].Trim('\\').Trim();
                if (string.IsNullOrEmpty(subKey) || subKey.Contains(' ')) continue;

                // Display name: (Default) value of the subkey.
                var name = ParseRegDefault(RunAndCapture("reg.exe", $"query \"{root}\\{subKey}\" /ve"));
                if (string.IsNullOrEmpty(name)) name = subKey;

                // Executable: (Default) value of ...\shell\open\command.
                var cmd = ParseRegDefault(RunAndCapture("reg.exe",
                    $"query \"{root}\\{subKey}\\shell\\open\\command\" /ve"));
                var exePath = StripExeQuotes(cmd);
                if (string.IsNullOrEmpty(exePath)) continue;

                candidates.Add(new BrowserCandidate
                {
                    Name = name,
                    BinaryPath = exePath,
                    BinaryName = Path.GetFileNameWithoutExtension(exePath),
                    DesktopId = subKey,
                    Icon = string.Empty,
                    IsDefault = !string.IsNullOrEmpty(defaultExe) &&
                                string.Equals(exePath.Trim('"'), defaultExe.Trim('"'), StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        catch { /* best-effort */ }
        return candidates;
    }

    private static string? GetDefaultBrowserExeWindows()
    {
        try
        {
            var progId = ParseRegValue(RunAndCapture("reg.exe",
                @"query ""HKCU\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice"" /v ProgId"),
                "ProgId");
            if (string.IsNullOrEmpty(progId)) return null;
            var cmd = ParseRegDefault(RunAndCapture("reg.exe",
                $"query \"HKCR\\{progId}\\shell\\open\\command\" /ve"));
            return StripExeQuotes(cmd);
        }
        catch { return null; }
    }

    // ─── macOS ────────────────────────────────────────────────────────────

    private static List<BrowserCandidate> DetectMacOS()
    {
        var candidates = new List<BrowserCandidate>();
        try
        {
            var plistPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Preferences", "com.apple.LaunchServices",
                "com.apple.launchservices.secure.plist");
            if (!File.Exists(plistPath)) return candidates;

            var json = RunAndCapture("plutil", $"-convert json -o - \"{plistPath}\"");
            if (string.IsNullOrWhiteSpace(json)) return candidates;

            string? defaultBundleId = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("LSHandlers", out var handlers) &&
                handlers.ValueKind == JsonValueKind.Array)
            {
                foreach (var handler in handlers.EnumerateArray())
                {
                    if (!handler.TryGetProperty("LSHandlerURLScheme", out var schemeEl)) continue;
                    var scheme = schemeEl.GetString();
                    if (!string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!handler.TryGetProperty("LSHandlerRoleAll", out var bundleEl)) continue;
                    var bundleId = bundleEl.GetString();
                    if (string.IsNullOrEmpty(bundleId)) continue;
                    if (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
                        defaultBundleId ??= bundleId;

                    if (!seen.Add(bundleId)) continue;

                    var appPath = ResolveMacAppPath(bundleId);
                    if (string.IsNullOrEmpty(appPath)) continue;

                    candidates.Add(new BrowserCandidate
                    {
                        Name = Path.GetFileNameWithoutExtension(appPath),
                        BinaryPath = appPath,
                        BinaryName = bundleId,
                        DesktopId = bundleId,
                        Icon = string.Empty,
                        IsDefault = string.Equals(bundleId, defaultBundleId, StringComparison.OrdinalIgnoreCase),
                    });
                }
            }
        }
        catch { /* best-effort */ }
        return candidates;
    }

    private static string? ResolveMacAppPath(string bundleId)
    {
        try
        {
            var out_ = RunAndCapture("mdfind", $"\"kMDItemCFBundleIdentifier == '{bundleId}'\"");
            if (string.IsNullOrWhiteSpace(out_)) return null;
            var line = out_.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.EndsWith(".app", StringComparison.OrdinalIgnoreCase));
            return line;
        }
        catch { return null; }
    }

    // ─── Shared helpers ───────────────────────────────────────────────────

    /// <summary>Extract the executable token from a desktop Exec line.</summary>
    public static string? ExtractBinaryFromExec(string? exec)
    {
        if (string.IsNullOrWhiteSpace(exec)) return null;
        exec = exec.Trim();
        var spaceIdx = exec.IndexOf(' ');
        var firstPart = spaceIdx > 0 ? exec[..spaceIdx] : exec;
        firstPart = firstPart.Trim('"');
        var binary = Path.GetFileNameWithoutExtension(firstPart);
        return string.IsNullOrWhiteSpace(binary) ? null : binary;
    }

    /// <summary>Resolve a bare binary name to its absolute path (PATH search), or return it unchanged if already absolute.</summary>
    private static string ResolveExecutablePath(string? binary)
    {
        if (string.IsNullOrWhiteSpace(binary)) return string.Empty;
        if (binary.Contains('/') && File.Exists(binary)) return binary;
        var which = RunAndCapture("which", binary);
        if (!string.IsNullOrWhiteSpace(which))
        {
            var path = which.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(path)) return path;
        }
        return binary.Contains('/') ? binary : binary;
    }

    private static string? ParseRegDefault(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("(Default)", StringComparison.OrdinalIgnoreCase))
            {
                var parts = t.Split(new[] { "REG_SZ" }, StringSplitOptions.None);
                return parts.Length > 1 ? parts[^1].Trim() : null;
            }
        }
        return null;
    }

    private static string? ParseRegValue(string? output, string valueName)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith(valueName, StringComparison.OrdinalIgnoreCase))
            {
                var parts = t.Split(new[] { "REG_SZ" }, StringSplitOptions.None);
                return parts.Length > 1 ? parts[^1].Trim() : null;
            }
        }
        return null;
    }

    private static string? StripExeQuotes(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 1) return cmd[1..end];
        }
        return cmd.Split(' ', 2)[0].Trim();
    }

    private static string RunAndCapture(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return string.Empty;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }
}

/// <summary>
/// Display-only brand catalog. It is NEVER used to gate scanning, inclusion, or
/// engine classification — it only pretties up the name/icon and provides a known
/// config-dir hint for browsers whose real user-data dir differs from the binary
/// name (e.g. BraveSoftware/Brave-Browser). A browser missing from this table is
/// still fully detected and classified.
/// </summary>
public static class BrandCatalog
{
    private static readonly Dictionary<string, (string Name, string ConfigDir)> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["google-chrome"] = ("Google Chrome", "google-chrome"),
            ["google-chrome-stable"] = ("Google Chrome", "google-chrome"),
            ["chromium"] = ("Chromium", "chromium"),
            ["chromium-browser"] = ("Chromium", "chromium"),
            ["brave-browser"] = ("Brave", "BraveSoftware/Brave-Browser"),
            ["microsoft-edge"] = ("Microsoft Edge", "microsoft-edge"),
            ["microsoft-edge-stable"] = ("Microsoft Edge", "microsoft-edge"),
            ["vivaldi"] = ("Vivaldi", "vivaldi"),
            ["opera"] = ("Opera", "opera"),
            ["firefox"] = ("Firefox", "mozilla/firefox"),
            ["firefox-esr"] = ("Firefox ESR", "mozilla/firefox"),
            ["zen-browser"] = ("Zen Browser", "zen"),
            ["librewolf"] = ("LibreWolf", "librewolf"),
            ["waterfox"] = ("Waterfox", "waterfox"),
            ["epiphany"] = ("GNOME Web", "epiphany"),
            ["falkon"] = ("Falkon", "falkon"),
        };

    public static (string? Name, string? ConfigDir) Find(BrowserCandidate c)
    {
        if (!string.IsNullOrEmpty(c.BinaryName) && Known.TryGetValue(c.BinaryName, out var byBin))
            return (byBin.Name, byBin.ConfigDir);
        if (!string.IsNullOrEmpty(c.DesktopId) && Known.TryGetValue(c.DesktopId, out var byId))
            return (byId.Name, byId.ConfigDir);

        // Name-overlap fallback for rebrands we don't list yet (e.g. desktop name
        // "Brave Software" → "Brave"): display-only, never gates anything.
        foreach (var kvp in Known)
        {
            if (c.Name.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.Contains(c.Name, StringComparison.OrdinalIgnoreCase))
                return (kvp.Value.Name, kvp.Value.ConfigDir);
        }
        return (null, null);
    }
}
