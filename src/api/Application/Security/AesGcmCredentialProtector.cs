using System.Security.Cryptography;
using System.Text;

namespace DmarcAnalyzer.Api.Application.Security;

/// <summary>
/// AES-256-GCM credential protection with a deployment-provided master key.
/// Stored format: "enc:v1:" + base64(nonce[12] || ciphertext || tag[16]).
/// Values without the prefix are treated as legacy plaintext and pass through
/// Unprotect unchanged so existing rows keep working until re-saved.
/// </summary>
public sealed class AesGcmCredentialProtector : ICredentialProtector
{
    public const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmCredentialProtector(string base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            throw new ArgumentException("credential encryption key is required", nameof(base64Key));
        }

        try
        {
            _key = Convert.FromBase64String(base64Key.Trim());
        }
        catch (FormatException)
        {
            throw new ArgumentException("credential encryption key must be valid base64", nameof(base64Key));
        }

        if (_key.Length != 32)
        {
            throw new ArgumentException("credential encryption key must decode to exactly 32 bytes (AES-256)", nameof(base64Key));
        }
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        if (IsProtected(plaintext))
        {
            return plaintext;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var payload = new byte[NonceSize + cipherBytes.Length + TagSize];
        nonce.CopyTo(payload, 0);
        cipherBytes.CopyTo(payload, NonceSize);
        tag.CopyTo(payload, NonceSize + cipherBytes.Length);

        return Prefix + Convert.ToBase64String(payload);
    }

    public string Unprotect(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        if (!IsProtected(stored))
        {
            return stored;
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(stored[Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("stored credential is malformed", ex);
        }

        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("stored credential is truncated");
        }

        var nonce = payload.AsSpan(0, NonceSize);
        var cipherBytes = payload.AsSpan(NonceSize, payload.Length - NonceSize - TagSize);
        var tag = payload.AsSpan(payload.Length - TagSize, TagSize);
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    public bool IsProtected(string stored)
        => stored.StartsWith(Prefix, StringComparison.Ordinal);
}
