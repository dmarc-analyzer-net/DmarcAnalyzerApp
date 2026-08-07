using DmarcAnalyzer.Api.Application.Analytics;
using DnsClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Minimal stand-in for <see cref="IAuthoritativeDnsClientLocator"/> in tests
/// that only exercise the cache short-circuit in <c>DnsTxtResolver</c>/
/// <c>DnsMxResolver</c> and never reach the authoritative-lookup path.
/// </summary>
public sealed class NullAuthoritativeDnsClientLocator : IAuthoritativeDnsClientLocator
{
    public Task<LookupClient?> LocateAsync(string name, CancellationToken ct) =>
        Task.FromResult<LookupClient?>(null);
}

/// <summary>
/// The one half of the bypassCache behavior that is testable without a real
/// DNS query: a cache hit short-circuits before <c>DnsTxtResolver</c>/
/// <c>DnsMxResolver</c> ever reach their static <c>LookupClient</c>, so
/// pre-seeding the cache and calling with <c>bypassCache: false</c> is
/// deterministic and network-free. The other half — that
/// <c>bypassCache: true</c> actually reaches the network instead of returning
/// the seeded value — needs a real resolver and is verified manually against
/// the compose stack, the same way the rest of this class's DNS behavior
/// always has been (there is no seam to mock <c>LookupClient</c> itself).
/// </summary>
public sealed class DnsResolverCacheTests
{
    [Fact]
    public async Task TxtResolver_CacheHit_ReturnsWithoutBypass()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        IReadOnlyList<string> seeded = ["v=STSv1; id=cached"];
        cache.Set("dns-txt:_mta-sts.acme.example", seeded, TimeSpan.FromMinutes(5));

        var resolver = new DnsTxtResolver(cache, NullLogger<DnsTxtResolver>.Instance, new NullAuthoritativeDnsClientLocator());
        var result = await resolver.ResolveAsync("_mta-sts.acme.example", CancellationToken.None);

        Assert.Same(seeded, result);
    }

    [Fact]
    public async Task MxResolver_CacheHit_ReturnsWithoutBypass()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        IReadOnlyList<MxHost> seeded = [new MxHost(10, "mx1.acme.example")];
        cache.Set("dns-mx:acme.example", seeded, TimeSpan.FromMinutes(5));

        var resolver = new DnsMxResolver(cache, NullLogger<DnsMxResolver>.Instance, new NullAuthoritativeDnsClientLocator());
        var result = await resolver.ResolveAsync("acme.example", CancellationToken.None);

        Assert.Same(seeded, result);
    }
}
