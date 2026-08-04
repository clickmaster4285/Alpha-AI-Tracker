using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace client.Core;

/// <summary>
/// Seed Chromium profiles by launching the browser once per
/// <c>--profile-directory</c> with <c>--load-extension</c>. That is the only
/// reliable way to turn on developer mode + persist an unpacked extension:
/// the browser writes Secure Preferences itself (valid HMAC).
/// <para>
/// Idempotent: each Install / Setup All re-reads <c>info_cache</c> and seeds
/// only profiles that lack the extension entry.
/// </para>
/// </summary>
public static class ChromiumProfileSeeder
{
    public const int DefaultPerProfileTimeoutSec = 45;

    public sealed record ProfileSeedOutcome(string Profile, string Status, string? ExtensionId);

    public sealed record SeedResult(
        int ProfilesSeeded,
        int ProfilesSkipped,
        int ProfilesFailed,
        string? LastUsedProfile,
        string? FirstExtensionId,
        IReadOnlyList<ProfileSeedOutcome> Outcomes);

    /// <summary>Profile directory names from Local State (falls back to folder scan).</summary>
    public static List<string> ListProfileDirectoryNames(string userDataDir, string? preferFirst = null)
    {
        var names = new List<string>();
        var localState = Path.Combine(userDataDir, "Local State");
        try
        {
            if (File.Exists(localState))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(localState));
                if (doc.RootElement.TryGetProperty("profile", out var profile) &&
                    profile.TryGetProperty("info_cache", out var cache))
                {
                    foreach (var prop in cache.EnumerateObject())
                    {
                        var dir = Path.Combine(userDataDir, prop.Name);
                        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "Preferences")))
                            names.Add(prop.Name);
                    }
                }
            }
        }
        catch { }

        if (names.Count == 0)
        {
            foreach (var dir in ChromiumProfilePropagator.EnumerateProfiles(userDataDir))
                names.Add(Path.GetFileName(dir)!);
        }

        names.Sort((a, b) =>
        {
            int Rank(string n)
            {
                if (!string.IsNullOrEmpty(preferFirst) &&
                    n.Equals(preferFirst, StringComparison.OrdinalIgnoreCase))
                    return 0;
                if (n.Equals("Default", StringComparison.OrdinalIgnoreCase)) return 1;
                return 2;
            }
            var cmp = Rank(a).CompareTo(Rank(b));
            return cmp != 0 ? cmp : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? GetLastUsedProfile(string userDataDir)
    {
        try
        {
            var localState = Path.Combine(userDataDir, "Local State");
            if (!File.Exists(localState)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(localState));
            if (doc.RootElement.TryGetProperty("profile", out var profile) &&
                profile.TryGetProperty("last_used", out var last))
                return last.GetString();
        }
        catch { }
        return null;
    }

    public static bool ProfileHasExtension(string userDataDir, string profileName, string extensionDir)
        => FindExtensionId(userDataDir, profileName, extensionDir) != null;

    /// <summary>
    /// Re-read current profiles; seed only those missing the extension; skip the rest.
    /// </summary>
    public static async Task<SeedResult> SeedAllProfilesAsync(
        string browserBinary,
        string? flatpakAppId,
        string userDataDir,
        string extensionDir,
        Func<Task> killAndWaitAsync,
        ILogger? logger = null,
        int perProfileTimeoutSec = DefaultPerProfileTimeoutSec)
    {
        var lastUsed = GetLastUsedProfile(userDataDir);
        var profiles = ListProfileDirectoryNames(userDataDir, preferFirst: lastUsed);
        var seeded = 0;
        var skipped = 0;
        var failed = 0;
        string? firstId = null;
        var extFull = Path.GetFullPath(extensionDir);
        var outcomes = new List<ProfileSeedOutcome>();

        if (!Directory.Exists(extFull) || !File.Exists(Path.Combine(extFull, "manifest.json")))
        {
            logger?.LogError(
                "Staged extension path missing or has no manifest.json: {Path} (bucket=staged_path)",
                extFull);
            return new SeedResult(0, 0, profiles.Count, lastUsed, null,
                profiles.Select(p => new ProfileSeedOutcome(p, "failed:staged_path", null)).ToList());
        }

        logger?.LogInformation(
            "Idempotent seed of {Count} Chromium profiles under {UserData} (last_used={Last}, timeout={Sec}s, ext={Ext})",
            profiles.Count, userDataDir, lastUsed ?? "(none)", perProfileTimeoutSec, extFull);

        foreach (var profileName in profiles)
        {
            try
            {
                var existingId = FindExtensionId(userDataDir, profileName, extFull);
                if (existingId != null)
                {
                    skipped++;
                    firstId ??= existingId;
                    outcomes.Add(new ProfileSeedOutcome(profileName, "skipped", existingId));
                    logger?.LogInformation(
                        "Skip profile {Profile} — extension already present (id={Id})",
                        profileName, existingId);
                    continue;
                }

                await killAndWaitAsync();
                // Only clear locks after the browser process is gone — otherwise
                // Chromium may corrupt shutdown and the next launch ignores argv.
                ClearSingletonLocks(userDataDir);

                var args =
                    $"--user-data-dir=\"{userDataDir}\" " +
                    $"--profile-directory=\"{profileName}\" " +
                    $"--load-extension=\"{extFull}\" " +
                    "--disable-features=DisableLoadExtensionCommandLineSwitch " +
                    "--no-first-run --disable-gcm --new-window about:blank";

                var (file, launchArgs) = BuildLaunch(browserBinary, flatpakAppId, args);
                logger?.LogInformation(
                    "Seeding profile {Profile}: {File} {Args}", profileName, file, launchArgs);

                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = launchArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (proc == null)
                {
                    failed++;
                    outcomes.Add(new ProfileSeedOutcome(profileName, "failed:launch", null));
                    logger?.LogWarning("Seed launch failed for profile {Profile} (bucket=launch)", profileName);
                    continue;
                }

                var ok = await WaitProfileHasExtensionAsync(
                    userDataDir, profileName, extFull, TimeSpan.FromSeconds(perProfileTimeoutSec), logger);
                if (ok)
                {
                    var id = FindExtensionId(userDataDir, profileName, extFull);
                    seeded++;
                    firstId ??= id;
                    outcomes.Add(new ProfileSeedOutcome(profileName, "seeded", id));
                    logger?.LogInformation("Seeded profile {Profile} (id={Id})", profileName, id);
                }
                else
                {
                    failed++;
                    outcomes.Add(new ProfileSeedOutcome(profileName, "failed:timeout", null));
                    logger?.LogWarning(
                        "Seed timeout for profile {Profile} after {Sec}s (bucket=seed_timeout)",
                        profileName, perProfileTimeoutSec);
                }
            }
            catch (Exception ex)
            {
                failed++;
                outcomes.Add(new ProfileSeedOutcome(profileName, "failed:exception", null));
                logger?.LogWarning(ex, "Seed failed for profile {Profile} (bucket=exception)", profileName);
            }
        }

        await killAndWaitAsync();
        logger?.LogInformation(
            "Seed done: seeded={Seeded} skipped={Skipped} failed={Failed}",
            seeded, skipped, failed);
        return new SeedResult(seeded, skipped, failed, lastUsed, firstId, outcomes);
    }

    public static (string file, string args) BuildLaunch(string binary, string? flatpakAppId, string args)
    {
        if (!string.IsNullOrEmpty(flatpakAppId))
            return ("flatpak", $"run {flatpakAppId} {args}".Trim());
        return (binary, args);
    }

    public static void ClearSingletonLocks(string userDataDir)
    {
        foreach (var name in new[] { "SingletonLock", "SingletonCookie", "SingletonSocket" })
        {
            try
            {
                var p = Path.Combine(userDataDir, name);
                if (File.Exists(p)) File.Delete(p);
                if (Directory.Exists(p)) Directory.Delete(p, true);
            }
            catch { }
        }
    }

    private static async Task<bool> WaitProfileHasExtensionAsync(
        string userDataDir, string profileName, string extensionDir, TimeSpan timeout, ILogger? logger)
    {
        var deadline = DateTime.UtcNow + timeout;
        var prefsPath = Path.Combine(userDataDir, profileName, "Preferences");
        var securePath = Path.Combine(userDataDir, profileName, "Secure Preferences");
        DateTime? lastPrefsWrite = null;
        DateTime? lastSecureWrite = null;

        while (DateTime.UtcNow < deadline)
        {
            if (FindExtensionId(userDataDir, profileName, extensionDir) != null)
            {
                // Prefer a brief settle so we don't kill mid-flush.
                await Task.Delay(500);
                if (FindExtensionId(userDataDir, profileName, extensionDir) != null)
                    return true;
            }

            try
            {
                if (File.Exists(prefsPath))
                {
                    var wt = File.GetLastWriteTimeUtc(prefsPath);
                    if (lastPrefsWrite != null && wt > lastPrefsWrite)
                        logger?.LogDebug("Preferences changed for {Profile}", profileName);
                    lastPrefsWrite = wt;
                }
                if (File.Exists(securePath))
                {
                    var wt = File.GetLastWriteTimeUtc(securePath);
                    if (lastSecureWrite != null && wt > lastSecureWrite)
                        logger?.LogDebug("Secure Preferences changed for {Profile}", profileName);
                    lastSecureWrite = wt;
                }
            }
            catch { }

            await Task.Delay(500);
        }
        return false;
    }

    internal static string? FindExtensionId(string userDataDir, string profileName, string extensionDir)
    {
        var extNorm = Path.GetFullPath(extensionDir).Replace('\\', '/');
        foreach (var fileName in new[] { "Secure Preferences", "Preferences" })
        {
            var path = Path.Combine(userDataDir, profileName, fileName);
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("extensions", out var exts)) continue;
                if (!exts.TryGetProperty("settings", out var settings)) continue;
                foreach (var prop in settings.EnumerateObject())
                {
                    if (!prop.Value.TryGetProperty("path", out var pathEl)) continue;
                    var p = (pathEl.GetString() ?? "").Replace('\\', '/');
                    if (p.Contains(extNorm, StringComparison.OrdinalIgnoreCase) ||
                        extNorm.Contains(p, StringComparison.OrdinalIgnoreCase))
                        return prop.Name;
                }
            }
            catch { }
        }
        return null;
    }
}
