using System.Security.Cryptography;
using DmarcAnalyzer.Api.Application.Security;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class AesGcmCredentialProtectorTests
{
    private static readonly string Key = Convert.ToBase64String(new byte[32]
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
    });

    private readonly AesGcmCredentialProtector _protector = new(Key);

    [Fact]
    public void Protect_ThenUnprotect_RoundTrips()
    {
        var stored = _protector.Protect("s3cret-p@ssword");

        Assert.StartsWith("enc:v1:", stored, StringComparison.Ordinal);
        Assert.True(_protector.IsProtected(stored));
        Assert.Equal("s3cret-p@ssword", _protector.Unprotect(stored));
    }

    [Fact]
    public void Protect_IsIdempotent_OnAlreadyProtectedValue()
    {
        var stored = _protector.Protect("secret");

        Assert.Equal(stored, _protector.Protect(stored));
    }

    [Fact]
    public void Unprotect_PassesThroughLegacyPlaintext()
    {
        Assert.Equal("legacy-plaintext", _protector.Unprotect("legacy-plaintext"));
        Assert.False(_protector.IsProtected("legacy-plaintext"));
    }

    [Fact]
    public void Unprotect_WithTamperedPayload_Throws()
    {
        var stored = _protector.Protect("secret");
        var payload = Convert.FromBase64String(stored["enc:v1:".Length..]);
        payload[^1] ^= 0xFF;
        var tampered = "enc:v1:" + Convert.ToBase64String(payload);

        Assert.ThrowsAny<CryptographicException>(() => _protector.Unprotect(tampered));
    }

    [Fact]
    public void Unprotect_WithWrongKey_Throws()
    {
        var stored = _protector.Protect("secret");
        var otherKey = Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray());
        var other = new AesGcmCredentialProtector(otherKey);

        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("c2hvcnQ=")]
    public void Constructor_WithInvalidKey_Throws(string key)
    {
        Assert.Throws<ArgumentException>(() => new AesGcmCredentialProtector(key));
    }

    [Fact]
    public void Protect_ProducesUniqueCiphertexts_PerCall()
    {
        var first = _protector.Protect("secret");
        var second = _protector.Protect("secret");

        Assert.NotEqual(first, second);
    }
}
