using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Covers the disposition rollups, which bucket by exact string and therefore drop
/// anything they do not name. That was harmless while the parser could only ever store
/// the three v1 values, and stopped being harmless once it began preserving RFC 9990's
/// <c>pass</c>: the messages still counted in the totals while accounting for none of the
/// breakdown, so the same panel said "2 of 2 messages" above "none 0 / quarantine 0 /
/// reject 0". Asserted at both sites — the tenant summary and the per-source detail —
/// because each builds the DTO from its own projection.
/// </summary>
public sealed class DispositionAnalyticsTests
{
    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static Domain SeedDomain(DmarcAnalyzerDbContext db)
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
        db.AddRange(client, domain);
        return domain;
    }

    /// <summary>One report carrying one record per disposition, all from the same source.</summary>
    private static void AddReport(
        DmarcAnalyzerDbContext db, Guid domainId, params (string Disposition, int Count)[] records)
        => AddReport(db, domainId, generator: null, records);

    private static void AddReport(
        DmarcAnalyzerDbContext db, Guid domainId, string? generator, params (string Disposition, int Count)[] records)
    {
        var report = new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domainId, ReportSourceId = Guid.NewGuid(),
            OrganizationName = "google.com", ReportId = Guid.NewGuid().ToString("N"),
            RangeBeginUtc = DateTime.UtcNow.AddDays(-1),
            RangeEndUtc = DateTime.UtcNow.AddDays(-1).AddHours(23),
            RecordCount = records.Length, IngestedAtUtc = DateTime.UtcNow,
            PublishedPolicy = "reject", SubdomainPolicy = "reject", PublishedPct = 100,
            Generator = generator,
        };
        db.Add(report);
        foreach (var r in records)
        {
            db.Add(new DmarcReportRecord
            {
                Id = Guid.NewGuid(), DmarcReportId = report.Id,
                ReportRangeBeginUtc = report.RangeBeginUtc,
                SourceIp = "203.0.113.5", MessageCount = r.Count,
                Disposition = r.Disposition, DkimResult = "pass", SpfResult = "pass",
            });
        }
    }

    /// <summary>
    /// The reporter's generator (RFC 9990) rides along on the source-detail reporters list.
    /// One reporter sends one generator, but its older rows predate the column, so the
    /// value has to win over a null rather than depend on which row the grouping saw first.
    /// </summary>
    [Fact]
    public async Task SourceDetailReportersCarryTheGeneratorWhenAnyReportHasOne()
    {
        using var db = NewDb();
        var domain = SeedDomain(db);
        AddReport(db, domain.Id, generator: null, ("none", 1));
        AddReport(db, domain.Id, generator: "Example Reporter v1.2", ("none", 1));
        await db.SaveChangesAsync();

        var detail = await TestAnalytics.Service(db, TestCurrentUserContext.Admin())
            .GetSourceDetailAsync(domain.Id, "203.0.113.5", 30, CancellationToken.None);

        var reporter = Assert.Single(detail!.Reporters);
        Assert.Equal("google.com", reporter.OrganizationName);
        Assert.Equal("Example Reporter v1.2", reporter.Generator);
    }

    [Fact]
    public async Task SummaryCountsPassInItsOwnBucket()
    {
        using var db = NewDb();
        var domain = SeedDomain(db);
        AddReport(db, domain.Id, ("none", 1), ("pass", 2), ("quarantine", 4), ("reject", 8));
        await db.SaveChangesAsync();

        var summary = await TestAnalytics
            .Service(db, TestCurrentUserContext.Admin())
            .GetSummaryAsync(30, CancellationToken.None);

        Assert.Equal(1, summary.Dispositions.None);
        Assert.Equal(2, summary.Dispositions.Pass);
        Assert.Equal(4, summary.Dispositions.Quarantine);
        Assert.Equal(8, summary.Dispositions.Reject);
    }

    [Fact]
    public async Task SourceDetailCountsPassInItsOwnBucket()
    {
        using var db = NewDb();
        var domain = SeedDomain(db);
        AddReport(db, domain.Id, ("none", 1), ("pass", 2), ("quarantine", 4), ("reject", 8));
        await db.SaveChangesAsync();

        var detail = await TestAnalytics
            .Service(db, TestCurrentUserContext.Admin())
            .GetSourceDetailAsync(domain.Id, "203.0.113.5", 30, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(1, detail.Dispositions.None);
        Assert.Equal(2, detail.Dispositions.Pass);
        Assert.Equal(4, detail.Dispositions.Quarantine);
        Assert.Equal(8, detail.Dispositions.Reject);
    }

    /// <summary>
    /// The point of the bucket: the breakdown has to account for every message the same
    /// panel claims exists. An all-pass source is the case that used to show three zeroes.
    /// </summary>
    [Fact]
    public async Task DispositionsAccountForEveryMessageInAnAllPassReport()
    {
        using var db = NewDb();
        var domain = SeedDomain(db);
        AddReport(db, domain.Id, ("pass", 7));
        await db.SaveChangesAsync();

        var detail = await TestAnalytics
            .Service(db, TestCurrentUserContext.Admin())
            .GetSourceDetailAsync(domain.Id, "203.0.113.5", 30, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(7, detail.Messages);
        Assert.Equal(
            detail.Messages,
            detail.Dispositions.None
                + detail.Dispositions.Pass
                + detail.Dispositions.Quarantine
                + detail.Dispositions.Reject);
    }
}
