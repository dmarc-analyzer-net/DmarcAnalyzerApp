using System.Security.Cryptography;

namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>PBKDF2-SHA256 password hashing (16-byte salt + 32-byte hash, base64).</summary>
public static class PasswordHasher
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        var combined = new byte[16 + 32];
        salt.CopyTo(combined, 0);
        hash.CopyTo(combined, 16);
        return Convert.ToBase64String(combined);
    }

    public static bool Verify(string password, string storedHash)
    {
        byte[] combined;
        try
        {
            combined = Convert.FromBase64String(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        if (combined.Length != 48) return false;
        var salt = combined[..16];
        var expectedHash = combined[16..];
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
