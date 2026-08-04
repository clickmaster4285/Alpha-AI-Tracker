using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using client.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace client.Core;

/// <summary>
/// Persistent Chromium-engine install across every profile — no brand-name table.
/// <para>
/// Reality check (official Chrome/Edge docs, non-domain Windows):
/// branded Chrome rejects local-CRX External installs; Edge documents
/// HKLM\Wow6432Node External Extensions (not HKCU); off-store
/// ExtensionInstallForcelist is limited on non-AD machines. Developer mode in
/// Secure Preferences resets when HMAC/encrypted_hash is wrong.
/// </para>
/// Strategy (best-effort layered):
/// <list type="number">
/// <item>Stage + pack CRX (stable key-derived ID).</item>
/// <item>HKLM (+ Wow6432Node) External Extensions path/version — elevated.</item>
/// <item>HKCU policy ExtensionInstallForcelist + file:// update.xml.</item>
/// <item>Force developer_mode in Preferences only (never corrupt Secure Preferences).</item>
/// <item>Patch Start Menu/Desktop/Taskbar shortcuts with --load-extension
///       (the reliable consumer restart path for Chrome).</item>
/// </list>
/// </summary>
public static class ChromiumPersistentInstaller
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
    };

    public sealed record Result(
        bool ExternalRegistered,
        bool PolicyRegistered,
        int ProfilesUpdated,
        int ShortcutsPatched,
        string ExtensionId,
        string? CrxPath,
        string Message);

    /// <summary>Writable staging root for packed CRX + PEM (never the install dir).</summary>
    public static string StagingRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlphaAITracker", "extensions", "chromium");

    public static string PemPath => Path.Combine(StagingRoot, "extension.pem");
    public static string CrxPath => Path.Combine(StagingRoot, "chromium.crx");
    public static string StagedExtDir => Path.Combine(StagingRoot, "unpacked");
    public static string UpdateXmlPath => Path.Combine(StagingRoot, "update.xml");

    /// <summary>
    /// Persist the Chromium pack for this browser across profiles + restarts.
    /// </summary>
    public static Result Install(DetectedBrowser browser, string sourceExtensionDir, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(sourceExtensionDir) || !Directory.Exists(sourceExtensionDir))
            return new Result(false, false, 0, 0, "", null, "Chromium extension pack not found.");

        EnsureProfileRoot(browser);
        var userData = browser.ConfigDir;
        if (string.IsNullOrWhiteSpace(userData) || !Directory.Exists(userData))
            return new Result(false, false, 0, 0, "", null, "Browser user-data directory not resolved.");

        Directory.CreateDirectory(StagingRoot);
        StageExtensionPack(sourceExtensionDir);

        var (extId, spki) = EnsureKeyAndManifestKey();
        var packed = TryPackCrx(browser.BinaryPath, logger);
        var crx = packed && File.Exists(CrxPath) ? CrxPath : null;

        // Re-read PEM after pack — Chromium may have rewritten the key material.
        if (File.Exists(PemPath))
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(File.ReadAllText(PemPath));
                spki = rsa.ExportSubjectPublicKeyInfo();
                extId = ExtensionIdCalculator.ComputeFromPublicKey(spki);
            }
            catch { /* keep prior id */ }
        }

        // Unpacked staged dir embeds the same public key → same ID as CRX when loaded.
        // Prefer key-derived ID everywhere (native host allowed_origins + prefs).
        var injectPath = Path.GetFullPath(StagedExtDir);

        var externalOk = false;
        var policyOk = false;
        if (crx != null && OperatingSystem.IsWindows())
        {
            WriteUpdateXml(extId, crx, "1.0.0");
            externalOk = RegisterHklmExternalExtension(browser.BinaryPath, extId, crx, "1.0.0", logger);
            policyOk = RegisterHkcuInstallForcelist(browser.BinaryPath, extId, UpdateXmlPath, logger);
        }

        // Do NOT forge Preferences/Secure Preferences here. Writing developer_mode
        // into Preferences while Secure Preferences holds the real (MAC-signed) value
        // causes Chrome/Edge to reset developer mode on the next launch. Seeding is
        // done by ChromiumProfileSeeder via --load-extension per profile.

        // Reliable restart path on branded Chrome/Edge (consumer / non-domain).
        var shortcuts = ChromiumShortcutPersister.PersistLoadExtensionArgs(
            browser.BinaryPath, injectPath, logger);

        var profileCount = ChromiumProfileSeeder.ListProfileDirectoryNames(userData).Count;
        var parts = new List<string>
        {
            $"{profileCount} profiles detected (seeded separately via --load-extension)",
            $"{shortcuts} shortcuts patched with --load-extension",
        };
        if (externalOk) parts.Add("HKLM External Extensions registered");
        if (policyOk) parts.Add("HKCU ExtensionInstallForcelist written");
        if (!externalOk && !policyOk && crx != null)
            parts.Add("Note: branded Chrome blocks silent local-CRX External installs on Windows; per-profile --load-extension + shortcuts keep the extension after restart");

        return new Result(externalOk, policyOk, 0, shortcuts, extId, crx, string.Join(". ", parts) + ".");
    }

    /// <summary>All Chromium profile folders that have Preferences (skips Guest/System).</summary>
    /// <summary>
    /// Prefer profiles that look like real user profiles (Default / Profile N).
    /// Still accepts custom-named profiles that have Preferences.
    /// </summary>
    public static IEnumerable<string> EnumerateProfileDirs(string userDataDir)
    {
        if (!Directory.Exists(userDataDir)) yield break;

        foreach (var dir in Directory.GetDirectories(userDataDir))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Equals("System Profile", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Crashpad", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Snapshots", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("External Extensions", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            // Skip Chromium component/cache dirs that sometimes contain a Preferences stub.
            if (name.Contains("Cache", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains("Metrics", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(Path.Combine(dir, "Preferences"))) continue;
            yield return dir;
        }
    }

    private static void EnsureProfileRoot(DetectedBrowser browser)
    {
        if (!string.IsNullOrEmpty(browser.ConfigDir) && Directory.Exists(browser.ConfigDir))
            return;

        var candidate = new BrowserCandidate
        {
            Name = browser.Name,
            BinaryName = browser.BinaryName,
            DesktopId = browser.DesktopId,
            BinaryPath = browser.BinaryPath,
            FlatpakAppId = browser.FlatpakAppId,
        };
        var found = BrowserEngineDetector.FindProfileRoot(candidate);
        if (found == null) return;
        browser.ConfigDir = found.Value.Root;
        if (!string.IsNullOrEmpty(found.Value.DefaultProfile))
            browser.DefaultProfileDir = Path.Combine(found.Value.Root, found.Value.DefaultProfile);
    }

    private static void StageExtensionPack(string sourceDir)
    {
        if (Directory.Exists(StagedExtDir))
        {
            try { Directory.Delete(StagedExtDir, recursive: true); } catch { }
        }
        CopyDirectory(sourceDir, StagedExtDir);
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    /// <summary>Ensure PEM exists and manifest.json embeds the matching public key.</summary>
    private static (string ExtId, byte[] Spki) EnsureKeyAndManifestKey()
    {
        RSA rsa;
        if (File.Exists(PemPath))
        {
            rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(PemPath));
        }
        else
        {
            rsa = RSA.Create(2048);
            // Traditional RSA PEM — what Chromium --pack-extension-key expects.
            File.WriteAllText(PemPath, rsa.ExportRSAPrivateKeyPem());
        }

        var spki = rsa.ExportSubjectPublicKeyInfo();
        var extId = ExtensionIdCalculator.ComputeFromPublicKey(spki);
        var keyB64 = Convert.ToBase64String(spki);

        var manifestPath = Path.Combine(StagedExtDir, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var node = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
                       ?? new JsonObject();
            node["key"] = keyB64;
            File.WriteAllText(manifestPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        return (extId, spki);
    }

    private static bool TryPackCrx(string browserBinary, ILogger? logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(browserBinary) || !File.Exists(browserBinary))
                return false;

            // Remove stale CRX so we can detect a fresh pack.
            try { if (File.Exists(CrxPath)) File.Delete(CrxPath); } catch { }
            var siblingCrx = StagedExtDir + ".crx";
            try { if (File.Exists(siblingCrx)) File.Delete(siblingCrx); } catch { }

            // Isolated user-data-dir so --pack-extension cannot grab the real
            // Chrome/Edge singleton and ignore later --load-extension seeds.
            var packUserData = Path.Combine(Path.GetTempPath(), "AlphaAITracker-crx-pack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(packUserData);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = browserBinary,
                    Arguments =
                        $"--user-data-dir=\"{packUserData}\" " +
                        $"--pack-extension=\"{StagedExtDir}\" --pack-extension-key=\"{PemPath}\" --no-message-box",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                if (!proc.WaitForExit(60_000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return false;
                }

                // Chrome writes <dir>.crx next to the unpacked folder.
                if (File.Exists(siblingCrx))
                {
                    File.Copy(siblingCrx, CrxPath, overwrite: true);
                    try { File.Delete(siblingCrx); } catch { }
                }

                // If pack regenerated a PEM next to the folder, adopt it so IDs stay aligned.
                var siblingPem = StagedExtDir + ".pem";
                if (File.Exists(siblingPem))
                {
                    try { File.Copy(siblingPem, PemPath, overwrite: true); } catch { }
                    try { File.Delete(siblingPem); } catch { }
                }

                var ok = File.Exists(CrxPath) && new FileInfo(CrxPath).Length > 0;
                logger?.LogInformation(
                    "CRX pack via {Bin}: ok={Ok} path={Path} exit={Code}",
                    browserBinary, ok, CrxPath, proc.ExitCode);
                return ok;
            }
            finally
            {
                try { Directory.Delete(packUserData, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "CRX pack failed");
            return false;
        }
    }

    /// <summary>
    /// Official Edge/Chrome Windows path: HKLM (+ Wow6432Node). Requires one UAC prompt.
    /// Chrome may still ignore local path CRX (store-only policy); Edge often honors it.
    /// </summary>
    private static bool RegisterHklmExternalExtension(
        string binaryPath, string extId, string crxPath, string version, ILogger? logger)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var soft = DeriveSoftwareVendorProductKey(binaryPath);
        if (string.IsNullOrEmpty(soft)) return false;

        // Documented locations (Microsoft Edge + Chromium):
        // HKLM\Software\Wow6432Node\{Vendor}\{Product}\Extensions\{id}
        // HKLM\Software\{Vendor}\{Product}\Extensions\{id}
        var targets = new[]
        {
            $@"HKLM:\Software\Wow6432Node\{soft}\Extensions\{extId}",
            $@"HKLM:\Software\{soft}\Extensions\{extId}",
        };

        var script = new StringBuilder();
        foreach (var t in targets)
        {
            script.AppendLine($"New-Item -Path '{t}' -Force | Out-Null");
            script.AppendLine($"Set-ItemProperty -Path '{t}' -Name 'path' -Value '{crxPath.Replace("'", "''")}' -Type String");
            script.AppendLine($"Set-ItemProperty -Path '{t}' -Name 'version' -Value '{version}' -Type String");
        }

        var ok = RunElevatedPowerShell(script.ToString(), logger);
        // Also keep HKCU copy — ignored by Edge docs, harmless, helps some Chromium forks.
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{soft}\Extensions\{extId}", true);
            key?.SetValue("path", crxPath);
            key?.SetValue("version", version);
        }
        catch { }

        logger?.LogInformation("HKLM External Extensions write ok={Ok} id={Id}", ok, extId);
        return ok;
    }

    /// <summary>
    /// HKCU ExtensionInstallForcelist + ExtensionInstallSources for a local file:// update.xml.
    /// Non-AD Edge/Chrome may refuse off-store forcelist — still worth writing; shortcut
    /// persistence covers the consumer case.
    /// </summary>
    private static bool RegisterHkcuInstallForcelist(
        string binaryPath, string extId, string updateXmlPath, ILogger? logger)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var soft = DeriveSoftwareVendorProductKey(binaryPath);
        if (string.IsNullOrEmpty(soft)) return false;

        // Policies\{Vendor}\{Product} derived from install layout (Google\Chrome, Microsoft\Edge, …).
        var parts = soft.Split('\\');
        if (parts.Length < 3) return false;
        var policyRoot = $@"Software\Policies\{parts[1]}\{parts[2]}";

        try
        {
            var fileUrl = new Uri(updateXmlPath).AbsoluteUri; // file:///C:/...
            var forcelistValue = $"{extId};{fileUrl}";

            using (var fl = Registry.CurrentUser.CreateSubKey($@"{policyRoot}\ExtensionInstallForcelist", true))
            {
                fl?.SetValue("1", forcelistValue, RegistryValueKind.String);
            }
            using (var src = Registry.CurrentUser.CreateSubKey($@"{policyRoot}\ExtensionInstallSources", true))
            {
                // Allow file:// and the staging directory host for updates.
                src?.SetValue("1", "file:///*", RegistryValueKind.String);
                src?.SetValue("2", "http://127.0.0.1:*/*", RegistryValueKind.String);
                src?.SetValue("3", "http://localhost:*/*", RegistryValueKind.String);
            }
            // Allow developer mode on the extensions page (does not force it on).
            using (var pol = Registry.CurrentUser.CreateSubKey(policyRoot, true))
            {
                pol?.SetValue("ExtensionDeveloperModeSettings", 0, RegistryValueKind.DWord);
            }

            logger?.LogInformation(
                "HKCU forcelist written under {Root}: {Value}", policyRoot, forcelistValue);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "HKCU ExtensionInstallForcelist failed");
            return false;
        }
    }

    private static void WriteUpdateXml(string extId, string crxPath, string version)
    {
        var codebase = new Uri(crxPath).AbsoluteUri;
        var xml =
            $"""
            <?xml version='1.0' encoding='UTF-8'?>
            <gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>
              <app appid='{extId}'>
                <updatecheck codebase='{codebase}' version='{version}' />
              </app>
            </gupdate>
            """;
        File.WriteAllText(UpdateXmlPath, xml);
    }

    private static bool RunElevatedPowerShell(string scriptBody, ILogger? logger)
    {
        var tmpPs1 = Path.Combine(Path.GetTempPath(), "aat_ext_" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            File.WriteAllText(tmpPs1, scriptBody);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments =
                    $"-NoProfile -ExecutionPolicy Bypass -Command " +
                    $"\"Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \\\"{tmpPs1}\\\"'\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Elevated PowerShell failed");
            return false;
        }
        finally
        {
            try { if (File.Exists(tmpPs1)) File.Delete(tmpPs1); } catch { }
        }
    }

    /// <summary>...\Vendor\Product\Application\exe → Software\Vendor\Product</summary>
    public static string? DeriveSoftwareVendorProductKey(string binaryPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
                return null;

            var dir = Path.GetDirectoryName(binaryPath);
            if (string.IsNullOrEmpty(dir)) return null;

            string productDir;
            if (string.Equals(Path.GetFileName(dir), "Application", StringComparison.OrdinalIgnoreCase))
                productDir = Path.GetDirectoryName(dir) ?? "";
            else
                productDir = dir;

            var vendorDir = Path.GetDirectoryName(productDir) ?? "";
            var product = Path.GetFileName(productDir);
            var vendor = Path.GetFileName(vendorDir);
            if (string.IsNullOrEmpty(product) || string.IsNullOrEmpty(vendor))
                return null;

            return $@"Software\{vendor}\{product}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Write developer_mode + extension settings into Preferences only.
    /// DEPRECATED for install — forging these values fights Secure Preferences and
    /// resets developer mode on restart. Kept only for diagnostics; Install no longer calls it.
    /// </summary>
    private static bool UpdateProfilePreferencesOnly(
        string profileDir, string extId, string extensionPath, ILogger? logger)
    {
        // Intentionally no-op: see ChromiumProfileSeeder.
        logger?.LogDebug("UpdateProfilePreferencesOnly skipped for {Dir} (use per-profile --load-extension seed)", profileDir);
        return false;
    }

    private static bool HasEncryptedExtensionHashes(JsonObject root)
    {
        var macs = root["protection"]?["macs"]?["extensions"] as JsonObject;
        if (macs == null) return false;
        if (macs["settings_encrypted_hash"] != null) return true;
        if (macs["ui"]?["developer_mode_encrypted_hash"] != null) return true;
        return false;
    }

    private static void ForceDeveloperMode(JsonObject root)
    {
        var extensions = root["extensions"] as JsonObject ?? new JsonObject();
        var ui = extensions["ui"] as JsonObject ?? new JsonObject();
        ui["developer_mode"] = true;
        extensions["ui"] = ui;
        root["extensions"] = extensions;
    }

    private static void UpsertExtensionSettings(JsonObject root, string extId, string extensionPath)
    {
        var extensions = root["extensions"] as JsonObject ?? new JsonObject();
        var settings = extensions["settings"] as JsonObject ?? new JsonObject();

        var installTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        // Chromium on Windows often stores paths with forward slashes in prefs.
        var normalizedPath = extensionPath.Replace('\\', '/');

        settings[extId] = new JsonObject
        {
            ["active_permissions"] = new JsonObject
            {
                ["api"] = new JsonArray(
                    "alarms", "nativeMessaging", "storage", "tabs"),
                ["explicit_host"] = new JsonArray("<all_urls>"),
                ["manifest_permissions"] = new JsonArray(
                    "alarms", "nativeMessaging", "storage", "tabs"),
                ["scriptable_host"] = new JsonArray("<all_urls>"),
            },
            ["commands"] = new JsonObject(),
            ["creation_flags"] = 1,
            ["from_webstore"] = false,
            ["granted_permissions"] = new JsonObject
            {
                ["api"] = new JsonArray(
                    "alarms", "nativeMessaging", "storage", "tabs"),
                ["explicit_host"] = new JsonArray("<all_urls>"),
                ["manifest_permissions"] = new JsonArray(
                    "alarms", "nativeMessaging", "storage", "tabs"),
                ["scriptable_host"] = new JsonArray("<all_urls>"),
            },
            ["install_time"] = installTime,
            ["location"] = 4,
            ["path"] = normalizedPath,
            ["state"] = 1,
            ["was_installed_by_default"] = false,
            ["was_installed_by_oem"] = false,
            ["withholding_permissions"] = false,
            ["manifest"] = new JsonObject
            {
                ["name"] = "Alpha AI Tracker — Browser Journey",
                ["version"] = "1.0.0",
                ["manifest_version"] = 3,
            },
        };

        extensions["settings"] = settings;
        root["extensions"] = extensions;
    }

    private static void TryUpdateProtectionMacs(JsonObject root, string extId, ILogger? logger)
    {
        try
        {
            var seed = ResolveHmacSeed();
            var protection = root["protection"] as JsonObject ?? new JsonObject();
            var macs = protection["macs"] as JsonObject ?? new JsonObject();
            var macExt = macs["extensions"] as JsonObject ?? new JsonObject();
            var macSettings = macExt["settings"] as JsonObject ?? new JsonObject();
            var macUi = macExt["ui"] as JsonObject ?? new JsonObject();

            var settingsNode = root["extensions"]?["settings"]?[extId];
            if (settingsNode != null)
            {
                var path = $"extensions.settings.{extId}";
                var payload = settingsNode.ToJsonString(CompactJson);
                macSettings[extId] = ComputeMacHex(seed, path + payload);
            }

            macUi["developer_mode"] = ComputeMacHex(seed, "extensions.ui.developer_mode" + "true");

            // Drop encrypted_hash for nodes we own — stale device hashes cause rejects.
            if (macExt["settings_encrypted_hash"] is JsonObject encSettings)
                encSettings.Remove(extId);
            if (macExt["ui"] is JsonObject)
            {
                // encrypted hash sibling lives under macs.extensions.ui in some builds,
                // and under settings_encrypted_hash / developer_mode_encrypted_hash in others.
            }
            if (macUi.ContainsKey("developer_mode_encrypted_hash"))
                macUi.Remove("developer_mode_encrypted_hash");

            macExt["settings"] = macSettings;
            macExt["ui"] = macUi;
            macs["extensions"] = macExt;
            protection["macs"] = macs;

            // super_mac: HMAC(seed, sidWithoutRid + json(macs)) on Windows Secure Preferences.
            var sidPrefix = GetUserSidWithoutRid();
            var macsJson = macs.ToJsonString(CompactJson);
            protection["super_mac"] = ComputeMacHex(seed, (sidPrefix ?? "") + macsJson);
            root["protection"] = protection;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Protection MAC update skipped");
        }
    }

    /// <summary>
    /// Chromium HMAC seed — try empty (Edge often) then the well-known Chrome resources.pak seed.
    /// Exact seed varies by browser build; External Extensions is the primary persistence path.
    /// </summary>
    private static byte[] ResolveHmacSeed()
    {
        // Known Chrome seed container bytes from public research (Chromium ~130–139).
        // Used only as a best-effort Preferences backup; External Extensions is authoritative.
        try
        {
            return Convert.FromHexString(
                "e748f336d85ea5f9dcdf25d8f347a65b4cdf667600f02df6724a2af18a212d26" +
                "b788a25086910cf3a90313696871f3dc05823730c91df8ba5c4fd9c884b505a8");
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static string ComputeMacHex(byte[] seed, string message)
    {
        using var hmac = new HMACSHA256(seed.Length == 0 ? new byte[32] : seed);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash);
    }

    private static string? GetUserSidWithoutRid()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrEmpty(sid)) return null;
            var last = sid.LastIndexOf('-');
            return last > 0 ? sid[..last] : sid;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteJsonAtomic(string path, JsonObject root)
    {
        var tmp = path + ".aat.tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { }
    }
}
