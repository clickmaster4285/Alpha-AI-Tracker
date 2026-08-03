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

        var id = new char[32];
        for (int i = 0; i < 16; i++)
        {
            var b = hash[i];
            id[i * 2] = Alphabet[(b >> 4) & 0x0F];   // high nibble
            id[i * 2 + 1] = Alphabet[b & 0x0F];       // low nibble
        }
        return new string(id);
    }
}
