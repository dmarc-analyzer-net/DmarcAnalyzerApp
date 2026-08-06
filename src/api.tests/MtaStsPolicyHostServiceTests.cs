using DmarcAnalyzer.Api.Application.MtaSts;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The serving lookup and its cache discipline. Positive-only caching is the
/// load-bearing rule: negative results are keyed by attacker-chosen Host
/// values, so caching them would let random Host headers fill the cache.
/// </summary>
public sealed class MtaStsPolicyHostServiceTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static MtaStsPolicyHostService Service(DmarcAnalyzerDbContext db, IMemoryCache cache)
        => new(db, cache, Options.Create(new MtaStsOptions()));

    private static async Task<(Client, Domain)> SeedAsync(
        DmarcAnalyzerDbContext db, string name = "acme.example",
        bool domainActive = true, bool policyEnabled = true)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC" };
        var domain = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = name, IsActive = domainActive };
        var policy = new MtaStsPolicy
        {
            DomainId = domain.Id,
            Enabled = policyEnabled,
            Mode = "testing",
            MaxAgeSeconds = 86400,
            MxPatterns = "mx1.acme.example",
            PolicyId = "20260801000000",
        };
        db.AddRange(client, domain, policy);
        await db.SaveChangesAsync();
        return (client, domain);
    }

    [Fact]
    public async Task ServesTheRenderedPolicy_ForTheMappedHost()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var body = await Service(db, cache)
            .GetPolicyBodyForHostAsync("mta-sts.acme.example", CancellationToken.None);

        Assert.Equal("version: STSv1\r\nmode: testing\r\nmx: mx1.acme.example\r\nmax_age: 86400\r\n", body);
    }

    [Theory]
    [InlineData("mta-sts.unknown.example")]  // no such domain
    [InlineData("acme.example")]             // no mta-sts prefix
    [InlineData("")]
    public async Task UnknownOrUnmappedHosts_AreNull(string host)
    {
        await using var db = NewDb();
        await SeedAsync(db);
        using var cache = new MemoryCache(new MemoryCacheOptions());

        Assert.Null(await Service(db, cache).GetPolicyBodyForHostAsync(host, CancellationToken.None));
    }

    [Fact]
    public async Task DisabledPolicyAndInactiveDomain_ServeNothing()
    {
        await using var db = NewDb();
        await SeedAsync(db, name: "off.example", policyEnabled: false);
        await SeedAsync(db, name: "gone.example", domainActive: false);
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var service = Service(db, cache);
        Assert.Null(await service.GetPolicyBodyForHostAsync("mta-sts.off.example", CancellationToken.None));
        Assert.Null(await service.GetPolicyBodyForHostAsync("mta-sts.gone.example", CancellationToken.None));
    }

    [Fact]
    public async Task PositiveResults_AreCached_WithinTheTtl()
    {
        await using var db = NewDb();
        var (_, domain) = await SeedAsync(db);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Service(db, cache);

        var first = await service.GetPolicyBodyForHostAsync("mta-sts.acme.example", CancellationToken.None);

        // Mutate the row behind the cache's back: the served body must not move
        // until the TTL (or an eviction on save) does it.
        var policy = await db.MtaStsPolicies.SingleAsync(p => p.DomainId == domain.Id);
        policy.Mode = "enforce";
        await db.SaveChangesAsync();

        var second = await service.GetPolicyBodyForHostAsync("mta-sts.acme.example", CancellationToken.None);
        Assert.Equal(first, second);

        // The admin service evicts this exact key on save; prove eviction works.
        cache.Remove(MtaStsPolicyHostService.CacheKey(domain.Name));
        var third = await service.GetPolicyBodyForHostAsync("mta-sts.acme.example", CancellationToken.None);
        Assert.Contains("mode: enforce", third);
    }

    [Fact]
    public async Task NegativeResults_AreNeverCached()
    {
        await using var db = NewDb();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Service(db, cache);

        // Miss first — an attacker-chosen Host must not occupy the cache…
        Assert.Null(await service.GetPolicyBodyForHostAsync("mta-sts.late.example", CancellationToken.None));

        // …and a policy created afterwards is served immediately, no TTL wait.
        var client = new Client { Id = Guid.NewGuid(), Name = "late", Slug = "late", Timezone = "UTC" };
        var domain = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = "late.example", IsActive = true };
        db.AddRange(client, domain, new MtaStsPolicy
        {
            DomainId = domain.Id, Mode = "none", MaxAgeSeconds = 3600,
            MxPatterns = string.Empty, PolicyId = "20260801000000",
        });
        await db.SaveChangesAsync();

        Assert.NotNull(await service.GetPolicyBodyForHostAsync("mta-sts.late.example", CancellationToken.None));
    }

    [Fact]
    public async Task AskEndpointLookup_SharesTheSameAnswer()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = Service(db, cache);

        Assert.True(await service.IsKnownPolicyHostAsync("mta-sts.acme.example", CancellationToken.None));
        Assert.False(await service.IsKnownPolicyHostAsync("mta-sts.stranger.example", CancellationToken.None));
        Assert.False(await service.IsKnownPolicyHostAsync("acme.example", CancellationToken.None));
    }
}
