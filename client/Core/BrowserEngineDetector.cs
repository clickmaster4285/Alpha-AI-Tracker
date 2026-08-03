using System.Diagnostics;
using System.Text.Json;

namespace client.Core;

/// <summary>Which browser engine family a detected browser uses.</summary>
public enum BrowserEngine { Unknown, Chromium, Gecko }

/// <summary>
/// Separates "is this a browser" (BrowserDetector) from "which engine it uses".
/// Classification is done from PROFILE-DIRECTORY SHAPE, never from the binary
/// name — forks and rebrands (Zen, LibreWolf, Waterfox, Brave…) change names but
/// keep the underlying profile format:
///   Chromium signature → &lt;root&gt;/&lt;profile&gt;/Preferences (JSON) and/or Local State.
///   Gecko signature     → &lt;root&gt;/profiles.ini and/or &lt;profile&gt;/prefs.js | user.js.
///
/// SCOPE GUARD: the fallback corpus sweep is never a blind filesystem scan. It
/// only inspects standard roots that overlap a candidate BrowserDetector already
/// confirmed via OS-level http/https handler registration. This is what excludes
/// Electron apps (Slack, Discord, VS Code, …): they have Chromium-shaped
/// Preferences files, but they never register as http/https handlers, so they
/// never become candidates — and the sweep never inspects their roots.
/// </summary>
public static class BrowserEngineDetector
{
    public static BrowserEngine DetectFor(BrowserCandidate candidate)
    {
        // 1. Roots derived directly from the confirmed candidate.
        foreach (var root in DeriveRoots(candidate))
        {
            var engine = ClassifyRoot(root);
            if (engine != BrowserEngine.Unknown) return engine;
        }

        // 2. Correlated corpus sweep — standard roots overlapping the candidate.
        foreach (var root in CorrelatedSweepRoots(candidate))
        {
            var engine = ClassifyRoot(root);
            if (engine != BrowserEngine.Unknown) return engine;
        }

        return BrowserEngine.Unknown;
    }

    /// <summary>Classify a single profile root by its on-disk shape.</summary>
    public static BrowserEngine ClassifyRoot(string root)
    {
        try
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return BrowserEngine.Unknown;

            // Gecko: profiles.ini at the root is the definitive signature.
            if (File.Exists(Path.Combine(root, "profiles.ini"))) return BrowserEngine.Gecko;
            // Gecko secondary: compatibility.ini.
            if (File.Exists(Path.Combine(root, "compatibility.ini"))) return BrowserEngine.Gecko;
            // Chromium secondary: Local State JSON at the user-data root.
            if (File.Exists(Path.Combine(root, "Local State"))) return BrowserEngine.Chromium;

            // Per-profile subdirectories.
            foreach (var sub in Directory.GetDirectories(root))
            {
                var prefs = Path.Combine(sub, "Preferences");
                if (File.Exists(prefs))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllBytes(prefs));
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                            return BrowserEngine.Chromium;
                    }
                    catch { /* unreadable/corrupt — not a Chromium signature */ }
                }
                if (File.Exists(Path.Combine(sub, "prefs.js")) || File.Exists(Path.Combine(sub, "user.js")))
                    return BrowserEngine.Gecko;
            }

            // Rare: prefs.js directly at the root.
            if (File.Exists(Path.Combine(root, "prefs.js"))) return BrowserEngine.Gecko;
        }
        catch { }
        return BrowserEngine.Unknown;
    }

    /// <summary>
    /// Find the resolved profile root + default profile directory for a candidate
    /// (same probe order as <see cref="DetectFor"/>). Returns null when the
    /// candidate has no attributable profile (engine Unknown).
    /// </summary>
    public static (string Root, string DefaultProfile)? FindProfileRoot(BrowserCandidate candidate)
    {
        foreach (var root in DeriveRoots(candidate).Concat(CorrelatedSweepRoots(candidate)))
        {
            var engine = ClassifyRoot(root);
            if (engine == BrowserEngine.Unknown) continue;
            return (root, FindDefaultProfile(root, engine));
        }
        return null;
    }

    /// <summary>
    /// Resolve the default profile directory name inside a classified root.
    /// Gecko: read profiles.ini and return the Path= of the [Profile*] marked
    /// Default=1 (falling back to the first profile). Chromium: first subdir with
    /// a parseable Preferences file (usually "Default").
    /// </summary>
    private static string FindDefaultProfile(string root, BrowserEngine engine)
    {
        try
        {
            if (engine == BrowserEngine.Gecko)
            {
                var iniPath = Path.Combine(root, "profiles.ini");
                if (File.Exists(iniPath))
                {
                    // Parse section-by-section into a dictionary so key ORDER inside a
                    // [ProfileN] section never matters (Default=1 can precede Path=).
                    string? firstProfile = null;
                    string? defaultProfile = null;
                    var section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in File.ReadAllLines(iniPath))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal))
                        {
                            EvaluateSection(section, ref firstProfile, ref defaultProfile);
                            section.Clear();
                            continue;
                        }
                        var eq = t.IndexOf('=');
                        if (eq > 0)
                            section[t[..eq].Trim()] = t[(eq + 1)..].Trim();
                    }
                    EvaluateSection(section, ref firstProfile, ref defaultProfile);
                    return defaultProfile ?? firstProfile ?? string.Empty;
                }
                return string.Empty;
            }

            // Chromium: first subdir whose Preferences parses as JSON.
            foreach (var sub in Directory.GetDirectories(root))
            {
                var prefs = Path.Combine(sub, "Preferences");
                if (File.Exists(prefs))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllBytes(prefs));
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                            return Path.GetFileName(sub);
                    }
                    catch { /* not JSON */ }
                }
            }
            return "Default";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void EvaluateSection(
        Dictionary<string, string> section, ref string? firstProfile, ref string? defaultProfile)
    {
        if (section.Count == 0) return;
        var path = section.GetValueOrDefault("Path");
        if (string.IsNullOrEmpty(path)) return;
        firstProfile ??= path;
        if (defaultProfile == null && section.GetValueOrDefault("Default") == "1")
            defaultProfile = path;
    }

    /// <summary>
    /// Roots derived from the confirmed candidate itself: config dir named after
    /// the binary/desktop-id, plus the platform-specific wrappers (snap, flatpak,
    /// %LOCALAPPDATA%, ~/Library/Application Support) and the display-only
    /// BrandCatalog config-dir hint. No list gates anything here — every root is
    /// simply probed for a profile signature.
    /// </summary>
    private static List<string> DeriveRoots(BrowserCandidate c)
    {
        var roots = new List<string>();
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var (_, configHint) = BrandCatalog.Find(c);
        var names = new List<string>();
        if (!string.IsNullOrEmpty(c.BinaryName)) names.Add(c.BinaryName);
        if (!string.IsNullOrEmpty(c.DesktopId)) names.Add(c.DesktopId);
        if (!string.IsNullOrEmpty(configHint)) names.Add(configHint.Replace('/', Path.DirectorySeparatorChar));

        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (OperatingSystem.IsLinux())
            {
                roots.Add(Path.Combine(userHome, ".config", name));
                // snap wrapper: ~/snap/<name>/common/.mozilla/firefox (Firefox) and the snap root itself.
                var snapCommon = Path.Combine(userHome, "snap", name, "common");
                roots.Add(Path.Combine(snapCommon, ".mozilla", "firefox"));
                roots.Add(Path.Combine(snapCommon, ".config", name));
                roots.Add(Path.Combine(userHome, "snap", name));
            }
            else if (OperatingSystem.IsWindows())
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                roots.Add(Path.Combine(localAppData, name, "User Data"));
                roots.Add(Path.Combine(localAppData, name));
                roots.Add(Path.Combine(appData, name));
            }
            else if (OperatingSystem.IsMacOS())
            {
                roots.Add(Path.Combine(userHome, "Library", "Application Support", name));
                roots.Add(Path.Combine(userHome, "Library", "Application Support", name, "Default"));
            }
        }

        // flatpak: ~/.var/app/<appid>/config
        if (!string.IsNullOrEmpty(c.FlatpakAppId))
            roots.Add(Path.Combine(userHome, ".var", "app", c.FlatpakAppId, "config"));

        return roots;
    }

    /// <summary>
    /// Standard-root sweep, gated by correlation with the confirmed candidate
    /// (path-name overlap only — never a blind scan, per the scope guard above).
    /// Catches renamed user-data dirs like BraveSoftware/Brave-Browser.
    /// </summary>
    private static List<string> CorrelatedSweepRoots(BrowserCandidate c)
    {
        var roots = new List<string>();
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        IEnumerable<string> enumerable;
        if (OperatingSystem.IsLinux())
        {
            var config = Path.Combine(userHome, ".config");
            enumerable = Directory.Exists(config)
                ? Directory.GetDirectories(config)
                : Array.Empty<string>();
        }
        else if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dirs = new List<string>();
            if (Directory.Exists(localAppData))
                dirs.AddRange(Directory.GetDirectories(localAppData));
            if (Directory.Exists(appData))
                dirs.AddRange(Directory.GetDirectories(appData));
            enumerable = dirs;
        }
        else
        {
            var appSupport = Path.Combine(userHome, "Library", "Application Support");
            enumerable = Directory.Exists(appSupport)
                ? Directory.GetDirectories(appSupport)
                : Array.Empty<string>();
        }

        foreach (var root in enumerable)
        {
            var name = Path.GetFileName(root);
            if (!string.IsNullOrEmpty(name) && Overlaps(name, c))
                roots.Add(root);
        }

        // Linux snap + flatpak wrapper roots, correlated the same way.
        if (OperatingSystem.IsLinux())
        {
            var snapRoot = Path.Combine(userHome, "snap");
            if (Directory.Exists(snapRoot))
            {
                foreach (var snapDir in Directory.GetDirectories(snapRoot))
                {
                    var name = Path.GetFileName(snapDir);
                    if (!string.IsNullOrEmpty(name) && Overlaps(name, c))
                    {
                        roots.Add(Path.Combine(snapDir, "common", ".mozilla", "firefox"));
                        roots.Add(Path.Combine(snapDir, "common", ".config"));
                    }
                }
            }
        }

        return roots;
    }

    private static bool Overlaps(string rootName, BrowserCandidate c)
    {
        return rootName.Contains(c.Name, StringComparison.OrdinalIgnoreCase) ||
               c.Name.Contains(rootName, StringComparison.OrdinalIgnoreCase) ||
               rootName.Contains(c.BinaryName, StringComparison.OrdinalIgnoreCase) ||
               c.BinaryName.Contains(rootName, StringComparison.OrdinalIgnoreCase) ||
               rootName.Contains(c.DesktopId, StringComparison.OrdinalIgnoreCase) ||
               c.DesktopId.Contains(rootName, StringComparison.OrdinalIgnoreCase);
    }
}
