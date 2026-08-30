using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The TLS-RPT summary: tenant scoping (404 semantics), aggregation, the
/// TLS-own window anchor, and the gate sample's category filter.
/// </summary>
public sealed class TlsRptQueryServiceTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    /// <summary>
    /// The real record checker over stubbed DNS — the summary carries a live
    /// `_smtp._tls` lookup, and the aggregation tests should not care.
    /// </summary>
    private static TlsRptQueryService NewService(
        DmarcAnalyzerDbContext db, ICurrentUserContext user, IDnsTxtResolver? dns = null)
        => new(db, user, new TlsRptRecordChecker(dns ?? TestDnsTxtResolver.Empty()));

    private static (Client Client, Domain Domain) Seed(DmarcAnalyzerDbContext db, string slug)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = slug, Slug = slug, Timezone = "UTC" };
        var domain = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = $"{slug}.example", IsActive = true };
        db.AddRange(client, domain);
        return (client, domain);
    }

    /// <summary>A report + one policy for the domain, ended <paramref name="daysAgo"/> days ago.</summary>
    private static SmtpTlsReportPolicy AddPolicy(
        DmarcAnalyzerDbContext db, Guid domainId, int daysAgo,
        long ok = 100, long fail = 0, string reporter = "reporter.example", string policyType = "sts")
    {
        var end = DateTime.UtcNow.AddDays(-daysAgo);
        var report = new SmtpTlsReport
        {
            Id = Guid.NewGuid(), ReportSourceId = Guid.NewGuid(),
            OrganizationName = reporter, ReportId = Guid.NewGuid().ToString("N"),
            RangeBeginUtc = end.AddDays(-1), RangeEndUtc = end, PolicyCount = 1,
        };
        var policy = new SmtpTlsReportPolicy
        {
            Id = Guid.NewGuid(), SmtpTlsReportId = report.Id, DomainId = domainId,
            PolicyType = policyType, PolicyDomain = "x.example",
            SuccessfulSessionCount = ok, FailureSessionCount = fail,
            ReportRangeBeginUtc = report.RangeBeginUtc, ReportRangeEndUtc = report.RangeEndUtc,
        };
        db.AddRange(report, policy);
        return policy;
    }

    private static void AddDetail(
        DmarcAnalyzerDbContext db, Guid policyId, string resultType, string category,
        long sessions, string? mx = "mx1.x.example")
        => db.Add(new SmtpTlsFailureDetail
        {
            Id = Guid.NewGuid(), SmtpTlsReportPolicyId = policyId,
            ResultType = resultType, FailureCategory = category,
            ReceivingMxHostname = mx, FailedSessionCount = sessions,
        });

    [Fact]
    public async Task CrossTenant_ReadsAsNotFound()
    {
        await using var db = NewDb();
        var (granted, grantedDomain) = Seed(db, "granted");
        var (_, otherDomain) = Seed(db, "other");
        AddPolicy(db, grantedDomain.Id, 1);
        AddPolicy(db, otherDomain.Id, 1);
        await db.SaveChangesAsync();

        var viewer = NewService(db, TestCurrentUserContext.Viewer(granted.Id));

        Assert.Null(await viewer.GetDomainSummaryAsync(otherDomain.Id, 30, CancellationToken.None));
        Assert.NotNull(await viewer.GetDomainSummaryAsync(grantedDomain.Id, 30, CancellationToken.None));
    }

    [Fact]
    public async Task Summary_AggregatesSessionsCategoriesAndMx()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db, "acme");
        var p1 = AddPolicy(db, domain.Id, 1, ok: 100, fail: 6, reporter: "a.example");
        var p2 = AddPolicy(db, domain.Id, 2, ok: 50, fail: 1, reporter: "b.example", policyType: "no-policy-found");
        AddDetail(db, p1.Id, "sts-policy-invalid", "sts", 4, mx: "mx1.acme.example");
        AddDetail(db, p1.Id, "certificate-expired", "transport", 2, mx: "mx1.acme.example");
        AddDetail(db, p2.Id, "starttls-not-supported", "transport", 1, mx: "mx2.acme.example");
        await db.SaveChangesAsync();

        var summary = (await NewService(db, TestCurrentUserContext.Admin())
            .GetDomainSummaryAsync(domain.Id, 30, CancellationToken.None))!;

        Assert.Equal(157, summary.TotalSessions);
        Assert.Equal(150, summary.SuccessfulSessions);
        Assert.Equal(7, summary.FailedSessions);
        Assert.Equal(2, summary.ReportCount);
        Assert.Equal(2, summary.ReporterCount);

        Assert.Equal(["sts", "no-policy-found"], summary.ByPolicyType.Select(x => x.PolicyType).ToArray());
        var sts = Assert.Single(summary.FailuresByCategory, c => c.Category == "sts");
        Assert.Equal(4, sts.FailedSessions);
        var transport = Assert.Single(summary.FailuresByCategory, c => c.Category == "transport");
        Assert.Equal(3, transport.FailedSessions);

        var mx1 = Assert.Single(summary.ByReceivingMx, m => m.ReceivingMxHostname == "mx1.acme.example");
        Assert.Equal(6, mx1.FailedSessions);
        Assert.Equal(["certificate-expired", "sts-policy-invalid"], mx1.ResultTypes);
    }

    [Fact]
    public async Task Window_AnchorsToTheDomainOwnersVisibleTlsData()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db, "acme");
        // The newest TLS data is 100 days old — a wall-clock 30-day window would
        // be empty; the anchored one shows the data that exists.
        AddPolicy(db, domain.Id, 100, ok: 10);
        AddPolicy(db, domain.Id, 110, ok: 20);
        await db.SaveChangesAsync();

        var summary = (await NewService(db, TestCurrentUserContext.Admin())
            .GetDomainSummaryAsync(domain.Id, 30, CancellationToken.None))!;

        Assert.True(summary.Window.AnchoredToLatestData);
        Assert.Equal(30, summary.TotalSessions);
    }

    /// <summary>
    /// Zero sessions means two opposite things depending on this record, so the
    /// summary has to carry it: published means nobody answered, missing means
    /// nobody was asked.
    /// </summary>
    [Fact]
    public async Task Summary_CarriesTheLiveTlsRptRecord()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db, "acme");
        await db.SaveChangesAsync();

        var dns = TestDnsTxtResolver.Empty()
            .Publish($"_smtp._tls.{domain.Name}", "v=TLSRPTv1;rua=mailto:tls@acme.example");

        var summary = (await NewService(db, TestCurrentUserContext.Admin(), dns)
            .GetDomainSummaryAsync(domain.Id, 30, CancellationToken.None))!;

        Assert.Equal(0, summary.TotalSessions);
        Assert.Equal(TlsRptRecordStatus.Found, summary.Record.Status);
        Assert.Equal(["mailto:tls@acme.example"], summary.Record.Rua);
    }

    [Fact]
    public async Task Summary_ReportsAMissingTlsRptRecord()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db, "acme");
        AddPolicy(db, domain.Id, 1);
        await db.SaveChangesAsync();

        var summary = (await NewService(db, TestCurrentUserContext.Admin())
            .GetDomainSummaryAsync(domain.Id, 30, CancellationToken.None))!;

        Assert.Equal(TlsRptRecordStatus.Missing, summary.Record.Status);
        Assert.Null(summary.Record.Raw);
    }

    [Fact]
    public async Task GateSample_CountsOnlyStsCategory_SinceWallClock()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db, "acme");
        var recent = AddPolicy(db, domain.Id, 2, ok: 100, fail: 9);
        AddDetail(db, recent.Id, "sts-policy-fetch-error", "sts", 3);
        AddDetail(db, recent.Id, "certificate-expired", "transport", 6);
        var stale = AddPolicy(db, domain.Id, 40, ok: 5, fail: 5);
        AddDetail(db, stale.Id, "sts-policy-invalid", "sts", 5);
        await db.SaveChangesAsync();

        var sample = await NewService(db, TestCurrentUserContext.Admin())
            .GetGateSampleAsync(domain.Id, DateTime.UtcNow.AddDays(-14), CancellationToken.None);

        Assert.Equal(109, sample.TotalSessions);   // the stale policy is outside the gate window
        Assert.Equal(3, sample.StsFailureSessions); // transport never counts
        Assert.Equal(1, sample.ReportCount);
    }
}
