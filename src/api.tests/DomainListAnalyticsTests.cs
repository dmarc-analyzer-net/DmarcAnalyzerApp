using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Covers the per-domain aggregates behind the Domains list. Reports, Sources and
/// Reporters each count something distinct across a set of reports, and Reporters is the
/// awkward one: the organisation name lives on the parent report, not the record. It was
/// originally written as a projection through the navigation, which EF turned into one
/// correlated subquery per domain — 1,930ms of a 1,988ms request against real data. The
/// counts are asserted here so the rewrite to an explicit join cannot change what they
/// mean, and so the next person to touch the query has something to break.
/// </summary>
public sealed class DomainListAnalyticsTests
{
    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static Domain SeedDomain(DmarcAnalyzerDbContext db, string name)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC",
            RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var domain = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = name, IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.AddRange(client, domain);
        return domain;
    }

    /// <summary>Adds one report from <paramref name="organization"/> with the given source IPs.</summary>
    private static void AddReport(
        DmarcAnalyzerDbContext db, Guid domainId, string organization, int daysAgo, params (string Ip, int Count)[] records)
    {
        var report = new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domainId, MailboxSourceId = Guid.NewGuid(),
            OrganizationName = organization, ReportId = Guid.NewGuid().ToString("N"),
            RangeBeginUtc = DateTime.UtcNow.AddDays(-daysAgo),
            RangeEndUtc = DateTime.UtcNow.AddDays(-daysAgo).AddHours(23),
            RecordCount = records.Length, IngestedAtUtc = DateTime.UtcNow,
            PublishedPolicy = "none", SubdomainPolicy = "none", PublishedPct = 100,
        };
        db.Add(report);
        foreach (var r in records)
        {
            db.Add(new DmarcReportRecord
            {
                Id = Guid.NewGuid(), DmarcReportId = report.Id,
                ReportRangeBeginUtc = report.RangeBeginUtc,
                SourceIp = r.Ip, MessageCount = r.Count,
                Disposition = "none", DkimResult = "pass", SpfResult = "pass",
            });
        }
    }

    [Fact]
    public async Task CountsDistinctReportersReportsAndSources()
    {
        using var db = NewDb();
        var domain = SeedDomain(db, "acme.example");

        // Three reports, two organisations, and 203.0.113.5 seen by both — so each count
        // has to deduplicate over a different axis: 3 reports, 2 reporters, 3 sources.
        AddReport(db, domain.Id, "google.com", 3, ("203.0.113.5", 10), ("203.0.113.6", 5));
        AddReport(db, domain.Id, "google.com", 2, ("203.0.113.7", 2));
        AddReport(db, domain.Id, "yahoo.com", 1, ("203.0.113.5", 3));
        await db.SaveChangesAsync();

        var rows = await TestAnalytics
            .Service(db, TestCurrentUserContext.Admin())
            .ListDomainAnalyticsAsync(30, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(20, row.Messages);
        Assert.Equal(3, row.Reports);
        Assert.Equal(2, row.Reporters);
        Assert.Equal(3, row.Sources);
    }

    [Fact]
    public async Task ExcludesReportsOutsideTheWindow()
    {
        using var db = NewDb();
        var domain = SeedDomain(db, "acme.example");

        // The window anchors to the newest report, so this pair is 1 and 400 days before
        // that anchor rather than before now. Only the recent one is inside 30 days.
        AddReport(db, domain.Id, "google.com", 1, ("203.0.113.5", 10));
        AddReport(db, domain.Id, "yahoo.com", 400, ("203.0.113.9", 999));
        await db.SaveChangesAsync();

        var rows = await TestAnalytics
            .Service(db, TestCurrentUserContext.Admin())
            .ListDomainAnalyticsAsync(30, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(10, row.Messages);
        Assert.Equal(1, row.Reports);
        Assert.Equal(1, row.Reporters);
        Assert.Equal(1, row.Sources);
    }

    [Fact]
    public async Task ReportsZeroesForADomainWithNoDataInTheWindow()
    {
        using var db = NewDb();
        var withData = SeedDomain(db, "busy.example");
        var quiet = SeedDomain(db, "quiet.example");

        AddReport(db, withData.Id, "google.com", 1, ("203.0.113.5", 10));
        await db.SaveChangesAsync();

        var rows = await TestAnalytics
            .Service(db, TestCurrentUserContext.Admin())
            .ListDomainAnalyticsAsync(30, CancellationToken.None);

        // A domain absent from the aggregate must still appear, reading zero rather than
        // dropping out of the list — the join is inner, the presentation is not.
        Assert.Equal(2, rows.Count);
        var quietRow = Assert.Single(rows, r => r.DomainId == quiet.Id);
        Assert.Equal(0, quietRow.Messages);
        Assert.Equal(0, quietRow.Reports);
        Assert.Equal(0, quietRow.Reporters);
        Assert.Equal(0, quietRow.Sources);
        Assert.Equal("no_data", quietRow.Status);
    }
}
