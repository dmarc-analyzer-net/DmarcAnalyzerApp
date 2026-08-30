using DmarcAnalyzer.Api.Application.Analytics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// How the resolver keys what it hands back. Every case here pre-seeds the cache so
/// the lookup short-circuits before any PTR query — the same seam
/// <see cref="DnsResolverCacheTests"/> uses, and the only way to make these
/// deterministic and network-free.
///
/// The behavior under test is that an address is answered under the spelling the
/// caller asked with. Report records store the source IP exactly as the reporter
/// wrote it and nothing normalizes it on the way in, so the caller's key is not
/// necessarily <c>IPAddress.ToString()</c>'s — and the sources table looks its
/// hostnames up by the string in the row.
/// </summary>
public sealed class HostnameResolverTests
{
    private static HostnameResolver ResolverWith(params (string Address, string? Hostname)[] cached)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        foreach (var (address, hostname) in cached)
        {
            cache.Set($"ptr:{address}", hostname, TimeSpan.FromMinutes(5));
        }

        return new HostnameResolver(cache, NullLogger<HostnameResolver>.Instance);
    }

    [Fact]
    public async Task AnswersIpv4UnderTheAddressAsAsked()
    {
        var resolver = ResolverWith(("192.0.2.1", "mail.example.com"));

        var resolved = await resolver.ResolveAsync(["192.0.2.1"], CancellationToken.None);

        Assert.Equal("mail.example.com", resolved["192.0.2.1"]);
    }

    [Fact]
    public async Task AnswersIpv6UnderTheSpellingTheCallerUsed()
    {
        // IPAddress.ToString() only ever answers lowercase and compressed. A reporter
        // is free to write either of these, and before this the response came back
        // keyed by the canonical form — a key the caller had no row for, so every
        // IPv6 source stayed hostname-less however often it was requested.
        var resolver = ResolverWith(("2001:db8::1", "mail.example.com"));

        var resolved = await resolver.ResolveAsync(
            ["2001:DB8::1", "2001:0db8:0000:0000:0000:0000:0000:0001"],
            CancellationToken.None);

        Assert.Equal("mail.example.com", resolved["2001:DB8::1"]);
        Assert.Equal("mail.example.com", resolved["2001:0db8:0000:0000:0000:0000:0000:0001"]);
    }

    [Fact]
    public async Task ReportsAnAddressWithNoPtrRecordAsNull()
    {
        // Distinct from being absent: the caller shows a hostname row only when there
        // is a name, but "asked and got nothing" is what stops it asking again.
        var resolver = ResolverWith(("192.0.2.7", null));

        var resolved = await resolver.ResolveAsync(["192.0.2.7"], CancellationToken.None);

        Assert.True(resolved.ContainsKey("192.0.2.7"));
        Assert.Null(resolved["192.0.2.7"]);
    }

    [Fact]
    public async Task LeavesOutWhatIsNotAnAddress()
    {
        // A record with no source IP reaches the table as an empty string. It has
        // nothing to look up, and must not come back as a key the caller then treats
        // as a resolved-to-nothing answer.
        var resolver = ResolverWith(("192.0.2.1", "mail.example.com"));

        var resolved = await resolver.ResolveAsync(
            ["192.0.2.1", "", "not-an-ip"],
            CancellationToken.None);

        Assert.Equal(["192.0.2.1"], resolved.Keys);
    }
}
