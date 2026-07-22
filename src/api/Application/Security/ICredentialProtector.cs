namespace DmarcAnalyzer.Api.Application.Security;

public interface ICredentialProtector
{
    /// <summary>Encrypts a plaintext credential for storage. Idempotent on already-protected values.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts a stored credential. Legacy plaintext values pass through unchanged.</summary>
    string Unprotect(string stored);

    bool IsProtected(string stored);
}
