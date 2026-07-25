using DmarcAnalyzer.Api.Application.Retention;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class RetentionPurgeTests
{
    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static RetentionPurgeService Service(DmarcAnalyzerDbContext db, int auditRetentionDays = 730)
        => new(db,
            Options.Create(new RetentionOptions { AuditRetentionDays = auditRetentionDays }),
            NullLogger<RetentionPurgeService>.Instance);

    private static Client NewClient(string slug, int retentionMonths = 27, bool legalHold = false) => new()
    {
        Id = Guid.NewGuid(), Name = slug, Slug = slug, Timezone = "UTC",
        RetentionMonths = retentionMonths, LegalHold = legalHold, IsActive = true,
        CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
    };

    private static Domain NewDomain(Guid clientId, string name) => new()
    {
        Id = Guid.NewGuid(), ClientId = clientId, Name = name, IsActive = true,
        CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
    };

    /// <summary>A report whose reporting window ended <paramref name="monthsAgo"/> months ago.</summary>
    private static DmarcReport NewReport(Guid domainId, int monthsAgo)
    {
        var end = DateTime.UtcNow.AddMonths(-monthsAgo);
        return new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domainId, MailboxSourceId = Guid.NewGuid(),
            OrganizationName = "google.com", ReportId = Guid.NewGuid().ToString("N"),
            RangeBeginUtc = end.AddDays(-1), RangeEndUtc = end,
            RecordCount = 1,
            // Ingested recently on purpose: retention must key off the reporting
            // window, not when a backfill happened to deliver the file.
            IngestedAtUtc = DateTime.UtcNow,
            PublishedPolicy = "none", SubdomainPolicy = "none", PublishedPct = 100,
        };
    }

    [Fact]
    public async Task DeletesReportsPastRetention_AndKeepsRecentOnes()
    {
        await using var db = NewDb();
        var client = NewClient("acme", retentionMonths: 27);
        var domain = NewDomain(client.Id, "acme.example");
        db.AddRange(client, domain);
        db.AddRange(
            NewReport(domain.Id, 30),   // expired
            NewReport(domain.Id, 40),   // expired
            NewReport(domain.Id, 26),   // inside the window
            NewReport(domain.Id, 1));   // recent
        await db.SaveChangesAsync();

        var result = await Service(db).PurgeAsync(dryRun: false, 500, CancellationToken.None);

        Assert.Equal(2, result.ReportsDeleted);
        Assert.Equal(2, await db.DmarcReports.CountAsync());
        Assert.All(await db.DmarcReports.ToListAsync(),
            r => Assert.True(r.RangeEndUtc > DateTime.UtcNow.AddMonths(-27)));
    }

    [Fact]
    public async Task LegalHold_PreventsAnyDeletion()
    {
        await using var db = NewDb();
        var client = NewClient("held", retentionMonths: 12, legalHold: true);
        var domain = NewDomain(client.Id, "held.example");
        db.AddRange(client, domain);
        db.AddRange(NewReport(domain.Id, 60), NewReport(domain.Id, 90));
        await db.SaveChangesAsync();

        var result = await Service(db).PurgeAsync(dryRun: false, 500, CancellationToken.None);

        Assert.Equal(0, result.ReportsDeleted);
        Assert.Equal(1, result.ClientsOnLegalHold);
        Assert.True(Assert.Single(result.PerClient).SkippedForLegalHold);
        Assert.Equal(2, await db.DmarcReports.CountAsync());
    }

    [Fact]
    public async Task RetentionIsPerClient()
    {
        await using var db = NewDb();
        var shortLived = NewClient("short", retentionMonths: 6);
        var longLived = NewClient("long", retentionMonths: 60);
        var d1 = NewDomain(shortLived.Id, "short.example");
        var d2 = NewDomain(longLived.Id, "long.example");
        db.AddRange(shortLived, longLived, d1, d2);
        // Same age, different fate.
        db.AddRange(NewReport(d1.Id, 12), NewReport(d2.Id, 12));
        await db.SaveChangesAsync();

        await Service(db).PurgeAsync(dryRun: false, 500, CancellationToken.None);

        Assert.Empty(await db.DmarcReports.Where(r => r.DomainId == d1.Id).ToListAsync());
        Assert.Single(await db.DmarcReports.Where(r => r.DomainId == d2.Id).ToListAsync());
    }

    [Fact]
    public async Task DryRun_CountsWithoutDeleting()
    {
        await using var db = NewDb();
        var client = NewClient("acme");
        var domain = NewDomain(client.Id, "acme.example");
        db.AddRange(client, domain);
        db.AddRange(NewReport(domain.Id, 30), NewReport(domain.Id, 30), NewReport(domain.Id, 2));
        await db.SaveChangesAsync();

        var result = await Service(db).PurgeAsync(dryRun: true, 500, CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Equal(2, result.ReportsDeleted);           // what *would* go
        Assert.Equal(3, await db.DmarcReports.CountAsync()); // nothing actually did
    }

    [Fact]
    public async Task NonPositiveRetention_FallsBackToTheDefault_RatherThanDeletingEverything()
    {
        await using var db = NewDb();
        // A misconfigured 0 must not be read as "keep nothing".
        var client = NewClient("zero", retentionMonths: 0);
        var domain = NewDomain(client.Id, "zero.example");
        db.AddRange(client, domain);
        db.AddRange(NewReport(domain.Id, 1), NewReport(domain.Id, 40));
        await db.SaveChangesAsync();

        var result = await Service(db).PurgeAsync(dryRun: false, 500, CancellationToken.None);

        Assert.Equal(27, Assert.Single(result.PerClient).RetentionMonths);
        Assert.Equal(1, result.ReportsDeleted);
        Assert.Single(await db.DmarcReports.ToListAsync());
    }

    [Fact]
    public async Task OnlyTouchesTheOwningClientsData()
    {
        await using var db = NewDb();
        var a = NewClient("a", retentionMonths: 6);
        var b = NewClient("b", retentionMonths: 6, legalHold: true);
        var da = NewDomain(a.Id, "a.example");
        var dbb = NewDomain(b.Id, "b.example");
        db.AddRange(a, b, da, dbb);
        db.AddRange(NewReport(da.Id, 24), NewReport(dbb.Id, 24));
        await db.SaveChangesAsync();

        await Service(db).PurgeAsync(dryRun: false, 500, CancellationToken.None);

        Assert.Empty(await db.DmarcReports.Where(r => r.DomainId == da.Id).ToListAsync());
        Assert.Single(await db.DmarcReports.Where(r => r.DomainId == dbb.Id).ToListAsync());
    }

    [Fact]
    public async Task PurgesTheIngestLedgerToo()
    {
        await using var db = NewDb();
        var client = NewClient("acme", retentionMonths: 12);
        db.Add(client);
        var old = DateTime.UtcNow.AddMonths(-24);
        var recent = DateTime.UtcNow.AddMonths(-1);
        foreach (var end in new[] { old, recent })
        {
            db.Add(new DmarcReportIngest
            {
                Id = Guid.NewGuid(), ClientId = client.Id, MailboxSourceId = Guid.NewGuid(),
                PolicyDomain = "acme.example", ReportId = Guid.NewGuid().ToString("N"),
                ReportRangeBeginUtc = end.AddDays(-1), ReportRangeEndUtc = end,
                OrganizationName = "google.com", RecordCount = 1, IngestedAtUtc = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        var result = await Service(db).PurgeAsync(dryRun: false, 500, CancellationToken.None);

        Assert.Equal(1, result.IngestRowsDeleted);
        Assert.Single(await db.DmarcReportIngests.ToListAsync());
    }

    [Fact]
    public async Task BatchingDeletesEverythingExpired()
    {
        await using var db = NewDb();
        var client = NewClient("bulk", retentionMonths: 12);
        var domain = NewDomain(client.Id, "bulk.example");
        db.AddRange(client, domain);
        for (var i = 0; i < 25; i++)
        {
            db.Add(NewReport(domain.Id, 24));
        }
        await db.SaveChangesAsync();

        // Batch size well below the row count, so the loop must iterate.
        var result = await Service(db).PurgeAsync(dryRun: false, 4, CancellationToken.None);

        Assert.Equal(25, result.ReportsDeleted);
        Assert.Empty(await db.DmarcReports.ToListAsync());
    }

    [Fact]
    public async Task PurgesAuditEventsOnTheirOwnLongerWindow()
    {
        await using var db = NewDb();
        var (client, _) = (NewClient("acme"), (Domain?)null);
        db.Add(client);
        db.AddRange(
            new AuditEvent
            {
                Id = Guid.NewGuid(), ActorType = "user", ActorEmail = "a@b.c", EventType = "auth.login.succeeded",
                Summary = "old", OccurredAtUtc = DateTime.UtcNow.AddDays(-800),
            },
            new AuditEvent
            {
                Id = Guid.NewGuid(), ActorType = "user", ActorEmail = "a@b.c", EventType = "auth.login.succeeded",
                Summary = "recent", OccurredAtUtc = DateTime.UtcNow.AddDays(-10),
            });
        await db.SaveChangesAsync();

        var result = await Service(db).PurgeAsync(dryRun: false, 500, CancellationToken.None);

        Assert.Equal(1, result.AuditEventsDeleted);
        Assert.Equal("recent", Assert.Single(await db.AuditEvents.ToListAsync()).Summary);
    }

    [Fact]
    public async Task AuditRetentionOfZeroKeepsTheTrailForever()
    {
        await using var db = NewDb();
        db.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), ActorType = "user", ActorEmail = "a@b.c", EventType = "auth.login.succeeded",
            Summary = "ancient", OccurredAtUtc = DateTime.UtcNow.AddDays(-5000),
        });
        await db.SaveChangesAsync();

        var result = await Service(db, auditRetentionDays: 0)
            .PurgeAsync(dryRun: false, 500, CancellationToken.None);

        Assert.Equal(0, result.AuditEventsDeleted);
        Assert.Single(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task LegalHoldDoesNotProtectTheAuditTrail()
    {
        await using var db = NewDb();
        // The trail spans the whole install, so one client's hold must not pin it.
        db.Add(NewClient("held", legalHold: true));
        db.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), ActorType = "user", ActorEmail = "a@b.c", EventType = "client.updated",
            Summary = "old", OccurredAtUtc = DateTime.UtcNow.AddDays(-900),
        });
        await db.SaveChangesAsync();

        var result = await Service(db).PurgeAsync(dryRun: false, 500, CancellationToken.None);

        Assert.Equal(1, result.AuditEventsDeleted);
    }
}
