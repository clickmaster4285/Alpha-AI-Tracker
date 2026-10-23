using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AlphaAITracker.Service;

/// <summary>
/// Local CA used only to present a trusted HTTPS block page on 127.0.0.1:443
/// when hosts sinks a domain to loopback. Traffic never leaves the machine.
/// Installed into LocalMachine\Root while web-block is active; removed on clear/teardown.
/// </summary>
internal static class BlockPageCertStore
{
    public const string CaSubject = "CN=Alpha AI Tracker DLP Block CA";
    private const string CaFileName = "block-page-ca.pfx";
    private const string CaPassword = "AlphaAI-DLP-BlockPage-LocalOnly";

    private static readonly object Gate = new();
    private static X509Certificate2? _ca;
    private static readonly Dictionary<string, X509Certificate2> LeafCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultDataDir
    {
        get
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, ServiceIdentity.DataFolderName, "dlp");
        }
    }

    public static string CaPfxPath(string? dataDir = null) =>
        Path.Combine(dataDir ?? DefaultDataDir, CaFileName);

    public static X509Certificate2 EnsureCa(DlpConfig? cfg = null, ILogger? logger = null)
    {
        var dataDir = cfg?.DataDir ?? DefaultDataDir;
        lock (Gate)
        {
            if (_ca is not null) return _ca;

            Directory.CreateDirectory(dataDir);
            var path = CaPfxPath(dataDir);
            if (File.Exists(path))
            {
                try
                {
                    _ca = X509CertificateLoader.LoadPkcs12FromFile(
                        path, CaPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to load block-page CA; recreating");
                    try { File.Delete(path); } catch { /* ignore */ }
                }
            }

            if (_ca is null)
            {
                using var rsa = RSA.Create(2048);
                var req = new CertificateRequest(CaSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
                    critical: true));
                req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
                var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(12));
                var pfx = ca.Export(X509ContentType.Pfx, CaPassword);
                File.WriteAllBytes(path, pfx);
                ca.Dispose();
                _ca = X509CertificateLoader.LoadPkcs12(
                    pfx, CaPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
                logger?.LogInformation("Created block-page CA at {Path}", path);
            }

            InstallCaInRootStore(_ca, logger);
            return _ca;
        }
    }

    public static X509Certificate2 GetLeaf(string hostName, DlpConfig? cfg = null, ILogger? logger = null)
    {
        var host = NormalizeHost(hostName);
        if (host.Length == 0) host = "blocked.local";

        lock (Gate)
        {
            if (LeafCache.TryGetValue(host, out var cached) && DateTime.UtcNow < cached.NotAfter.AddDays(-7))
                return cached;

            var ca = EnsureCa(cfg, logger);
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(host);
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                san.AddDnsName(host[4..]);
            else
                san.AddDnsName("www." + host);
            req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7f;
            var leaf = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2), serial);
            leaf = leaf.CopyWithPrivateKey(rsa);

            if (LeafCache.TryGetValue(host, out var old))
            {
                LeafCache.Remove(host);
                try { old.Dispose(); } catch { /* ignore */ }
            }
            LeafCache[host] = leaf;
            return leaf;
        }
    }

    public static void Uninstall(DlpConfig? cfg = null, ILogger? logger = null)
    {
        var dataDir = cfg?.DataDir ?? DefaultDataDir;
        lock (Gate)
        {
            foreach (var kv in LeafCache.ToList())
            {
                try { kv.Value.Dispose(); } catch { /* ignore */ }
                LeafCache.Remove(kv.Key);
            }

            string? thumb = null;
            if (_ca is not null)
            {
                thumb = _ca.Thumbprint;
                try { _ca.Dispose(); } catch { /* ignore */ }
                _ca = null;
            }

            try
            {
                var path = CaPfxPath(dataDir);
                if (File.Exists(path))
                {
                    if (thumb is null)
                    {
                        try
                        {
                            using var tmp = X509CertificateLoader.LoadPkcs12FromFile(path, CaPassword);
                            thumb = tmp.Thumbprint;
                        }
                        catch { /* ignore */ }
                    }
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "block-page CA file delete failed");
            }

            if (!string.IsNullOrEmpty(thumb))
                RemoveCaFromRootStore(thumb, logger);

            // Also sweep by subject in case thumbprint unknown.
            RemoveCaBySubject(logger);
        }
    }

    private static void RemoveCaBySubject(ILogger? logger)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            var victims = store.Certificates
                .Cast<X509Certificate2>()
                .Where(c => c.Subject.Contains("Alpha AI Tracker DLP Block CA", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var c in victims)
            {
                store.Remove(c);
                logger?.LogInformation("Removed block-page CA by subject ({Thumb})", c.Thumbprint);
                c.Dispose();
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "RemoveCaBySubject failed");
        }
    }

    private static void InstallCaInRootStore(X509Certificate2 ca, ILogger? logger)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            var existing = store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, validOnly: false);
            if (existing.Count > 0) return;
            // Public-only copy for the store (private key stays in our PFX usage).
            var pub = X509CertificateLoader.LoadCertificate(ca.Export(X509ContentType.Cert));
            store.Add(pub);
            logger?.LogInformation("Installed block-page CA into LocalMachine\\Root ({Thumb})", ca.Thumbprint);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to install block-page CA into Root store");
        }
    }

    private static void RemoveCaFromRootStore(string thumbprint, ILogger? logger)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            foreach (var c in found)
            {
                store.Remove(c);
                c.Dispose();
            }
            if (found.Count > 0)
                logger?.LogInformation("Removed block-page CA from LocalMachine\\Root ({Thumb})", thumbprint);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to remove block-page CA from Root store");
        }
    }

    private static string NormalizeHost(string? host)
    {
        var h = (host ?? "").Trim().TrimEnd('.');
        var colon = h.IndexOf(':');
        if (colon > 0) h = h[..colon];
        return h.ToLowerInvariant();
    }
}
