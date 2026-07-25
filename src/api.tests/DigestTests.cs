using DmarcAnalyzer.Api.Application.Notifications;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class DigestTests
{
    private sealed class FakeEmailSender : IEmailSender
    {
        public List<(IReadOnlyCollection<string> To, string Subject, string Body)> Sent { get; } = [];
        public bool Deliver { get; init; } = true;
        public bool IsConfigured => Deliver;

        public Task<bool> SendAsync(IReadOnlyCollection<string> to, string subject, string body, CancellationToken ct)
        {
            Sent.Add((to, subject, body));
            return Task.FromResult(Deliver);
        }
    }

    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static DigestService Service(
        DmarcAnalyzerDbContext db, IEmailSender email, DigestOptions? options = null)
        => new(db, email,
            Options.Create(options ?? new DigestOptions()),
            Options.Create(new EmailOptions { BaseUrl = "https://dmarc.example.com" }),
            NullLogger<DigestService>.Instance);

    private static DateTime LastMonthStart()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
    }

    private static (Client, Domain) Seed(DmarcAnalyzerDbContext db, string slug = "acme")
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = slug, Slug = slug, Timezone = "UTC", RetentionMonths = 27,
            IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var domain = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = $"{slug}.example", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.AddRange(client, domain);
        return (client, domain);
    }

    private static void AddTraffic(
        DmarcAnalyzerDbContext db, Guid domainId, DateTime day, int messages, int compliant, string policy = "none")
    {
        var report = new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domainId, MailboxSourceId = Guid.NewGuid(),
            OrganizationName = "google.com", ReportId = Guid.NewGuid().ToString("N"),
            RangeBeginUtc = day, RangeEndUtc = day.AddHours(23), RecordCount = 2,
            IngestedAtUtc = DateTime.UtcNow, PublishedPolicy = policy, SubdomainPolicy = policy, PublishedPct = 100,
        };
        db.Add(report);
        if (compliant > 0)
        {
            db.Add(new DmarcReportRecord
            {
                ReportRangeBeginUtc = day,
                Id = Guid.NewGuid(), DmarcReportId = report.Id, SourceIp = "203.0.113.10",
                MessageCount = compliant, Disposition = "none", DkimResult = "pass", SpfResult = "pass",
                HeaderFrom = "x", EnvelopeFrom = "x", EnvelopeTo = "x",
            });
        }
        if (messages - compliant > 0)
        {
            db.Add(new DmarcReportRecord
            {
                ReportRangeBeginUtc = day,
                Id = Guid.NewGuid(), DmarcReportId = report.Id, SourceIp = "198.51.100.24",
                MessageCount = messages - compliant, Disposition = "none", DkimResult = "fail", SpfResult = "fail",
                HeaderFrom = "x", EnvelopeFrom = "x", EnvelopeTo = "x",
            });
        }
    }

    [Fact]
    public async Task BuildsASummaryForThePeriod()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        var start = LastMonthStart();
        AddTraffic(db, domain.Id, start.AddDays(5), messages: 1000, compliant: 900, policy: "reject");
        // Outside the period — must not be counted.
        AddTraffic(db, domain.Id, start.AddMonths(1).AddDays(2), messages: 500, compliant: 0);
        await db.SaveChangesAsync();

        var summary = await Service(db, new FakeEmailSender())
            .BuildAsync(client.Id, start, start.AddMonths(1), CancellationToken.None);

        Assert.NotNull(summary);
        Assert.Equal(1000, summary!.Messages);
        Assert.Equal(900, summary.CompliantMessages);
        Assert.Equal(0.9, summary.ComplianceRate);
        Assert.Equal(1, summary.Domains);
        Assert.Equal(1, summary.DomainsEnforcing);   // p=reject
        Assert.Equal(1, summary.FailingSources);
    }

    [Fact]
    public async Task ComparesAgainstThePrecedingPeriod()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        var start = LastMonthStart();
        AddTraffic(db, domain.Id, start.AddMonths(-1).AddDays(3), messages: 1000, compliant: 500);  // 50% before
        AddTraffic(db, domain.Id, start.AddDays(3), messages: 1000, compliant: 1000);               // 100% now
        await db.SaveChangesAsync();

        var summary = await Service(db, new FakeEmailSender())
            .BuildAsync(client.Id, start, start.AddMonths(1), CancellationToken.None);

        Assert.Equal(1.0, summary!.ComplianceRate);
        Assert.Equal(0.5, summary.PreviousComplianceRate);
        Assert.Contains("up 50.0 points", Service(db, new FakeEmailSender()).Render(summary));
    }

    [Fact]
    public async Task RendersAReadableBodyWithNoData()
    {
        await using var db = NewDb();
        var (client, _) = Seed(db);
        await db.SaveChangesAsync();
        var start = LastMonthStart();

        var service = Service(db, new FakeEmailSender());
        var summary = await service.BuildAsync(client.Id, start, start.AddMonths(1), CancellationToken.None);
        var body = service.Render(summary!);

        Assert.Contains("No DMARC reports covered this period", body);
        Assert.DoesNotContain("NaN", body);
    }

    [Fact]
    public async Task SendsToDigestRecipientsOnly_AndRecordsTheDelivery()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        db.AddRange(
            new NotificationRecipient { ClientId = client.Id, Email = "cfo@acme.example", Kind = "digest" },
            new NotificationRecipient { ClientId = null, Email = "agency@example.com", Kind = "both" },
            // alert-only must not receive the digest
            new NotificationRecipient { ClientId = client.Id, Email = "pager@acme.example", Kind = "alert" });
        AddTraffic(db, domain.Id, LastMonthStart().AddDays(4), messages: 100, compliant: 90);
        await db.SaveChangesAsync();

        var email = new FakeEmailSender();
        var result = await Service(db, email, new DigestOptions { DayOfMonth = 1 })
            .SendDueAsync(CancellationToken.None);

        Assert.Equal(1, result.Sent);
        var sent = Assert.Single(email.Sent);
        Assert.Contains("cfo@acme.example", sent.To);
        Assert.Contains("agency@example.com", sent.To);
        Assert.DoesNotContain("pager@acme.example", sent.To);
        var delivery = Assert.Single(await db.DigestDeliveries.ToListAsync());
        Assert.Equal(2, delivery.RecipientCount);
    }

    [Fact]
    public async Task DoesNotSendTheSamePeriodTwice()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        db.Add(new NotificationRecipient { ClientId = client.Id, Email = "cfo@acme.example", Kind = "digest" });
        AddTraffic(db, domain.Id, LastMonthStart().AddDays(4), messages: 100, compliant: 90);
        await db.SaveChangesAsync();

        var email = new FakeEmailSender();
        var service = Service(db, email, new DigestOptions { DayOfMonth = 1 });
        var first = await service.SendDueAsync(CancellationToken.None);
        var second = await service.SendDueAsync(CancellationToken.None);

        Assert.Equal(1, first.Sent);
        Assert.Equal(0, second.Sent);
        Assert.Equal(1, second.Skipped);
        Assert.Single(email.Sent);
        Assert.Single(await db.DigestDeliveries.ToListAsync());
    }

    [Fact]
    public async Task ABrokenRelayStillMarksThePeriod_SoItDoesNotRetryForever()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        db.Add(new NotificationRecipient { ClientId = client.Id, Email = "cfo@acme.example", Kind = "digest" });
        AddTraffic(db, domain.Id, LastMonthStart().AddDays(4), messages: 100, compliant: 90);
        await db.SaveChangesAsync();

        var email = new FakeEmailSender { Deliver = false };
        var result = await Service(db, email, new DigestOptions { DayOfMonth = 1 })
            .SendDueAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
        var delivery = Assert.Single(await db.DigestDeliveries.ToListAsync());
        Assert.Equal(0, delivery.RecipientCount);   // attempted, nothing delivered
    }

    [Fact]
    public async Task ClientsWithNoDigestRecipientsAreSkipped()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        AddTraffic(db, domain.Id, LastMonthStart().AddDays(4), messages: 100, compliant: 90);
        await db.SaveChangesAsync();

        var email = new FakeEmailSender();
        var result = await Service(db, email, new DigestOptions { DayOfMonth = 1 })
            .SendDueAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.Skipped);
        Assert.Empty(email.Sent);
        Assert.Empty(await db.DigestDeliveries.ToListAsync());
    }

    [Fact]
    public async Task DisabledSendsNothing()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        db.Add(new NotificationRecipient { ClientId = client.Id, Email = "cfo@acme.example", Kind = "digest" });
        AddTraffic(db, domain.Id, LastMonthStart().AddDays(4), messages: 100, compliant: 90);
        await db.SaveChangesAsync();

        var email = new FakeEmailSender();
        var result = await Service(db, email, new DigestOptions { Enabled = false })
            .SendDueAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task WaitsUntilTheConfiguredDayOfMonth()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        db.Add(new NotificationRecipient { ClientId = client.Id, Email = "cfo@acme.example", Kind = "digest" });
        AddTraffic(db, domain.Id, LastMonthStart().AddDays(4), messages: 100, compliant: 90);
        await db.SaveChangesAsync();

        // A day-of-month later than today must hold the digest back.
        var future = Math.Min(28, DateTime.UtcNow.Day + 1);
        var email = new FakeEmailSender();
        var result = await Service(db, email, new DigestOptions { DayOfMonth = future })
            .SendDueAsync(CancellationToken.None);

        if (DateTime.UtcNow.Day < future)
        {
            Assert.Equal(0, result.Sent);
            Assert.Empty(email.Sent);
        }
    }
}
