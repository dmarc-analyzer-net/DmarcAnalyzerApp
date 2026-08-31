namespace DmarcAnalyzer.Api.Application.Security;

/// <summary>At-rest protection for stored secrets (mailbox passwords, S3 keys) — see <see cref="AesGcmCredentialProtector"/>.</summary>
public interface ICredentialProtector
{
    /// <summary>Encrypts a plaintext credential for storage. Idempotent on already-protected values.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts a stored credential. Legacy plaintext values pass through unchanged.</summary>
    string Unprotect(string stored);

    /// <summary>Whether a stored value carries the encrypted-format prefix.</summary>
    bool IsProtected(string stored);
}
