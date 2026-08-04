using System.Diagnostics;
using System.Text.Json;

namespace client.Core;

/// <summary>Which browser engine family a detected browser uses.</summary>
public enum BrowserEngine { Unknown, Chromium, Gecko, WebKit }

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
        foreach (var root in EnumerateCandidateRoots(candidate))
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
            // Chromium: Local State JSON at the user-data root.
            if (File.Exists(Path.Combine(root, "Local State"))) return BrowserEngine.Chromium;

            // Windows Chromium layout: <Vendor>/<Product>/User Data/Local State
            // (probe stops at <Product> without this one-level descent).
            var userData = Path.Combine(root, "User Data");
            if (File.Exists(Path.Combine(userData, "Local State"))) return BrowserEngine.Chromium;

            // WebKit: engine-shaped data dirs (no brand names — file signatures only).
            if (IsWebKitRoot(root)) return BrowserEngine.WebKit;

            // Per-profile subdirectories (Chromium Default/Preferences, Gecko prefs.js).
            foreach (var sub in Directory.GetDirectories(root))
            {
                var leaf = Path.GetFileName(sub);
                // Descend into User Data when present (Windows Chromium).
                if (string.Equals(leaf, "User Data", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(Path.Combine(sub, "Local State"))) return BrowserEngine.Chromium;
                    foreach (var profile in Directory.GetDirectories(sub))
                    {
                        if (HasChromiumPreferences(profile)) return BrowserEngine.Chromium;
                    }
                }

                if (HasChromiumPreferences(sub)) return BrowserEngine.Chromium;
                if (File.Exists(Path.Combine(sub, "prefs.js")) || File.Exists(Path.Combine(sub, "user.js")))
                    return BrowserEngine.Gecko;
                if (IsWebKitRoot(sub)) return BrowserEngine.WebKit;
            }

            // Rare: prefs.js directly at the root.
            if (File.Exists(Path.Combine(root, "prefs.js"))) return BrowserEngine.Gecko;
        }
        catch { }
        return BrowserEngine.Unknown;
    }

    private static bool HasChromiumPreferences(string profileDir)
    {
        var prefs = Path.Combine(profileDir, "Preferences");
        if (!File.Exists(prefs)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(prefs));
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }

    /// <summary>
    /// WebKit profile signatures — file/dir shapes only (History.db + Bookmarks.plist
    /// for Safari-style; ephy-bookmarks.db / WebKitCache for WebKitGTK). Never matches
    /// on application name.
    /// </summary>
    private static bool IsWebKitRoot(string root)
    {
        if (File.Exists(Path.Combine(root, "History.db")) &&
            (File.Exists(Path.Combine(root, "Bookmarks.plist")) ||
             File.Exists(Path.Combine(root, "LastSession.plist"))))
            return true;

        if (File.Exists(Path.Combine(root, "ephy-bookmarks.db")) ||
            File.Exists(Path.Combine(root, "ephy-history.db")))
            return true;

        if (Directory.Exists(Path.Combine(root, "WebKitCache")) ||
            Directory.Exists(Path.Combine(root, "webkit")))
            return true;

        return false;
    }

    /// <summary>
    /// Find the resolved profile root + default profile directory for a candidate
    /// (same probe order as <see cref="DetectFor"/>). Returns null when the
    /// candidate has no attributable profile (engine Unknown).
    /// </summary>
    public static (string Root, string DefaultProfile)? FindProfileRoot(BrowserCandidate candidate)
    {
        foreach (var root in EnumerateCandidateRoots(candidate))
        {
            var engine = ClassifyRoot(root);
            if (engine == BrowserEngine.Unknown) continue;

            // Normalize to the real Chromium user-data dir when we matched a parent.
            var resolved = ResolveChromiumUserDataDir(root) ?? root;
            return (resolved, FindDefaultProfile(resolved, engine));
        }
        return null;
    }

    /// <summary>All profile-root probes for a candidate (order matters).</summary>
    private static IEnumerable<string> EnumerateCandidateRoots(BrowserCandidate candidate) =>
        DeriveRoots(candidate)
            .Concat(RootsFromBinaryPath(candidate))
            .Concat(CorrelatedSweepRoots(candidate))
            .Concat(TwoLevelSweepRoots(candidate));

    /// <summary>
    /// From the browser exe path (…/Application/chrome.exe), walk up and look for a
    /// sibling <c>User Data</c> directory — the standard Windows Chromium layout,
    /// without any brand-name dictionary.
    /// </summary>
    private static List<string> RootsFromBinaryPath(BrowserCandidate c)
    {
        var roots = new List<string>();
        try
        {
            var path = c.BinaryPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return roots;

            var dir = Path.GetDirectoryName(path);
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
            {
                var userData = Path.Combine(dir, "User Data");
                if (Directory.Exists(userData))
                    roots.Add(userData);

                // Also try the directory itself (may already be the product folder).
                roots.Add(dir);

                var parent = Path.GetDirectoryName(dir);
                if (parent == null || parent == dir) break;
                dir = parent;
            }
        }
        catch { }
        return roots;
    }

    /// <summary>If <paramref name="root"/> contains User Data/Local State, return that path.</summary>
    private static string? ResolveChromiumUserDataDir(string root)
    {
        if (File.Exists(Path.Combine(root, "Local State"))) return root;
        var userData = Path.Combine(root, "User Data");
        if (File.Exists(Path.Combine(userData, "Local State"))) return userData;
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
    /// %LOCALAPPDATA%, ~/Library/Application Support).
    ///
    /// Config-dir resolution is entirely generic — no brand-keyed dictionary.
    /// Every name candidate (BinaryName, DesktopId, stripped variants) is simply
    /// probed for a profile signature; first hit wins. This catches Brave's
    /// (BraveSoftware/Brave-Browser) path and any other non-obvious layout
    /// without a per-brand shortcut.
    /// </summary>
    private static List<string> DeriveRoots(BrowserCandidate c)
    {
        var roots = new List<string>();
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Generic multi-candidate probe: try BinaryName, DesktopId, and
        // stripped variants (remove -browser, -stable, -esr suffixes).
        // No dictionary hint — every candidate is just a path probe that
        // succeeds only if a real profile file is found there.
        var names = new List<string>();
        if (!string.IsNullOrEmpty(c.BinaryName)) names.Add(c.BinaryName);
        if (!string.IsNullOrEmpty(c.DesktopId)) names.Add(c.DesktopId);
        // Stripped variants for non-obvious paths like BraveSoftware/Brave-Browser
        if (!string.IsNullOrEmpty(c.BinaryName))
        {
            foreach (var suffix in new[] { "-browser", "-stable", "-esr" })
            {
                if (c.BinaryName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    names.Add(c.BinaryName[..^suffix.Length]);
            }
        }

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
                // Safari (WebKit) keeps its profile under ~/Library/Safari — only
                // accepted when ClassifyRoot finds WebKit signatures there.
                roots.Add(Path.Combine(userHome, "Library", "Safari"));
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
        if (string.IsNullOrEmpty(rootName)) return false;
        // Empty needles make string.Contains return true for every haystack — skip them.
        return (!string.IsNullOrEmpty(c.Name) &&
                    (rootName.Contains(c.Name, StringComparison.OrdinalIgnoreCase) ||
                     c.Name.Contains(rootName, StringComparison.OrdinalIgnoreCase))) ||
               (!string.IsNullOrEmpty(c.BinaryName) &&
                    (rootName.Contains(c.BinaryName, StringComparison.OrdinalIgnoreCase) ||
                     c.BinaryName.Contains(rootName, StringComparison.OrdinalIgnoreCase))) ||
               (!string.IsNullOrEmpty(c.DesktopId) &&
                    (rootName.Contains(c.DesktopId, StringComparison.OrdinalIgnoreCase) ||
                     c.DesktopId.Contains(rootName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Structural two-level probe (STEP 1 fix, 2026-08-03): catches vendor-folder
    /// layouts like BraveSoftware/Brave-Browser where the real user-data root sits
    /// TWO levels below the config home (~/.config/&lt;Vendor&gt;/&lt;Browser&gt; on Linux,
    /// %LOCALAPPDATA%/&lt;Vendor&gt;/&lt;Browser&gt; on Windows, ~/Library/Application
    /// Support/&lt;Vendor&gt;/&lt;Browser&gt; on macOS).
    ///
    /// NOT name-keyed and NOT a per-brand dictionary: the two-level path is
    /// generated structurally from the config home, and it is only accepted when
    /// (a) the leaf or parent dir name correlates with the confirmed candidate
    /// (same Overlaps gate as the one-level sweep — this is what excludes unrelated
    /// Electron app configs from misattribution) AND (b) ClassifyRoot finds a real
    /// profile signature file (Preferences / Local State / profiles.ini / prefs.js)
    /// at that path. A future browser with a new vendor-folder layout is caught
    /// automatically — no dictionary entry needed.
    /// </summary>
    private static List<string> TwoLevelSweepRoots(BrowserCandidate c)
    {
        var roots = new List<string>();
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        IEnumerable<string> level1;
        if (OperatingSystem.IsLinux())
        {
            var config = Path.Combine(userHome, ".config");
            level1 = Directory.Exists(config)
                ? Directory.GetDirectories(config)
                : Array.Empty<string>();
        }
        else if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            level1 = Directory.Exists(localAppData)
                ? Directory.GetDirectories(localAppData)
                : Array.Empty<string>();
        }
        else
        {
            var appSupport = Path.Combine(userHome, "Library", "Application Support");
            level1 = Directory.Exists(appSupport)
                ? Directory.GetDirectories(appSupport)
                : Array.Empty<string>();
        }

        foreach (var l1 in level1)
        {
            var l1Name = Path.GetFileName(l1);
            if (string.IsNullOrEmpty(l1Name)) continue;
            IEnumerable<string> l2s;
            try { l2s = Directory.GetDirectories(l1); }
            catch { continue; }

            foreach (var l2 in l2s)
            {
                var l2Name = Path.GetFileName(l2);
                if (string.IsNullOrEmpty(l2Name)) continue;

                // Correlation gate: leaf OR parent overlaps the confirmed candidate.
                // (BraveSoftware/Brave-Browser matches via leaf "Brave-Browser" vs
                //  binary "brave-browser"; parent "BraveSoftware" alone does not,
                //  which is exactly why the one-level sweep missed it.)
                if (!Overlaps(l1Name, c) && !Overlaps(l2Name, c)) continue;

                // Final gate: a real profile signature must exist here — the
                // structural sweep never invents a root for empty dirs.
                if (ClassifyRoot(l2) == BrowserEngine.Unknown) continue;

                roots.Add(l2);
                // Windows Chromium: real Local State lives under User Data/.
                var userData = Path.Combine(l2, "User Data");
                if (Directory.Exists(userData))
                    roots.Add(userData);
            }
        }

        return roots;
    }
}
