using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class DnsPolicyCacheTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static async Task<Domain> SeedDomainAsync(DmarcAnalyzerDbContext db, string name, bool active = true)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC",
            RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var domain = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = name, IsActive = active,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.AddRange(client, domain);
        await db.SaveChangesAsync();
        return domain;
    }

    private static DnsPolicyCache Cache(DmarcAnalyzerDbContext db, IDnsTxtResolver dns)
        => new(db, new DmarcPolicyResolver(dns), NullLogger<DnsPolicyCache>.Instance);

    /// <summary>What a detail-page lookup resolves: an own record, so nothing inherited.</summary>
    private static EffectiveDmarcPolicy OwnRecord(string raw)
    {
        var record = RecordInspectionService.ParseDmarc([raw]);
        return new EffectiveDmarcPolicy(record, record.Policy, record.Status, null);
    }

    [Fact]
    public async Task RefreshAll_StoresPolicyStatusAndCheckedAt()
    {
        await using var db = NewDb();
        var domain = await SeedDomainAsync(db, "acme.example");

        var result = await Cache(db, TestDnsTxtResolver.WithPolicy("acme.example", "reject"))
            .RefreshAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Checked);
        Assert.Equal(1, result.Changed);
        var stored = await db.Domains.SingleAsync(x => x.Id == domain.Id);
        Assert.Equal("reject", stored.DnsPolicy);
        Assert.Equal(RecordLookupStatus.Found, stored.DnsLookupStatus);
        Assert.NotNull(stored.DnsCheckedAtUtc);
    }

    [Fact]
    public async Task RefreshAll_WhenNoRecordPublished_ClearsThePolicyButRecordsMissing()
    {
        await using var db = NewDb();
        var domain = await SeedDomainAsync(db, "acme.example");
        await Cache(db, TestDnsTxtResolver.WithPolicy("acme.example", "reject")).RefreshAllAsync(CancellationToken.None);

        // The record was withdrawn from DNS.
        await Cache(db, TestDnsTxtResolver.Empty()).RefreshAllAsync(CancellationToken.None);

        var stored = await db.Domains.SingleAsync(x => x.Id == domain.Id);
        Assert.Null(stored.DnsPolicy);
        Assert.Equal(RecordLookupStatus.Missing, stored.DnsLookupStatus);
    }

    /// <summary>
    /// A transient SERVFAIL must not make a p=reject domain look unprotected in the
    /// list. The status records that the check failed; the policy stays put.
    /// </summary>
    [Fact]
    public async Task RefreshAll_WhenLookupFails_KeepsTheLastKnownPolicy()
    {
        await using var db = NewDb();
        var domain = await SeedDomainAsync(db, "acme.example");
        await Cache(db, TestDnsTxtResolver.WithPolicy("acme.example", "reject")).RefreshAllAsync(CancellationToken.None);

        var failing = new TestDnsTxtResolver().FailFor("_dmarc.acme.example");
        var result = await Cache(db, failing).RefreshAllAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);
        var stored = await db.Domains.SingleAsync(x => x.Id == domain.Id);
        Assert.Equal("reject", stored.DnsPolicy);
        Assert.Equal(RecordLookupStatus.LookupFailed, stored.DnsLookupStatus);
    }

    [Fact]
    public async Task RefreshAll_SkipsInactiveDomains()
    {
        await using var db = NewDb();
        var domain = await SeedDomainAsync(db, "retired.example", active: false);

        var result = await Cache(db, TestDnsTxtResolver.WithPolicy("retired.example", "reject"))
            .RefreshAllAsync(CancellationToken.None);

        Assert.Equal(0, result.Checked);
        Assert.Null((await db.Domains.SingleAsync(x => x.Id == domain.Id)).DnsPolicy);
    }

    /// <summary>
    /// UpdatedAtUtc means "an operator changed this domain". A background refresh is
    /// not that, and bumping it would make every domain look freshly edited.
    /// </summary>
    [Fact]
    public async Task Refresh_DoesNotTouchUpdatedAtUtc()
    {
        await using var db = NewDb();
        var domain = await SeedDomainAsync(db, "acme.example");
        var before = domain.UpdatedAtUtc;

        await Cache(db, TestDnsTxtResolver.WithPolicy("acme.example", "quarantine"))
            .RefreshAllAsync(CancellationToken.None);

        Assert.Equal(before, (await db.Domains.SingleAsync(x => x.Id == domain.Id)).UpdatedAtUtc);
    }

    [Fact]
    public async Task WriteBack_CorrectsAStalePolicy()
    {
        await using var db = NewDb();
        var domain = await SeedDomainAsync(db, "acme.example");
        await Cache(db, TestDnsTxtResolver.WithPolicy("acme.example", "none")).RefreshAllAsync(CancellationToken.None);

        // What a detail-page lookup would have just resolved.
        var fresh = OwnRecord("v=DMARC1; p=reject");
        await Cache(db, TestDnsTxtResolver.Empty()).WriteBackAsync(domain.Id, fresh, CancellationToken.None);

        Assert.Equal("reject", (await db.Domains.SingleAsync(x => x.Id == domain.Id)).DnsPolicy);
    }

    [Fact]
    public async Task WriteBack_WhenUnchanged_LeavesTheRowAlone()
    {
        await using var db = NewDb();
        var domain = await SeedDomainAsync(db, "acme.example");
        await Cache(db, TestDnsTxtResolver.WithPolicy("acme.example", "reject")).RefreshAllAsync(CancellationToken.None);
        var checkedAt = (await db.Domains.SingleAsync(x => x.Id == domain.Id)).DnsCheckedAtUtc;

        var same = OwnRecord("v=DMARC1; p=reject");
        await Cache(db, TestDnsTxtResolver.Empty()).WriteBackAsync(domain.Id, same, CancellationToken.None);

        // Unchanged means no write at all, so the timestamp does not advance either —
        // otherwise every page view would write a row.
        Assert.Equal(checkedAt, (await db.Domains.SingleAsync(x => x.Id == domain.Id)).DnsCheckedAtUtc);
    }
}
