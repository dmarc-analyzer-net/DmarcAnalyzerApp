using DmarcAnalyzer.Api.Application.Analytics;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The one part of <see cref="AuthoritativeDnsClientLocator"/> that's pure and
/// network-free: the order of ancestor names to ask for NS records. The actual
/// NS/A lookups and the resulting non-recursive client are verified manually
/// against real infrastructure (no seam to mock <c>LookupClient</c> itself).
/// </summary>
public sealed class AuthoritativeDnsClientLocatorTests
{
    [Fact]
    public void SimpleDomain_TriesItselfThenParent()
    {
        var names = AuthoritativeDnsClientLocator.SearchNames("_mta-sts.acme.example");

        Assert.Equal(["_mta-sts.acme.example", "acme.example"], names);
    }

    [Fact]
    public void MultiLabelSuffix_WalksUpWithoutNeedingAPublicSuffixList()
    {
        var names = AuthoritativeDnsClientLocator.SearchNames("_mta-sts.mail.example.co.uk");

        Assert.Equal(
            ["_mta-sts.mail.example.co.uk", "mail.example.co.uk", "example.co.uk", "co.uk"],
            names);
    }

    [Fact]
    public void StopsBeforeABareTld()
    {
        var names = AuthoritativeDnsClientLocator.SearchNames("example.com");

        Assert.Equal(["example.com"], names);
        Assert.DoesNotContain("com", names);
    }

    [Fact]
    public void TrimsWhitespaceAndTrailingDot()
    {
        var names = AuthoritativeDnsClientLocator.SearchNames(" acme.example. ");

        Assert.Equal(["acme.example"], names);
    }

    [Fact]
    public void CapsAtMaxAncestors()
    {
        var names = AuthoritativeDnsClientLocator.SearchNames("a.b.c.d.e.f.example.com");

        Assert.Equal(AuthoritativeDnsClientLocator.MaxAncestors, names.Count);
    }
}
