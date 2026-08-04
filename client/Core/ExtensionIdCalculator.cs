using System.Security.Cryptography;
using System.Text;

namespace client.Core;

/// <summary>
/// Computes the Chrome extension ID from an absolute directory path, mirroring
/// Chrome's unpacked-extension ID scheme (previously implemented in Python in
/// <c>ComputeExtensionId()</c>, <c>InjectExtensionViaPython()</c>, and
/// <c>install-extensions.sh</c>).
///
/// Algorithm (identical to the Python version):
///   SHA-256 of the path UTF-8 bytes → first 128 bits → map each 4-bit nibble
///   to the letters a–p (high nibble first, then low nibble per byte).
/// </summary>
public static class ExtensionIdCalculator
{
    private const string Alphabet = "abcdefghijklmnop";

    /// <summary>
    /// Compute the 32-character a–p extension ID for a directory path.
    /// </summary>
    public static string Compute(string extensionPath)
    {
        var pathBytes = Encoding.UTF8.GetBytes(extensionPath);
        var hash = SHA256.HashData(pathBytes);
        return NibblesToId(hash, 16);
    }

    /// <summary>
    /// Compute the 32-character a–p extension ID for a PACKED (signed CRX)
    /// extension, derived from the extension's public key rather than its
    /// filesystem path.
    ///
    /// Chrome assigns a CRX-installed extension an ID from its key, NOT its
    /// path: the ID is the first 128 bits (16 bytes) of SHA-256 of the
    /// X.509 SubjectPublicKeyInfo (SPKI) DER block, nibble-mapped to the a–p
    /// alphabet — the same mapping as <see cref="Compute"/>, over different
    /// input bytes. (Chromium: crx_file/crx3.proto — "the first 128 bits of
    /// the SHA-256 hash of the public key must equal the crx_id".)
    ///
    /// The native-messaging manifest's <c>allowed_origins</c> must use THIS id
    /// for policy-installed (force-installed CRX) browsers, and the
    /// path-derived id for dev/unpacked (--load-extension / Preferences
    /// injection) browsers. The two modes are deliberately kept separate —
    /// never silently replace one with the other, or native messaging breaks.
    /// </summary>
    /// <param name="spkiPublicKeyDer">
    /// The extension's public key in X.509 SubjectPublicKeyInfo DER form
    /// (the same bytes Chrome sees in the CRX header).
    /// </param>
    public static string ComputeFromPublicKey(byte[] spkiPublicKeyDer)
    {
        var hash = SHA256.HashData(spkiPublicKeyDer);
        return NibblesToId(hash, 16);
    }

    /// <summary>
    /// Map the first <paramref name="byteCount"/> bytes of a hash to a–p
    /// letters: each byte's high nibble then low nibble picks a letter.
    /// </summary>
    private static string NibblesToId(byte[] hash, int byteCount)
    {
        var id = new char[byteCount * 2];
        for (int i = 0; i < byteCount; i++)
        {
            var b = hash[i];
            id[i * 2] = Alphabet[(b >> 4) & 0x0F];   // high nibble
            id[i * 2 + 1] = Alphabet[b & 0x0F];       // low nibble
        }
        return new string(id);
    }
}
