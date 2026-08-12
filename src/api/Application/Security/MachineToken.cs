using System.Security.Cryptography;
using System.Text;

namespace DmarcAnalyzer.Api.Application.Security;

/// <summary>
/// Mints and verifies machine tokens. The single place a token is generated and compared,
/// which ADR 0010 requires magic links to reuse when they arrive rather than growing a
/// second scheme with its own hashing.
/// <para>
/// The issued string is <c>dmarcanalyzer_&lt;tokenId&gt;_&lt;secret&gt;</c>. The prefix is
/// not decoration: it is what makes a leaked credential greppable in a log file and
/// recognisable to secret scanners, which is worth more here than the extra entropy the
/// characters could have carried.
/// </para>
/// <para>
/// The split matters too. The id half is stored in the clear and indexed, so verifying a
/// token is one lookup by id and one comparison — not a scan that hashes the presented
/// secret against every credential in the table.
/// </para>
/// </summary>
public static class MachineToken
{
    public const string Prefix = "dmarcanalyzer";

    private const int TokenIdBytes = 12;   // 24 chars of hex — an identifier, not a secret
    private const int SecretBytes = 32;    // 256 bits, which is what makes SHA-256 the right hash

    public sealed record Issued(string TokenId, string Presented, string Hash);

    /// <summary>
    /// Generates a new credential. The presented string is the only time the secret exists
    /// outside the caller's hands — nothing stores it, and it cannot be recovered from the
    /// hash.
    /// </summary>
    public static Issued Create()
    {
        // The id is hex and the secret is base64url, and that asymmetry is deliberate:
        // base64url's alphabet includes the '_' used as the delimiter here. Hex cannot
        // contain one, so the id can never move the split boundary, and the secret is the
        // final segment where an underscore is harmless.
        var tokenId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenIdBytes));
        var secret = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

        return new Issued(tokenId, $"{Prefix}_{tokenId}_{secret}", HashSecret(secret));
    }

    /// <summary>
    /// Splits a presented token without touching the database. Returns false for anything
    /// that is not shaped like one of ours, so a stray Authorization header costs no query.
    /// </summary>
    public static bool TryParse(string? presented, out string tokenId, out string secret)
    {
        tokenId = string.Empty;
        secret = string.Empty;

        if (string.IsNullOrWhiteSpace(presented))
        {
            return false;
        }

        // Limit of 3, so an underscore inside the base64url secret stays part of the secret
        // instead of turning a valid token into a malformed one.
        var parts = presented.Split('_', 3);
        if (parts.Length != 3 || parts[0] != Prefix || parts[1].Length == 0 || parts[2].Length == 0)
        {
            return false;
        }

        tokenId = parts[1];
        secret = parts[2];
        return true;
    }

    public static string HashSecret(string secret)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// Constant-time comparison. The timing leak it avoids is small, but the cost of avoiding
    /// it is one method call, and a credential check is exactly the place not to be clever.
    /// </summary>
    public static bool VerifySecret(string secret, string storedHash)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashSecret(secret)),
            Encoding.UTF8.GetBytes(storedHash));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
