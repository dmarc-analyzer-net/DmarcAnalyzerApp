using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.MtaSts;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// How <see cref="MtaStsReadinessService"/> maps persisted state onto the pure
/// evaluator's input — specifically the "never yet reachable" distinction that
/// the evaluator itself has no way to get wrong, because it lives entirely in
/// this mapping. Before this fix, a hosted policy mid-DNS-setup (TXT/CNAME not
/// propagated yet) was told its checks were "failing", identical to a real
/// regression after the policy had once worked.
/// </summary>
public sealed class MtaStsReadinessServiceTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task<Guid> SeedDomainWithPolicyAsync(DmarcAnalyzerDbContext db, string mode = "testing")
    {
        var client = new Client { Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC" };
        var domain = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = "acme.example", IsActive = true };
        db.AddRange(client, domain, new MtaStsPolicy
        {
            DomainId = domain.Id, Enabled = true, Mode = mode, MaxAgeSeconds = 86400,
            MxPatterns = "mx1.acme.example", PolicyId = "20260807000000",
            ModeChangedAtUtc = DateTime.UtcNow.AddDays(-20),
        });
        await db.SaveChangesAsync();
        return domain.Id;
    }

    [Fact]
    public async Task NeverReachable_MissingTxt_IsInsufficientData_NotFailingChecks()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithPolicyAsync(db);
        // Exactly the screenshot's scenario: a freshly hosted policy, DNS not
        // published yet. The pass ran once and correctly found nothing.
        db.Add(new MtaStsState
        {
            DomainId = domainId, DnsRecordStatus = "missing",
            LastFetchOkAtUtc = null, LastCheckedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new MtaStsReadinessService(db, new TlsRptQueryService(db, TestCurrentUserContext.Admin()));
        var readiness = await service.GetForDomainAsync(domainId, CancellationToken.None);

        Assert.NotNull(readiness);
        Assert.Equal(MtaStsReadinessStatus.InsufficientData, readiness!.Status);
        Assert.All(readiness.Checks, c => Assert.Equal("unknown", c.Status));
        Assert.DoesNotContain("failing", readiness.BlockedReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NeverReachable_TxtFoundButFetchStillFailing_IsAlsoInsufficientData()
    {
        // The CNAME/proxy case: TXT is live, but the policy fetch hasn't
        // succeeded yet (propagation lag). Same "still setting up" verdict.
        await using var db = NewDb();
        var domainId = await SeedDomainWithPolicyAsync(db);
        db.Add(new MtaStsState
        {
            DomainId = domainId, DnsRecordStatus = "found", FetchStatus = "connect_failed",
            LastFetchOkAtUtc = null, LastCheckedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new MtaStsReadinessService(db, new TlsRptQueryService(db, TestCurrentUserContext.Admin()));
        var readiness = await service.GetForDomainAsync(domainId, CancellationToken.None);

        Assert.Equal(MtaStsReadinessStatus.InsufficientData, readiness!.Status);
    }

    [Fact]
    public async Task OnceReachable_ARealRegressionStillBlocks()
    {
        // It worked before (LastFetchOkAtUtc set) and something broke since —
        // this must NOT be softened; it is exactly what the gate exists to catch.
        await using var db = NewDb();
        var domainId = await SeedDomainWithPolicyAsync(db);
        db.Add(new MtaStsState
        {
            DomainId = domainId, DnsRecordStatus = "missing",
            LastFetchOkAtUtc = DateTime.UtcNow.AddDays(-5), LastCheckedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new MtaStsReadinessService(db, new TlsRptQueryService(db, TestCurrentUserContext.Admin()));
        var readiness = await service.GetForDomainAsync(domainId, CancellationToken.None);

        Assert.Equal(MtaStsReadinessStatus.NotReady, readiness!.Status);
        Assert.Contains("TXT record", readiness.BlockedReason);
    }

    [Fact]
    public async Task OnceReachable_AndNowHealthy_ReadsChecksNormally()
    {
        await using var db = NewDb();
        var domainId = await SeedDomainWithPolicyAsync(db);
        db.Add(new MtaStsState
        {
            DomainId = domainId, DnsRecordStatus = "found", FetchStatus = "ok", PolicyValid = true,
            UnmatchedMxHostsJson = "[]", LastFetchOkAtUtc = DateTime.UtcNow, LastCheckedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new MtaStsReadinessService(db, new TlsRptQueryService(db, TestCurrentUserContext.Admin()));
        var readiness = await service.GetForDomainAsync(domainId, CancellationToken.None);

        Assert.All(readiness!.Checks, c => Assert.Equal("pass", c.Status));
    }
}
