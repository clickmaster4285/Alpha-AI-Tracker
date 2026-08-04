using client.Core.Models;

namespace client.Core;

/// <summary>
/// Single-source-of-truth software classification. Runs AFTER InstalledAppDetector and
/// PackageDetector have both discovered raw entries. Responsibilities:
///
///   1. Generate a stable identity for every raw entry (via SoftwareIdentityResolver).
///   2. Dedup: an entry discovered by both detectors collapses to one record. GUI apps
///      (those with a .desktop / .app / registry entry) win and go to installed_applications;
///      their matching package entry is dropped. This fixes the Firefox-snap case where the
///      snap appeared in installed_packages instead of installed_applications.
///   3. Classify each surviving entry into exactly one category (SoftwareCategoryResolver).
///
/// The classifier does NOT perform discovery itself — it only normalizes and routes the
/// output of the existing detectors, preserving all current discovery logic.
/// </summary>
public static class SoftwareClassifier
{
    /// <summary>
    /// Partition raw discovered apps + packages into deduplicated classified sets.
    /// Apps returned are GUI applications (installed_applications); packages returned
    /// are CLI tools/runtimes/libraries (installed_packages). No software appears in both.
    /// </summary>
    public static (IReadOnlyList<InstalledApplication> apps, IReadOnlyList<InstalledPackage> packages) Classify(
        IReadOnlyList<InstalledApplication> rawApps,
        IReadOnlyList<InstalledPackage> rawPackages)
    {
        var appByIdentity = new Dictionary<string, InstalledApplication>(StringComparer.Ordinal);
        foreach (var app in rawApps)
        {
            var identity = SoftwareIdentityResolver.ResolveAppIdentity(app);
            // Classify the app using its Categories metadata (no hardcoded name lists).
            var category = SoftwareCategoryResolver.ResolveFromCategories(app.Categories, app.IsBrowser);
            // Only GUI categories belong in installed_applications; if somehow a non-GUI
            // entry reached the detector, it is dropped here (will be re-evaluated as a package).
            if (category == SoftwareCategoryResolver.Unknown && string.IsNullOrEmpty(app.BinaryName))
                continue;
            appByIdentity[identity] = app;
        }

        // Build a quick lookup of app identities to suppress matching package entries.
        var appIdentitySet = new HashSet<string>(appByIdentity.Keys, StringComparer.Ordinal);
        // Also build a name-based suppression set: if a package name equals an app binary/app name,
        // the package is the same software (snap/flatpak GUI app) and must be dropped.
        var appNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in appByIdentity.Values)
        {
            if (!string.IsNullOrEmpty(a.BinaryName)) appNames.Add(a.BinaryName);
            if (!string.IsNullOrEmpty(a.AppName)) appNames.Add(a.AppName);
            if (!string.IsNullOrEmpty(a.DesktopId))
            {
                // snap desktop_id "firefox_firefox" → package name "firefox" matches
                var baseId = a.DesktopId.Split('_')[0];
                if (!string.IsNullOrEmpty(baseId)) appNames.Add(baseId);
            }
        }

        var packages = new List<InstalledPackage>();
        var pkgIdentitySet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pkg in rawPackages)
        {
            // Suppress a package if its name matches a discovered GUI app (one software = one identity).
            if (appNames.Contains(pkg.PackageName)) continue;
            var identity = SoftwareIdentityResolver.ResolvePackageIdentity(pkg);
            if (!pkgIdentitySet.Add(identity)) continue; // dedup identical packages
            // Classify the package (runtime/tool/library) and persist the canonical category.
            pkg.Category = SoftwareCategoryResolver.ResolveForPackage(pkg.PackageName, pkg.SourceManager, pkg.Category);
            packages.Add(pkg);
        }

        return (appByIdentity.Values.ToList(), packages);
    }
}
