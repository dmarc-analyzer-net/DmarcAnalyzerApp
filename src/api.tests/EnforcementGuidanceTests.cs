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

    private static Task<(Guid clientId, Guid domainId)> SeedAsync(
        DmarcAnalyzerDbContext db, string publishedPolicy, params (string Ip, int Count, bool Aligned)[] sources)
        => SeedAsync(db, publishedPolicy, "acme.example", sources);

    // Seeds one domain with a single report whose records are (ip, count, aligned?).
    private static async Task<(Guid clientId, Guid domainId)> SeedAsync(
        DmarcAnalyzerDbContext db, string publishedPolicy, string domainName,
        params (string Ip, int Count, bool Aligned)[] sources)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC",
            RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var domain = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = domainName, IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var report = new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domain.Id, ReportSourceId = Guid.NewGuid(),
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
                ReportRangeBeginUtc = report.RangeBeginUtc,
                Id = Guid.NewGuid(), DmarcReportId = report.Id, SourceIp = s.Ip, MessageCount = s.Count,
                Disposition = "none",
                DkimResult = s.Aligned ? "pass" : "fail",
                SpfResult = "fail",
            });
        }
        await db.SaveChangesAsync();
        return (client.Id, domain.Id);
    }

    // The guidance reads the current policy from live DNS, not from reports, so
    // each test states what DNS publishes. No stub means no DMARC record at all.
    private static AnalyticsQueryService Service(DmarcAnalyzerDbContext db, IDnsTxtResolver? dns = null)
        => TestAnalytics.Service(db, TestCurrentUserContext.Admin(), dns);

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
                Id = Guid.NewGuid(), DomainId = stale.Id, ReportSourceId = Guid.NewGuid(),
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
            Id = Guid.NewGuid(), DomainId = recent.Id, ReportSourceId = Guid.NewGuid(),
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

        var dns = TestDnsTxtResolver.WithPolicy("stale.example", published);
        var g = await Service(db, dns).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.Equal(0, g!.Messages);
        Assert.Equal(published, g.RecommendedPolicy);
        Assert.False(g.ReadyToAdvance);
        Assert.Contains($"p={published}", g.Rationale);
        Assert.DoesNotContain("publish a p=none", g.Rationale);
    }

    [Fact]
    public async Task NoDmarcRecordInDns_AdvisesStartingAtNone()
    {
        await using var db = NewDb();
        var domainId = await SeedStaleDomainAsync(db, publishedPolicy: null);

        var g = await Service(db).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.Equal(0, g!.Messages);
        Assert.Equal("none", g.RecommendedPolicy);
        Assert.False(g.ReadyToAdvance);
        Assert.Contains("No DMARC record is published in DNS", g.Rationale);
    }

    [Fact]
    public async Task AtNone_WithFailingVolume_RecommendsFixingSources()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "none",
            ("203.0.113.10", 800, true),   // aligned
            ("198.51.100.24", 200, false)); // failing

        var g = await Service(db, TestDnsTxtResolver.WithPolicy("acme.example", "none")).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

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
    public async Task AtNone_WhenAligned_RecommendsQuarantineInTestingMode()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "none",
            ("203.0.113.10", 1000, true),
            ("198.51.100.24", 2, false)); // negligible

        var g = await Service(db, TestDnsTxtResolver.WithPolicy("acme.example", "none")).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.True(g!.ReadyToAdvance);
        Assert.Equal("quarantine", g.RecommendedPolicy);
        Assert.True(g.RecommendedTesting);
        // p=none but ≥98% aligned → the existing resolver classifies this as monitoring.
        Assert.Equal(EnforcementStatus.Monitoring, g.EnforcementStatus);
    }

    /// <summary>
    /// The whole point of #227: the card hands over a record, and it is the live one
    /// with the policy tags moved — not a composed record that silently drops rua.
    /// </summary>
    [Fact]
    public async Task WhenReadyToAdvance_TheRecordToPublish_IsTheLiveRecordWithTheStepApplied()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "none", ("203.0.113.10", 1000, true));

        var dns = new TestDnsTxtResolver().Publish(
            "_dmarc.acme.example",
            "v=DMARC1; p=none; rua=mailto:dmarc@acme.example; adkim=s");

        var g = await Service(db, dns).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        var change = g!.RecordChange;
        Assert.NotNull(change);
        Assert.Equal("_dmarc.acme.example", change!.Host);
        Assert.Equal("v=DMARC1; p=none; rua=mailto:dmarc@acme.example; adkim=s", change.Current);
        Assert.Equal(
            "v=DMARC1; p=quarantine; t=y; rua=mailto:dmarc@acme.example; adkim=s",
            change.Recommended);
        Assert.DoesNotContain("pct", change.Recommended);
    }

    /// <summary>
    /// A domain at p=quarantine with t=y is not enforcing quarantine — receivers apply
    /// one level below (RFC 9989 §4.7). Reading p= alone sent it to p=reject, skipping
    /// the step where quarantine actually starts holding mail.
    /// </summary>
    [Fact]
    public async Task AtQuarantineInTestingMode_WhenAligned_RecommendsLeavingTestingMode()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "quarantine", ("203.0.113.10", 1000, true));

        var dns = new TestDnsTxtResolver().Publish(
            "_dmarc.acme.example",
            "v=DMARC1; p=quarantine; t=y; rua=mailto:dmarc@acme.example");

        var g = await Service(db, dns).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.True(g!.CurrentTesting);
        Assert.True(g.ReadyToAdvance);
        Assert.Equal("quarantine", g.RecommendedPolicy);
        Assert.False(g.RecommendedTesting);
        Assert.Contains("t=y", g.RecommendedAction);
        Assert.Equal(
            "v=DMARC1; p=quarantine; rua=mailto:dmarc@acme.example",
            g.RecordChange!.Recommended);
    }

    [Fact]
    public async Task AtQuarantineEnforcing_WhenAligned_RecommendsReject()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "quarantine", ("203.0.113.10", 1000, true));

        var dns = new TestDnsTxtResolver().Publish(
            "_dmarc.acme.example",
            "v=DMARC1; p=quarantine; rua=mailto:dmarc@acme.example");

        var g = await Service(db, dns).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.False(g!.CurrentTesting);
        Assert.Equal("reject", g.RecommendedPolicy);
        Assert.False(g.RecommendedTesting);
        Assert.Equal("v=DMARC1; p=reject; rua=mailto:dmarc@acme.example", g.RecordChange!.Recommended);
    }

    /// <summary>
    /// "If the live record could not be read, say so and show nothing." A lookup that
    /// timed out is not evidence of anything, so there is no record to hand over.
    /// </summary>
    [Fact]
    public async Task WhenTheLiveLookupFailed_NoRecordIsOffered()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "none", ("203.0.113.10", 1000, true));

        var dns = new TestDnsTxtResolver().FailFor("_dmarc.acme.example");
        var g = await Service(db, dns).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.Null(g!.RecordChange);
    }

    /// <summary>
    /// An inherited policy belongs to the organisational domain. Offering its record as
    /// "the record to publish" would have the operator edit a record that covers every
    /// sibling domain too.
    /// </summary>
    [Fact]
    public async Task WhenThePolicyIsInherited_NoRecordIsOffered()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "none", "mail.acme.example", [("203.0.113.10", 1000, true)]);

        // mail.acme.example publishes nothing of its own; the organisational domain does,
        // so the tree walk finds the policy there.
        var dns = new TestDnsTxtResolver().Publish(
            "_dmarc.acme.example", "v=DMARC1; p=none; rua=mailto:dmarc@acme.example");

        var g = await Service(db, dns).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.Null(g!.RecordChange);
    }

    [Fact]
    public async Task WhenBlockedByFailingSources_NoRecordIsOffered()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "none",
            ("203.0.113.10", 800, true),
            ("198.51.100.24", 200, false));

        var dns = new TestDnsTxtResolver().Publish(
            "_dmarc.acme.example", "v=DMARC1; p=none; rua=mailto:dmarc@acme.example");

        var g = await Service(db, dns).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.False(g!.ReadyToAdvance);
        Assert.Null(g.RecordChange);
    }

    /// <summary>
    /// "A recommendation on thin data reads the same as one on a month of traffic" —
    /// so the guidance carries what it was drawn from.
    /// </summary>
    [Fact]
    public async Task TheGuidanceReportsHowMuchEvidenceItRestsOn()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "none",
            ("203.0.113.10", 3, true),
            ("198.51.100.24", 1, true));

        var g = await Service(db, TestDnsTxtResolver.WithPolicy("acme.example", "none"))
            .GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.Equal(4, g!.Evidence.Messages);
        Assert.Equal(2, g.Evidence.Sources);
        Assert.Equal(1, g.Evidence.Reports);
        Assert.Equal(1, g.Evidence.DaysWithData);
        Assert.NotNull(g.Evidence.FirstSeenUtc);
        Assert.NotNull(g.Evidence.LastSeenUtc);
    }

    [Fact]
    public async Task WithNoDataInTheWindow_TheEvidenceIsEmptyRatherThanAbsent()
    {
        await using var db = NewDb();
        var domainId = await SeedStaleDomainAsync(db, "reject");

        var g = await Service(db, TestDnsTxtResolver.WithPolicy("stale.example", "reject"))
            .GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.Equal(0, g!.Evidence.Messages);
        Assert.Equal(0, g.Evidence.Reports);
        Assert.Equal(0, g.Evidence.DaysWithData);
        Assert.Null(g.Evidence.FirstSeenUtc);
        Assert.Null(g.RecordChange);
    }

    /// <summary>
    /// RFC 9989 Appendix A.6 retired pct. Whatever the live record carries, the record
    /// we hand an operator must not reintroduce it — and the rationale must not advise it.
    /// </summary>
    [Fact]
    public async Task TheRecommendationNeverMentionsOrEmitsPct()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "none", ("203.0.113.10", 1000, true));

        var dns = new TestDnsTxtResolver().Publish(
            "_dmarc.acme.example", "v=DMARC1; p=none; pct=100; rua=mailto:dmarc@acme.example");

        var g = await Service(db, dns).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.DoesNotContain("pct", g!.RecommendedAction);
        Assert.DoesNotContain("pct", g.Rationale);
        Assert.Equal(
            "v=DMARC1; p=quarantine; t=y; rua=mailto:dmarc@acme.example",
            g.RecordChange!.Recommended);
        Assert.Contains("pct", g.RecordChange.ChangedTags);
    }

    [Fact]
    public async Task AtReject_ReportsEnforced()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "reject", ("203.0.113.10", 500, true));

        var g = await Service(db, TestDnsTxtResolver.WithPolicy("acme.example", "reject")).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.True(g!.ReadyToAdvance);
        Assert.Equal("reject", g.RecommendedPolicy);
        Assert.Equal(EnforcementStatus.Enforced, g.EnforcementStatus);
        Assert.Empty(g.BlockingSources);
    }

    /// <summary>
    /// p=reject with t=y is not full enforcement — receivers apply quarantine. Telling
    /// this domain to "maintain enforcement" would leave it one tag short of the policy
    /// it believes it is publishing.
    /// </summary>
    [Fact]
    public async Task AtRejectInTestingMode_IsNotTreatedAsFullyEnforced()
    {
        await using var db = NewDb();
        var (_, domainId) = await SeedAsync(db, "reject", ("203.0.113.10", 500, true));

        var dns = new TestDnsTxtResolver().Publish(
            "_dmarc.acme.example", "v=DMARC1; p=reject; t=y; rua=mailto:dmarc@acme.example");

        var g = await Service(db, dns).GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.NotNull(g);
        Assert.True(g!.CurrentTesting);
        Assert.NotEqual("Maintain enforcement", g.RecommendedAction);
        Assert.Contains("quarantine", g.Rationale);
        Assert.Equal("v=DMARC1; p=reject; rua=mailto:dmarc@acme.example", g.RecordChange!.Recommended);
    }

    [Fact]
    public async Task CrossTenantDomain_ForViewer_ReturnsNull()
    {
        await using var db = NewDb();
        var (clientId, domainId) = await SeedAsync(db, "none", ("203.0.113.10", 10, true));

        // A viewer granted a *different* client must not see this domain.
        var svc = TestAnalytics.Service(db, TestCurrentUserContext.Viewer(Guid.NewGuid()));
        var g = await svc.GetEnforcementGuidanceAsync(domainId, 30, CancellationToken.None);

        Assert.Null(g);
    }
}
