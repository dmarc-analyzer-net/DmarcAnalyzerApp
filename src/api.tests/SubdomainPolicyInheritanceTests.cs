using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// End to end for subdomain policy inheritance: DNS -> the cached domain row -> what the
/// Domains list shows. The unit tests on the resolver prove the walk; these prove it is
/// actually wired to the screen, which is where the wrong answer was visible.
/// </summary>
public sealed class SubdomainPolicyInheritanceTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    /// <summary>Seeds one client with the given domains, and a report so the row is not no_data.</summary>
    private static async Task<Dictionary<string, Guid>> SeedAsync(
        DmarcAnalyzerDbContext db, params string[] names)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC",
            RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Add(client);

        var ids = new Dictionary<string, Guid>();
        foreach (var name in names)
        {
            var domain = new Domain
            {
                Id = Guid.NewGuid(), ClientId = client.Id, Name = name, IsActive = true,
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            };
            ids[name] = domain.Id;
            db.Add(domain);

            var report = new DmarcReport
            {
                Id = Guid.NewGuid(), DomainId = domain.Id, MailboxSourceId = Guid.NewGuid(),
                OrganizationName = "google.com", ReportId = Guid.NewGuid().ToString("N"),
                RangeBeginUtc = DateTime.UtcNow.AddDays(-2), RangeEndUtc = DateTime.UtcNow.AddDays(-1),
                RecordCount = 1, IngestedAtUtc = DateTime.UtcNow,
                PublishedPolicy = "none", SubdomainPolicy = "none", PublishedPct = 100,
            };
            db.Add(report);
            db.Add(new DmarcReportRecord
            {
                Id = Guid.NewGuid(), DmarcReportId = report.Id,
                ReportRangeBeginUtc = report.RangeBeginUtc,
                SourceIp = "192.0.2.1", MessageCount = 10,
                Disposition = "none", DkimResult = "pass", SpfResult = "pass",
            });
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private static DnsPolicyCache Cache(DmarcAnalyzerDbContext db, IDnsTxtResolver dns)
        => new(db, new DmarcPolicyResolver(dns), NullLogger<DnsPolicyCache>.Instance);

    /// <summary>
    /// The bug this change exists for. A subdomain publishing nothing, under a parent at
    /// p=reject, used to render as "—" with an enforcement status of unprotected. Receivers
    /// were rejecting for it the whole time.
    /// </summary>
    [Fact]
    public async Task ASubdomainWithNoRecordShowsTheInheritedPolicyAndItsSource()
    {
        await using var db = NewDb();
        await SeedAsync(db, "acme.example", "client.acme.example");
        var dns = new TestDnsTxtResolver().Publish("_dmarc.acme.example", "v=DMARC1; p=reject");

        await Cache(db, dns).RefreshAllAsync(CancellationToken.None);

        var rows = await TestAnalytics.Service(db, TestCurrentUserContext.Admin(), dns)
            .ListDomainAnalyticsAsync(30, CancellationToken.None);

        var child = Assert.Single(rows, x => x.Name == "client.acme.example");
        Assert.Equal("reject", child.PublishedPolicy);
        Assert.Equal(RecordLookupStatus.Inherited, child.DnsLookupStatus);
        Assert.Equal("acme.example", child.DnsPolicyInheritedFrom);

        // And the consequence that matters: it counts as enforced, not as unprotected.
        Assert.Equal(EnforcementStatus.Enforced, child.EnforcementStatus);
    }

    [Fact]
    public async Task AnOwnRecordIsNotReportedAsInherited()
    {
        await using var db = NewDb();
        await SeedAsync(db, "acme.example", "gitlab.acme.example");
        var dns = new TestDnsTxtResolver()
            .Publish("_dmarc.acme.example", "v=DMARC1; p=reject")
            .Publish("_dmarc.gitlab.acme.example", "v=DMARC1; p=none");

        await Cache(db, dns).RefreshAllAsync(CancellationToken.None);

        var rows = await TestAnalytics.Service(db, TestCurrentUserContext.Admin(), dns)
            .ListDomainAnalyticsAsync(30, CancellationToken.None);

        // A subdomain that deliberately opts out of its parent's enforcement must be shown
        // as the p=none it published, not as the parent's reject.
        var child = Assert.Single(rows, x => x.Name == "gitlab.acme.example");
        Assert.Equal("none", child.PublishedPolicy);
        Assert.Equal(RecordLookupStatus.Found, child.DnsLookupStatus);
        Assert.Null(child.DnsPolicyInheritedFrom);
    }

    [Fact]
    public async Task WithNothingPublishedAnywhereTheRowStillReportsMissing()
    {
        await using var db = NewDb();
        await SeedAsync(db, "client.acme.example");

        await Cache(db, TestDnsTxtResolver.Empty()).RefreshAllAsync(CancellationToken.None);

        var rows = await TestAnalytics.Service(db, TestCurrentUserContext.Admin(), TestDnsTxtResolver.Empty())
            .ListDomainAnalyticsAsync(30, CancellationToken.None);

        var child = Assert.Single(rows);
        Assert.Null(child.PublishedPolicy);
        Assert.Equal(RecordLookupStatus.Missing, child.DnsLookupStatus);
        Assert.Null(child.DnsPolicyInheritedFrom);
    }

    /// <summary>
    /// The parent need not be a monitored domain. In the instance that surfaced this, 39 of 44
    /// subdomain-shaped domains had no monitored parent, because reports only ever arrive for
    /// the sending subdomain — so resolving through the domain table instead of DNS would have
    /// missed almost all of them.
    /// </summary>
    [Fact]
    public async Task InheritanceDoesNotRequireTheParentToBeMonitored()
    {
        await using var db = NewDb();
        await SeedAsync(db, "email.acme.example");
        var dns = new TestDnsTxtResolver()
            .Publish("_dmarc.acme.example", "v=DMARC1; p=reject; sp=quarantine");

        await Cache(db, dns).RefreshAllAsync(CancellationToken.None);

        var rows = await TestAnalytics.Service(db, TestCurrentUserContext.Admin(), dns)
            .ListDomainAnalyticsAsync(30, CancellationToken.None);

        var child = Assert.Single(rows);
        Assert.Equal("quarantine", child.PublishedPolicy);   // sp=, not p=
        Assert.Equal("acme.example", child.DnsPolicyInheritedFrom);
        Assert.DoesNotContain(rows, x => x.Name == "acme.example");
    }

    /// <summary>
    /// A transient SERVFAIL must not blank an inherited policy, for the same reason it must not
    /// blank an own one: a p=reject domain briefly looking unprotected is the worse failure.
    /// </summary>
    [Fact]
    public async Task AFailedLookupKeepsTheInheritedPolicyAndSource()
    {
        await using var db = NewDb();
        var ids = await SeedAsync(db, "client.acme.example");
        var dns = new TestDnsTxtResolver().Publish("_dmarc.acme.example", "v=DMARC1; p=reject");
        await Cache(db, dns).RefreshAllAsync(CancellationToken.None);

        await Cache(db, new TestDnsTxtResolver().FailFor("_dmarc.client.acme.example"))
            .RefreshAllAsync(CancellationToken.None);

        var stored = await db.Domains.SingleAsync(x => x.Id == ids["client.acme.example"]);
        Assert.Equal("reject", stored.DnsPolicy);
        Assert.Equal("acme.example", stored.DnsPolicyInheritedFrom);
        Assert.Equal(RecordLookupStatus.LookupFailed, stored.DnsLookupStatus);
    }
}
