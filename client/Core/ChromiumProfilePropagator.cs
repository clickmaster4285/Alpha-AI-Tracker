using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace client.Core;

/// <summary>
/// After Chromium itself writes a valid unpacked-extension entry (via
/// <c>--load-extension</c>) into one seed profile, copy that browser-authored
/// entry into every other profile under the same user-data dir.
/// <para>
/// Important: some profiles store the unpacked extension in <c>Preferences</c>
/// (no HMAC), others in <c>Secure Preferences</c> (with HMAC). Always search
/// both. Never require a MAC for Preferences-sourced entries — requiring one
/// caused Profile 15 installs to be invisible to the propagator.
/// </para>
/// </summary>
public static class ChromiumProfilePropagator
{
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Profile", "Guest Profile", "Crashpad", "Snapshots", "Safe Browsing",
        "External Extensions", "component_crx_cache", "extensions_crx_cache",
        "ShaderCache", "GrShaderCache", "GraphiteDawnCache", "GPUPersistentCache",
        "BrowserMetrics", "DeferredBrowserMetrics", "NativeMessagingHosts",
    };

    public sealed record PropagateResult(string ExtensionId, string SeedProfile, int ProfilesUpdated);

    private sealed record SeedHit(
        string Profile,
        string ExtId,
        JsonNode Entry,
        string? Mac,
        string? DevMac,
        bool FromSecurePreferences);

    public static PropagateResult? PropagateFromSeed(
        string userDataDir, string extensionDir, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(userDataDir) || !Directory.Exists(userDataDir))
            return null;

        var extFull = Path.GetFullPath(extensionDir).Replace('\\', '/');
        var seed = FindSeed(userDataDir, extFull, logger);
        if (seed == null)
        {
            logger?.LogWarning(
                "No browser-authored extension entry found under {UserData} for {Ext}",
                userDataDir, extensionDir);
            return null;
        }

        var updated = 0;
        foreach (var profileDir in EnumerateProfiles(userDataDir))
        {
            var name = Path.GetFileName(profileDir);
            if (string.Equals(name, seed.Profile, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (WriteEntry(profileDir, seed, logger))
                    updated++;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Propagate failed for {Profile}", name);
            }
        }

        logger?.LogInformation(
            "Propagated extension {Id} from {Seed} ({Src}) to {N} profiles",
            seed.ExtId, seed.Profile, seed.FromSecurePreferences ? "Secure Preferences" : "Preferences", updated);
        return new PropagateResult(seed.ExtId, seed.Profile, updated);
    }

    public static IEnumerable<string> EnumerateProfiles(string userDataDir)
    {
        foreach (var dir in Directory.GetDirectories(userDataDir))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || SkipDirNames.Contains(name)) continue;
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            if (!File.Exists(Path.Combine(dir, "Preferences"))) continue;
            yield return dir;
        }
    }

    public static async Task<(string Profile, string ExtId)?> WaitForSeedAsync(
        string userDataDir, string extensionDir, TimeSpan timeout, ILogger? logger = null)
    {
        var extFull = Path.GetFullPath(extensionDir).Replace('\\', '/');
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var seed = FindSeed(userDataDir, extFull, logger: null);
            if (seed != null)
                return (seed.Profile, seed.ExtId);
            await Task.Delay(500);
        }
        logger?.LogWarning("Timed out waiting for browser to write extension into {UserData}", userDataDir);
        return null;
    }

    private static SeedHit? FindSeed(string userDataDir, string extensionDirForwardSlash, ILogger? logger)
    {
        SeedHit? prefsHit = null;

        foreach (var profileDir in EnumerateProfiles(userDataDir))
        {
            // Always check BOTH files. Prefer Secure Preferences hits with a MAC,
            // but accept Preferences hits (Chrome often writes unpacked there).
            foreach (var (fileName, isSecure) in new[]
                     {
                         ("Secure Preferences", true),
                         ("Preferences", false),
                     })
            {
                var path = Path.Combine(profileDir, fileName);
                if (!File.Exists(path)) continue;

                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                    var settings = root?["extensions"]?["settings"] as JsonObject;
                    if (settings == null) continue;

                    foreach (var prop in settings)
                    {
                        var entry = prop.Value as JsonObject;
                        if (entry == null) continue;
                        var p = entry["path"]?.GetValue<string>() ?? "";
                        var norm = p.Replace('\\', '/');
                        if (!norm.Contains(extensionDirForwardSlash, StringComparison.OrdinalIgnoreCase) &&
                            !extensionDirForwardSlash.Contains(norm, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var mac = root?["protection"]?["macs"]?["extensions"]?["settings"]?[prop.Key]
                            ?.GetValue<string>();
                        var devMac = root?["protection"]?["macs"]?["extensions"]?["ui"]?["developer_mode"]
                            ?.GetValue<string>();

                        var hit = new SeedHit(
                            Path.GetFileName(profileDir)!,
                            prop.Key,
                            entry.DeepClone()!,
                            mac,
                            devMac,
                            isSecure);

                        // Secure+MAC is ideal; return immediately.
                        if (isSecure && !string.IsNullOrEmpty(mac))
                            return hit;

                        // Otherwise remember a Preferences hit (no MAC required).
                        if (!isSecure)
                            prefsHit ??= hit;
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "FindSeed parse failed for {Path}", path);
                }
            }
        }

        return prefsHit;
    }

    private static bool WriteEntry(string profileDir, SeedHit seed, ILogger? logger)
    {
        // Always write the extension entry into Preferences (no HMAC). Never write
        // developer_mode or settings into Secure Preferences from here — forged
        // Secure Preferences without a valid super_mac resets developer mode on
        // the next Chrome/Edge launch (exact user-reported failure).
        // Profiles that need a Secure Preferences entry must be seeded via
        // --load-extension so the browser authors the MAC itself.
        var target = Path.Combine(profileDir, "Preferences");
        if (!File.Exists(target)) return false;

        var bak = target + ".aat.bak";
        if (!File.Exists(bak))
        {
            try { File.Copy(target, bak); } catch { }
        }

        var root = JsonNode.Parse(File.ReadAllText(target)) as JsonObject
                   ?? new JsonObject();

        var extensions = root["extensions"] as JsonObject ?? new JsonObject();
        var settings = extensions["settings"] as JsonObject ?? new JsonObject();
        settings[seed.ExtId] = seed.Entry.DeepClone();
        extensions["settings"] = settings;

        var ui = extensions["ui"] as JsonObject ?? new JsonObject();
        ui["developer_mode"] = true;
        extensions["ui"] = ui;
        root["extensions"] = extensions;

        var tmp = target + ".aat.tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        File.Copy(tmp, target, overwrite: true);
        try { File.Delete(tmp); } catch { }

        logger?.LogInformation(
            "Propagated {Id} → {Profile}/Preferences (Secure Preferences untouched)",
            seed.ExtId, Path.GetFileName(profileDir));
        return true;
    }
}
