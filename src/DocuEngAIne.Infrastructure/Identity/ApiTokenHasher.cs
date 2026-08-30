using System.Security.Cryptography;
using System.Text;

namespace DocuEngAIne.Infrastructure.Identity;

/// <summary>
/// Generates per-tenant API tokens and hashes them for storage.
/// The plaintext is a <c>dea_</c> prefix plus 32 random bytes as lowercase hex; the stored value is
/// SHA-256 hex of the full plaintext. There is no inverse.
/// </summary>
public static class ApiTokenHasher
{
    public const string PlaintextPrefix = "dea_";
    public const int RandomByteCount = 32;
    public const int HashHexLength = 64;
    public const int PublicPrefixLength = 12;

    public static string GeneratePlaintext()
    {
        var bytes = RandomNumberGenerator.GetBytes(RandomByteCount);
        return PlaintextPrefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Hash(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>First <see cref="PublicPrefixLength"/> characters of the plaintext, for admin lists.</summary>
    public static string PublicPrefix(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        return plaintext.Length <= PublicPrefixLength
            ? plaintext
            : plaintext[..PublicPrefixLength];
    }
}
