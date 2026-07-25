using DmarcAnalyzer.Api.Application.Notifications;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class AlertEvaluationTests
{
    /// <summary>Captures what would have been emailed instead of sending it.</summary>
    private sealed class FakeEmailSender : IEmailSender
    {
        public List<(IReadOnlyCollection<string> To, string Subject, string Body)> Sent { get; } = [];
        public bool Configured { get; init; } = true;
        public bool IsConfigured => Configured;

        public Task<bool> SendAsync(
            IReadOnlyCollection<string> to, string subject, string body, CancellationToken ct)
        {
            Sent.Add((to, subject, body));
            return Task.FromResult(Configured);
        }
    }

    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static AlertEvaluationService Service(
        DmarcAnalyzerDbContext db, IEmailSender email, AlertOptions? options = null)
        => new(db, email,
            Options.Create(options ?? new AlertOptions()),
            Options.Create(new EmailOptions { BaseUrl = "https://dmarc.example.com" }),
            NullLogger<AlertEvaluationService>.Instance);

    private static (Client, Domain) Seed(DmarcAnalyzerDbContext db, string slug = "acme")
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = slug, Slug = slug, Timezone = "UTC", RetentionMonths = 27,
            IsActive = true, AlertsEnabled = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        var domain = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = $"{slug}.example", IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.AddRange(client, domain);
        return (client, domain);
    }

    /// <summary>One day of traffic: <paramref name="messages"/> total, of which <paramref name="compliant"/> pass.</summary>
    private static void AddDay(
        DmarcAnalyzerDbContext db, Guid domainId, int daysAgo, int messages, int compliant, string policy = "none")
    {
        var day = DateTime.UtcNow.Date.AddDays(-daysAgo);
        var report = new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domainId, MailboxSourceId = Guid.NewGuid(),
            OrganizationName = "google.com", ReportId = Guid.NewGuid().ToString("N"),
            RangeBeginUtc = day, RangeEndUtc = day.AddHours(23), RecordCount = 2,
            IngestedAtUtc = DateTime.UtcNow, PublishedPolicy = policy, SubdomainPolicy = policy,
            PublishedPct = 100,
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
        var failing = messages - compliant;
        if (failing > 0)
        {
            db.Add(new DmarcReportRecord
            {
                ReportRangeBeginUtc = day,
                Id = Guid.NewGuid(), DmarcReportId = report.Id, SourceIp = "198.51.100.24",
                MessageCount = failing, Disposition = "none", DkimResult = "fail", SpfResult = "fail",
                HeaderFrom = "x", EnvelopeFrom = "x", EnvelopeTo = "x",
            });
        }
    }

    [Fact]
    public async Task RaisesFailureSpike_WhenComplianceDropsBelowBaseline()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        for (var d = 7; d >= 1; d--)
        {
            AddDay(db, domain.Id, d, messages: 1000, compliant: 990);   // ~99% baseline
        }
        AddDay(db, domain.Id, 0, messages: 1000, compliant: 500);       // 50% today
        await db.SaveChangesAsync();

        var email = new FakeEmailSender();
        var result = await Service(db, email).EvaluateAsync(CancellationToken.None);

        Assert.Equal(1, result.AlertsRaised);
        var alert = Assert.Single(await db.AlertEvents.ToListAsync());
        Assert.Equal(AlertRuleTypes.FailureSpike, alert.RuleType);
        Assert.Equal("critical", alert.Severity);   // ~49 points, well over 2x the 15 threshold
        Assert.Contains("acme.example", alert.Title);
    }

    [Fact]
    public async Task IgnoresLowVolumeDays()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        for (var d = 7; d >= 1; d--)
        {
            AddDay(db, domain.Id, d, messages: 1000, compliant: 1000);
        }
        // A total collapse, but on only 5 messages — noise, not a spike.
        AddDay(db, domain.Id, 0, messages: 5, compliant: 0);
        await db.SaveChangesAsync();

        var result = await Service(db, new FakeEmailSender()).EvaluateAsync(CancellationToken.None);

        Assert.Equal(0, result.AlertsRaised);
        Assert.Empty(await db.AlertEvents.ToListAsync());
    }

    [Fact]
    public async Task PerClientThreshold_OverridesTheDefault()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        client.AlertComplianceDropPercent = 60;   // tolerate a big drop
        for (var d = 7; d >= 1; d--)
        {
            AddDay(db, domain.Id, d, messages: 1000, compliant: 1000);
        }
        AddDay(db, domain.Id, 0, messages: 1000, compliant: 700);   // 30 point drop
        await db.SaveChangesAsync();

        var result = await Service(db, new FakeEmailSender()).EvaluateAsync(CancellationToken.None);

        // Over the 15 default, under this client's 60.
        Assert.Equal(0, result.AlertsRaised);
    }

    [Fact]
    public async Task AlertsDisabledForClient_RaisesNothing()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        client.AlertsEnabled = false;
        for (var d = 7; d >= 1; d--)
        {
            AddDay(db, domain.Id, d, messages: 1000, compliant: 1000);
        }
        AddDay(db, domain.Id, 0, messages: 1000, compliant: 0);
        await db.SaveChangesAsync();

        var result = await Service(db, new FakeEmailSender()).EvaluateAsync(CancellationToken.None);

        Assert.Equal(0, result.ClientsEvaluated);
        Assert.Equal(0, result.AlertsRaised);
    }

    [Fact]
    public async Task Cooldown_SuppressesARepeatOfTheSameAlert()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        for (var d = 7; d >= 1; d--)
        {
            AddDay(db, domain.Id, d, messages: 1000, compliant: 1000);
        }
        AddDay(db, domain.Id, 0, messages: 1000, compliant: 100);
        await db.SaveChangesAsync();

        var email = new FakeEmailSender();
        var first = await Service(db, email).EvaluateAsync(CancellationToken.None);
        var second = await Service(db, email).EvaluateAsync(CancellationToken.None);

        Assert.Equal(1, first.AlertsRaised);
        Assert.Equal(0, second.AlertsRaised);
        Assert.Equal(1, second.Suppressed);
        Assert.Single(await db.AlertEvents.ToListAsync());
    }

    [Fact]
    public async Task RaisesPolicyRegression_WhenPolicyWeakens()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        // Was enforcing, now back to monitoring only.
        AddDay(db, domain.Id, 3, messages: 10, compliant: 10, policy: "reject");
        AddDay(db, domain.Id, 0, messages: 10, compliant: 10, policy: "none");
        await db.SaveChangesAsync();

        var result = await Service(db, new FakeEmailSender()).EvaluateAsync(CancellationToken.None);

        var alert = Assert.Single(await db.AlertEvents.ToListAsync());
        Assert.Equal(AlertRuleTypes.PolicyRegression, alert.RuleType);
        Assert.Equal("critical", alert.Severity);       // dropped all the way to none
        Assert.Contains("p=reject", alert.Title);
        Assert.Equal(1, result.AlertsRaised);
    }

    [Fact]
    public async Task StrengtheningThePolicy_IsNotAnAlert()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        AddDay(db, domain.Id, 3, messages: 10, compliant: 10, policy: "none");
        AddDay(db, domain.Id, 0, messages: 10, compliant: 10, policy: "reject");
        await db.SaveChangesAsync();

        var result = await Service(db, new FakeEmailSender()).EvaluateAsync(CancellationToken.None);

        Assert.Equal(0, result.AlertsRaised);
    }

    [Fact]
    public async Task EmailsClientRecipientsAndAgencyWideOnes_AndRecordsDelivery()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        db.AddRange(
            new NotificationRecipient { ClientId = client.Id, Email = "ops@acme.example", Kind = "alert" },
            new NotificationRecipient { ClientId = null, Email = "agency@example.com", Kind = "both" },
            // digest-only must not receive alerts
            new NotificationRecipient { ClientId = client.Id, Email = "monthly@acme.example", Kind = "digest" });
        for (var d = 7; d >= 1; d--)
        {
            AddDay(db, domain.Id, d, messages: 1000, compliant: 1000);
        }
        AddDay(db, domain.Id, 0, messages: 1000, compliant: 100);
        await db.SaveChangesAsync();

        var email = new FakeEmailSender();
        var result = await Service(db, email).EvaluateAsync(CancellationToken.None);

        Assert.Equal(1, result.EmailsSent);
        var sent = Assert.Single(email.Sent);
        Assert.Contains("ops@acme.example", sent.To);
        Assert.Contains("agency@example.com", sent.To);
        Assert.DoesNotContain("monthly@acme.example", sent.To);
        Assert.Contains("https://dmarc.example.com/domains/", sent.Body);
        Assert.NotNull(Assert.Single(await db.AlertEvents.ToListAsync()).NotifiedAtUtc);
    }

    [Fact]
    public async Task WithoutRecipients_TheAlertIsStillRecorded()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        for (var d = 7; d >= 1; d--)
        {
            AddDay(db, domain.Id, d, messages: 1000, compliant: 1000);
        }
        AddDay(db, domain.Id, 0, messages: 1000, compliant: 100);
        await db.SaveChangesAsync();

        var email = new FakeEmailSender();
        var result = await Service(db, email).EvaluateAsync(CancellationToken.None);

        Assert.Equal(1, result.AlertsRaised);
        Assert.Equal(0, result.EmailsSent);
        Assert.Empty(email.Sent);
        // Recorded but not notified — visible in the UI, no delivery attempted.
        Assert.Null(Assert.Single(await db.AlertEvents.ToListAsync()).NotifiedAtUtc);
    }

    [Fact]
    public async Task GloballyDisabled_DoesNothing()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        for (var d = 7; d >= 1; d--)
        {
            AddDay(db, domain.Id, d, messages: 1000, compliant: 1000);
        }
        AddDay(db, domain.Id, 0, messages: 1000, compliant: 0);
        await db.SaveChangesAsync();

        var result = await Service(db, new FakeEmailSender(), new AlertOptions { Enabled = false })
            .EvaluateAsync(CancellationToken.None);

        Assert.Equal(0, result.ClientsEvaluated);
        Assert.Empty(await db.AlertEvents.ToListAsync());
    }
}
