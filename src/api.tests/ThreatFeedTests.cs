using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class ThreatFeedTests
{
    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static (Client Client, Domain Domain) NewClientWithDomain(string slug, string domainName)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = slug, Slug = slug, Timezone = "UTC",
            RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var domain = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = domainName, IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        return (client, domain);
    }

    // Adds a report for the domain with records (ip, count, dkim, spf).
    private static void AddReport(
        DmarcAnalyzerDbContext db, Guid domainId, params (string Ip, int Count, string Dkim, string Spf)[] records)
    {
        var report = new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domainId, MailboxSourceId = Guid.NewGuid(),
            OrganizationName = "google.com", ReportId = Guid.NewGuid().ToString("N"),
            RangeBeginUtc = DateTime.UtcNow.AddDays(-2), RangeEndUtc = DateTime.UtcNow.AddDays(-1),
            RecordCount = records.Length, IngestedAtUtc = DateTime.UtcNow,
            PublishedPolicy = "none", SubdomainPolicy = "none", PublishedPct = 100,
        };
        db.Add(report);
        foreach (var r in records)
        {
            db.Add(new DmarcReportRecord
            {
                Id = Guid.NewGuid(), DmarcReportId = report.Id, SourceIp = r.Ip, MessageCount = r.Count,
                Disposition = "none", DkimResult = r.Dkim, SpfResult = r.Spf,
            });
        }
    }

    [Fact]
    public async Task ReturnsOnlyFullyUnauthenticatedSources_WorstFirst()
    {
        await using var db = NewDb();
        var (client, domain) = NewClientWithDomain("acme", "acme.example");
        db.AddRange(client, domain);
        AddReport(db, domain.Id,
            ("203.0.113.10", 900, "pass", "fail"),   // compliant (DKIM aligned) — not a threat
            ("198.51.100.24", 50, "fail", "fail"),   // threat
            ("192.0.2.77", 300, "fail", "fail"));    // bigger threat
        await db.SaveChangesAsync();

        var feed = await new AnalyticsQueryService(db, TestCurrentUserContext.Admin())
            .GetThreatFeedAsync(30, 100, CancellationToken.None);

        Assert.Equal(2, feed.TotalSources);
        Assert.Equal(350, feed.TotalFailedMessages);
        Assert.Equal(2, feed.Sources.Count);
        Assert.Equal("192.0.2.77", feed.Sources[0].SourceIp); // worst first
        Assert.Equal(300, feed.Sources[0].FailedMessages);
        Assert.Equal("acme.example", feed.Sources[0].Domain);
        Assert.Equal("none", feed.Sources[0].PublishedPolicy);
        Assert.DoesNotContain(feed.Sources, s => s.SourceIp == "203.0.113.10");
    }

    [Fact]
    public async Task MixedSource_CountsOnlyUnauthenticatedVolumeAsFailed()
    {
        await using var db = NewDb();
        var (client, domain) = NewClientWithDomain("acme", "acme.example");
        db.AddRange(client, domain);
        AddReport(db, domain.Id,
            ("203.0.113.10", 800, "pass", "pass"),
            ("203.0.113.10", 200, "fail", "fail")); // same ip, partly failing
        await db.SaveChangesAsync();

        var feed = await new AnalyticsQueryService(db, TestCurrentUserContext.Admin())
            .GetThreatFeedAsync(30, 100, CancellationToken.None);

        var source = Assert.Single(feed.Sources);
        Assert.Equal(1000, source.Messages);
        Assert.Equal(200, source.FailedMessages);
        Assert.Equal(0.8, source.ComplianceRate);
    }

    [Fact]
    public async Task Viewer_SeesOnlyGrantedClientsThreats()
    {
        await using var db = NewDb();
        var (granted, grantedDomain) = NewClientWithDomain("granted", "granted.example");
        var (other, otherDomain) = NewClientWithDomain("other", "other.example");
        db.AddRange(granted, grantedDomain, other, otherDomain);
        AddReport(db, grantedDomain.Id, ("198.51.100.24", 10, "fail", "fail"));
        AddReport(db, otherDomain.Id, ("192.0.2.99", 999, "fail", "fail"));
        await db.SaveChangesAsync();

        var feed = await new AnalyticsQueryService(db, TestCurrentUserContext.Viewer(granted.Id))
            .GetThreatFeedAsync(30, 100, CancellationToken.None);

        var source = Assert.Single(feed.Sources);
        Assert.Equal("granted.example", source.Domain);
        Assert.Equal(1, feed.TotalSources);
        Assert.Equal(10, feed.TotalFailedMessages);
    }
}
