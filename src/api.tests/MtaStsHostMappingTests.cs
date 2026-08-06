using DmarcAnalyzer.Api.Application.MtaSts;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The pure halves of policy hosting: Host-header → domain mapping (anonymous
/// internet input, so the table leans hostile), the rendered policy format, and
/// the mx-pattern validator for what we persist. The render→parse round-trip is
/// what keeps the serving and checking halves from ever disagreeing.
/// </summary>
public sealed class MtaStsHostMappingTests
{
    [Theory]
    [InlineData("mta-sts.example.com", "example.com")]
    [InlineData("MTA-STS.Example.COM", "example.com")]           // case
    [InlineData("mta-sts.example.com.", "example.com")]          // trailing dot
    [InlineData("mta-sts.example.com:8443", "example.com")]      // defensive port strip
    [InlineData("mta-sts.mta-sts.example.com", "mta-sts.example.com")] // nested is a valid domain name
    [InlineData("mta-sts.xn--caf-dma.example", "xn--caf-dma.example")] // IDN A-label passthrough
    [InlineData("  mta-sts.example.com  ", "example.com")]
    [InlineData("example.com", null)]                            // no prefix
    [InlineData("sub.mta-sts.example.com", null)]                // prefix must be leftmost
    [InlineData("mta-sts.", null)]                               // empty remainder
    [InlineData("mta-sts..example.com", null)]                   // empty label
    [InlineData("mta-stsX.example.com", null)]                   // prefix is a label, not a substring
    [InlineData("[::1]", null)]                                  // IPv6 literal
    [InlineData("mta-sts.example.com/path", null)]               // junk
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void HostMapping_Table(string? host, string? expected)
        => Assert.Equal(expected, MtaStsPolicyHostService.TryMapHostToDomain(host));

    [Fact]
    public void RenderedPolicy_IsByteExact_CrlfWithTrailingNewline()
    {
        var body = MtaStsCheckService.RenderPolicyFile(
            "enforce", 604800, ["mx1.acme.example", "*.mail.acme.example"]);

        Assert.Equal(
            "version: STSv1\r\nmode: enforce\r\nmx: mx1.acme.example\r\nmx: *.mail.acme.example\r\nmax_age: 604800\r\n",
            body);
    }

    [Fact]
    public void RenderedPolicy_ModeNoneWithoutMx_OmitsMxEntirely()
    {
        var body = MtaStsCheckService.RenderPolicyFile("none", 86400, []);
        Assert.DoesNotContain("mx:", body);
        Assert.Equal("version: STSv1\r\nmode: none\r\nmax_age: 86400\r\n", body);
    }

    [Theory]
    [InlineData("enforce", 604800, new[] { "mx1.acme.example", "*.mail.acme.example" })]
    [InlineData("testing", 3600, new[] { "a.b" })]
    [InlineData("none", 31557600, new string[0])]
    public void RenderedPolicy_RoundTripsThroughTheParser(string mode, int maxAge, string[] patterns)
    {
        var parsed = MtaStsCheckService.ParsePolicy(
            MtaStsCheckService.RenderPolicyFile(mode, maxAge, patterns));

        Assert.True(parsed.Valid);
        Assert.Empty(parsed.Issues);
        Assert.Equal(mode, parsed.Mode);
        Assert.Equal(maxAge, parsed.MaxAgeSeconds);
        Assert.Equal(patterns, parsed.MxPatterns);
    }

    [Theory]
    [InlineData("mx1.example.com", true)]
    [InlineData("*.example.com", true)]
    [InlineData("MX1.Example.COM.", true)]     // normalized before the check
    [InlineData("a-b.example.com", true)]
    [InlineData("example", false)]             // one label is not a mail host
    [InlineData("*.example", false)]           // wildcard still needs two labels after it? no — *.example → "example", one label
    [InlineData("*.*.example.com", false)]     // one wildcard, leftmost only
    [InlineData("mx_1.example.com", false)]    // underscore
    [InlineData("-bad.example.com", false)]    // leading hyphen
    [InlineData("bad-.example.com", false)]    // trailing hyphen
    [InlineData("mx1..example.com", false)]    // empty label
    [InlineData("", false)]
    [InlineData("*.", false)]
    public void MxPatternValidator_Table(string pattern, bool valid)
        => Assert.Equal(valid, MtaStsCheckService.IsValidMxPattern(pattern));
}
