using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class EnforcementGuidanceTests
{
    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    // Seeds one domain with a single report whose records are (ip, count, aligned?).
    private static async Task<(Guid clientId, Guid domainId)> SeedAsync(
        DmarcAnalyzerDbContext db, string publishedPolicy, params (string Ip, int Count, bool Aligned)[] sources)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC",
            RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var domain = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = "acme.example", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var report = new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domain.Id, MailboxSourceId = Guid.NewGuid(),
            OrganizationName = "google.com", ReportId = "r1",
            RangeBeginUtc = DateTime.UtcNow.AddDays(-2), RangeEndUtc = DateTime.UtcNow.AddDays(-1),
            RecordCount = sources.Length, IngestedAtUtc = DateTime.UtcNow,
            PublishedPolicy = publishedPolicy, SubdomainPolicy = publishedPolicy, PublishedPct = 100,
        };
        db.AddRange(client, domain, report);
        foreach (var s in sources)
        {
            db.Add(new DmarcReportRecord
            {
                Id = Guid.NewGuid(), DmarcReportId = report.Id, SourceIp = s.Ip, MessageCount = s.Count,
                Disposition = "none",
                DkimResult = s.Aligned ? "pass" : "fail",
                SpfResult = "fail",
            });
        }
        await db.SaveChangesAsync();
        return (client.Id, domain.Id);
    }

    private static AnalyticsQueryService Service(DmarcAnalyzerDbContext db)
        => new(db, TestCurrentUserContext.Admin());

    /// <summary>
    /// Places a report for `domain` at `daysAgo`, and a second domain's report
    /// today so the analytics window (which anchors to the newest report
    /// tenant-wide) leaves the first one outside it.
    /// </summary>
    private static async Task<Guid> SeedStaleDomainAsync(
        DmarcAnalyzerDbContext db, string? publishedPolicy, int daysAgo = 400)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC",
            RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var stale = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = "stale.example", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var recent = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = "recent.example", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.AddRange(client, stale, recent);

        if (publishedPolicy is not null)
        {
            db.Add(new DmarcReport
            {
                Id = Guid.NewGuid(), DomainId = stale.Id, MailboxSourceId = Guid.NewGuid(),
                OrganizationName = "google.com", ReportId = "old",
                RangeBeginUtc = DateTime.UtcNow.AddDays(-daysAgo - 1),
                RangeEndUtc = DateTime.UtcNow.AddDays(-daysAgo),
                RecordCount = 0, IngestedAtUtc = DateTime.UtcNow.AddDays(-daysAgo),
                PublishedPolicy = publishedPolicy, SubdomainPolicy = null, PublishedPct = 100,
            });
        }

        // Anchors the window to now, pushing the stale report out of it.
        db.Add(new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = recent.Id, MailboxSourceId = Guid.NewGuid(),
            OrganizationName = "google.com", ReportId = "new",
            RangeBeginUtc = DateTime.UtcNow.AddDays(-2), RangeEndUtc = DateTime.UtcNow.AddDays(-1),
            RecordCount = 0, IngestedAtUtc = DateTime.UtcNow,
            PublishedPolicy = "reject", SubdomainPolicy = null, PublishedPct = 100,
        });

        await db.SaveChangesAsync();
        return stale.Id;
    }

    /// <summary>
    /// The regression this guards: with no traffic in the window the guidance
    /// used to return "none" unconditionally, so a domain whose newest report is
    /// months old was told to publish p=none. On the live instance that was 26
    /// domains, every one of them already at p=reject.
    /// </summary>
    [Theory]
    [InlineData("reject")]
    [InlineData("quarantine")]
    public async Task NoDataInWindow_KeepsTheKnownPolicy_AndNeverAdvisesWeakening(string published)
    {
        await using var db = NewDb();
        var domainId = await SeedStaleDomainAsync(db, published);

        var g = await Service(db).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.Equal(0, g!.Messages);
        Assert.Equal(published, g.RecommendedPolicy);
        Assert.False(g.ReadyToAdvance);
        Assert.Contains($"p={published}", g.Rationale);
        Assert.DoesNotContain("publish a p=none", g.Rationale);
    }

    [Fact]
    public async Task NoReportsEverForTheDomain_StillAdvisesStartingAtNone()
    {
        await using var db = NewDb();
        var domainId = await SeedStaleDomainAsync(db, publishedPolicy: null);

        var g = await Service(db).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.Equal(0, g!.Messages);
        Assert.Equal("none", g.RecommendedPolicy);
        Assert.False(g.ReadyToAdvance);
        Assert.Contains("No DMARC reports have arrived", g.Rationale);
    }

    [Fact]
    public async Task AtNone_WithFailingVolume_RecommendsFixingSources()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "none",
            ("203.0.113.10", 800, true),   // aligned
            ("198.51.100.24", 200, false)); // failing

        var g = await Service(db).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.Equal("none", g!.CurrentPolicy);
        Assert.False(g.ReadyToAdvance);
        Assert.Equal("none", g.RecommendedPolicy); // stay put until fixed
        Assert.Equal(1, g.BlockingSourceCount);
        var blocker = Assert.Single(g.BlockingSources);
        Assert.Equal("198.51.100.24", blocker.SourceIp);
        Assert.Equal(200, blocker.FailedMessages);
        Assert.Equal(1000, g.Messages);
        Assert.Equal(800, g.CompliantMessages);
    }

    [Fact]
    public async Task AtNone_WhenAligned_RecommendsQuarantine()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "none",
            ("203.0.113.10", 1000, true),
            ("198.51.100.24", 2, false)); // negligible

        var g = await Service(db).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.True(g!.ReadyToAdvance);
        Assert.Equal("quarantine", g.RecommendedPolicy);
        // p=none but ≥98% aligned → the existing resolver classifies this as monitoring.
        Assert.Equal(EnforcementStatus.Monitoring, g.EnforcementStatus);
    }

    [Fact]
    public async Task AtReject_ReportsEnforced()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "reject", ("203.0.113.10", 500, true));

        var g = await Service(db).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.True(g!.ReadyToAdvance);
        Assert.Equal("reject", g.RecommendedPolicy);
        Assert.Equal(EnforcementStatus.Enforced, g.EnforcementStatus);
        Assert.Empty(g.BlockingSources);
    }

    [Fact]
    public async Task CrossTenantDomain_ForViewer_ReturnsNull()
    {
        await using var db = NewDb();
        var (clientId, domainId) = await SeedAsync(db, "none", ("203.0.113.10", 10, true));

        // A viewer granted a *different* client must not see this domain.
        var svc = new AnalyticsQueryService(db, TestCurrentUserContext.Viewer(Guid.NewGuid()));
        var g = await svc.GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.Null(g);
    }
}
