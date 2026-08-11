using System.Security.Cryptography;
using System.Text;

namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// Identifies a credential encryption key without being able to decrypt anything with
/// it.
/// <para>
/// This exists because <c>AesGcmCredentialProtector</c> stores <c>enc:v1:</c> as a
/// *format* version with no key identity in it. Restore the database under the wrong
/// key today and everything looks fine until the next mailbox sync throws
/// <c>AuthenticationTagMismatchException</c> — long after the restore was called a
/// success. Writing the fingerprint into the manifest turns "do I hold the right key
/// for this file?" into a comparison that runs before the import.
/// </para>
/// </summary>
public static class CredentialKeyFingerprint
{
    /// <summary>
    /// Eight bytes of SHA-256 over the key string, hex, prefixed with the algorithm.
    /// Truncated because this is an identifier, not a commitment: a 32-byte AES key has
    /// far too much entropy for 64 bits of digest to help an attacker, and a short value
    /// is one an operator can compare by eye.
    /// </summary>
    public static string? Compute(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            return null;
        }

        // Hashed as the trimmed base64 *text*, matching what AesGcmCredentialProtector
        // accepts, so the same configured value always produces the same fingerprint.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(base64Key.Trim()));

        return $"sha256:{Convert.ToHexStringLower(digest.AsSpan(0, 8))}";
    }

    /// <summary>
    /// Whether an artifact's fingerprint matches the running key. A null on either side
    /// means "unknown", which is never a match — importing report sources that can
    /// never be decrypted is the failure this is here to prevent.
    /// </summary>
    public static bool Matches(string? artifactFingerprint, string? runningKey)
    {
        var running = Compute(runningKey);

        return artifactFingerprint is not null
            && running is not null
            && string.Equals(artifactFingerprint, running, StringComparison.OrdinalIgnoreCase);
    }
}
