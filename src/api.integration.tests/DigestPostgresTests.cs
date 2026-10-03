using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Notifications;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// The monthly digest against a real database. The unit suite runs it on the InMemory
/// provider, which evaluates LINQ in memory and so proves nothing about whether the
/// grouped aggregates translate to SQL; it also does not enforce unique indexes, and the
/// digest's idempotency rests on one whose nulls must compare equal.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DigestPostgresTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid SourceId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private sealed class CapturingSender : IEmailSender
    {
        public List<(IReadOnlyCollection<string> To, string Subject, string Text, string? Html)> Sent { get; } = [];
        public bool IsConfigured => true;

        public Task<bool> SendAsync(
            IReadOnlyCollection<string> to, string subject, string textBody, string? htmlBody, CancellationToken ct)
        {
            Sent.Add((to, subject, textBody, htmlBody));
            return Task.FromResult(true);
        }
    }

    private sealed class NoHostnames : IHostnameResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(IReadOnlyCollection<string> ips, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, string?>>(ips.ToDictionary(ip => ip, _ => (string?)null));
    }

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();
        await using var db = postgres.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE digest_delivery, notification_recipient_client, notification_recipient, alert_event CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static DigestService Service(DmarcAnalyzerDbContext db, IEmailSender email)
        => new(db, email, new NoHostnames(),
            new DigestRenderer(Options.Create(new BrandingOptions()), Options.Create(new EmailOptions { BaseUrl = "https://dmarc.example.com" })),
            Options.Create(new DigestOptions { DayOfMonth = 1 }),
            NullLogger<DigestService>.Instance);

    private static DateTime LastMonthStart()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
    }

    private static async Task<(Client Client, Domain Domain)> SeedClientAsync(
        DmarcAnalyzerDbContext db, string slug, DigestThresholds? thresholds = null)
    {
        if (!await db.ReportSources.AnyAsync(s => s.Id == SourceId))
        {
            var owner = new Client { Name = "Owner", Slug = "owner", Timezone = "UTC" };
            db.Clients.Add(owner);
            db.ReportSources.Add(new ReportSource
            {
                Id = SourceId, Name = "Mailbox", Protocol = "imap", Host = "imap.example.test", Port = 993,
                UseTls = true, Username = "rua@example.test", PasswordEncrypted = "x", DefaultClientId = owner.Id,
            });
        }

        var client = new Client { Name = slug, Slug = slug, Timezone = "UTC", DigestThresholds = thresholds };
        var domain = new Domain
        {
            ClientId = client.Id, Name = $"{slug}.example", CreatedAtUtc = DateTime.UtcNow.AddYears(-1),
        };
        db.AddRange(client, domain);
        await db.SaveChangesAsync();
        return (client, domain);
    }

    private static void AddDay(DmarcAnalyzerDbContext db, Guid domainId, DateTime day, int pass, int fail, string policy = "none", string failingIp = "198.51.100.24")
    {
        var report = new DmarcReport
        {
            DomainId = domainId, ReportSourceId = SourceId, OrganizationName = "google.com",
            ReportId = Guid.NewGuid().ToString("N"), RangeBeginUtc = day, RangeEndUtc = day.AddHours(23),
            RecordCount = 2, IngestedAtUtc = DateTime.UtcNow, PublishedPolicy = policy, SubdomainPolicy = policy,
            PublishedPct = 100,
        };
        db.Add(report);
        db.Add(new DmarcReportRecord
        {
            DmarcReportId = report.Id, ReportRangeBeginUtc = day, SourceIp = "203.0.113.10", MessageCount = pass,
            Disposition = "none", DkimResult = "pass", SpfResult = "pass", HeaderFrom = "x", EnvelopeFrom = "x", EnvelopeTo = "x",
        });
        if (fail > 0)
        {
            db.Add(new DmarcReportRecord
            {
                DmarcReportId = report.Id, ReportRangeBeginUtc = day, SourceIp = failingIp, MessageCount = fail,
                Disposition = "none", DkimResult = "fail", SpfResult = "fail", HeaderFrom = "x", EnvelopeFrom = "x", EnvelopeTo = "x",
            });
        }
    }

    [Fact]
    public async Task EveryQueryTranslates_AndTheSummaryAddsUp()
    {
        var start = LastMonthStart();
        Guid clientId, domainId;
        await using (var db = postgres.CreateContext())
        {
            var (client, domain) = await SeedClientAsync(db, "acme");
            (clientId, domainId) = (client.Id, domain.Id);
            // Ninety days of clean history at p=none, then a bad month from a new source.
            for (var back = 90; back >= 35; back -= 5)
            {
                AddDay(db, domain.Id, start.AddDays(-back), pass: 300, fail: 0);
            }
            AddDay(db, domain.Id, start.AddMonths(-1).AddDays(2), pass: 1000, fail: 0);
            AddDay(db, domain.Id, start.AddDays(2), pass: 900, fail: 100, failingIp: "192.0.2.77");
            db.AlertEvents.Add(new AlertEvent
            {
                ClientId = client.Id, DomainId = domain.Id, RuleType = "failure_spike", Severity = "warning",
                Status = "open", Title = "Failure spike on acme.example", Details = "x", DetectedAtUtc = start.AddDays(3),
            });
            await db.SaveChangesAsync();
        }

        await using (var db = postgres.CreateContext())
        {
            var summary = await Service(db, new CapturingSender())
                .BuildAsync(clientId, start, start.AddMonths(1), CancellationToken.None);

            Assert.NotNull(summary);
            Assert.Equal(1000, summary!.Messages);
            Assert.Equal(900, summary.CompliantMessages);
            Assert.Equal(1.0, summary.PreviousComplianceRate);
            Assert.Equal(1, summary.FailingSources);
            var source = Assert.Single(summary.TopFailingSources);
            Assert.True(source.IsNew);
            Assert.Contains(summary.Findings, f => f.Kind == DigestFindingKind.LowCompliance && f.DomainId == domainId);
            Assert.Contains(summary.Findings, f => f.Kind == DigestFindingKind.ComplianceDrop);
            Assert.DoesNotContain(summary.Findings, f => f.Kind == DigestFindingKind.ReadyToTighten);
            Assert.Equal("Failure spike on acme.example", Assert.Single(summary.Alerts).Title);
        }
    }

    [Fact]
    public async Task ThresholdsRoundTripThroughTheJsonbColumn()
    {
        Guid clientId;
        await using (var db = postgres.CreateContext())
        {
            clientId = (await SeedClientAsync(db, "acme",
                new DigestThresholds { LowCompliancePercent = 95.5, NoReports = false })).Client.Id;
        }

        await using (var db = postgres.CreateContext())
        {
            var thresholds = (await db.Clients.SingleAsync(c => c.Id == clientId)).DigestThresholds;
            Assert.Equal(new DigestThresholds { LowCompliancePercent = 95.5, NoReports = false }, thresholds);

            var raw = await db.Database
                .SqlQuery<string>($"SELECT \"DigestThresholds\"::text AS \"Value\" FROM client WHERE \"Id\" = {clientId}")
                .SingleAsync();
            Assert.DoesNotContain("isEmpty", raw);
            Assert.DoesNotContain("null", raw);
        }
    }

    [Fact]
    public async Task RoutingEditsOnAnExistingRecipientSave()
    {
        // Adding a per-client row to a recipient that is already tracked once went out as
        // an UPDATE of a row that did not exist, because the new row's key is set
        // client-side. The InMemory suite could not see it.
        Guid recipientId, alpha, bravo;
        await using (var db = postgres.CreateContext())
        {
            alpha = (await SeedClientAsync(db, "alpha")).Client.Id;
            bravo = (await SeedClientAsync(db, "bravo")).Client.Id;
            var recipient = new NotificationRecipient { Email = "agency@example.com", Kind = "digest" };
            db.NotificationRecipients.Add(recipient);
            await db.SaveChangesAsync();
            recipientId = recipient.Id;
        }

        async Task ApplyAsync(string defaultMode, params (Guid, string?)[] modes)
        {
            await using var db = postgres.CreateContext();
            var recipient = await db.NotificationRecipients.Include(r => r.ClientModes).SingleAsync(r => r.Id == recipientId);
            Assert.Null(await RecipientRouting.ApplyAsync(db, recipient, defaultMode, modes, CancellationToken.None));
            await db.SaveChangesAsync();
        }

        await ApplyAsync("rollup", (alpha, "separate"), (bravo, "rollup"));
        await ApplyAsync("off", (alpha, "off"), (bravo, "rollup"));

        await using (var db = postgres.CreateContext())
        {
            var recipient = await db.NotificationRecipients.Include(r => r.ClientModes).SingleAsync(r => r.Id == recipientId);
            Assert.Equal("off", recipient.DigestDefaultMode);
            var row = Assert.Single(recipient.ClientModes);   // alpha now equals the default, so it is not stored
            Assert.Equal((bravo, "rollup"), (row.ClientId, row.DigestMode));
        }
    }

    [Fact]
    public async Task SendingTwiceMailsOnce_AndARollupRowCollidesWithItsRetry()
    {
        var start = LastMonthStart();
        await using (var db = postgres.CreateContext())
        {
            foreach (var slug in new[] { "alpha", "bravo" })
            {
                var (_, domain) = await SeedClientAsync(db, slug);
                AddDay(db, domain.Id, start.AddDays(2), pass: 500, fail: 0);
            }
            db.NotificationRecipients.Add(new NotificationRecipient { Email = "agency@example.com", Kind = "digest" });
            await db.SaveChangesAsync();
        }

        var email = new CapturingSender();
        await using (var db = postgres.CreateContext())
        {
            Assert.Equal(1, (await Service(db, email).SendDueAsync(CancellationToken.None)).Sent);
        }
        await using (var db = postgres.CreateContext())
        {
            Assert.Equal(0, (await Service(db, email).SendDueAsync(CancellationToken.None)).Sent);
        }

        var mail = Assert.Single(email.Sent);
        Assert.Contains("2 clients", mail.Subject);

        // The roll-up row has a null ClientId. With Postgres' default NULLS DISTINCT a second
        // one would slip past the unique index and a racing pass would mail the month twice.
        await using (var db = postgres.CreateContext())
        {
            db.DigestDeliveries.Add(new DigestDelivery
            {
                RecipientEmail = "agency@example.com", ClientId = null, ClientCount = 2,
                PeriodStartUtc = start, PeriodEndUtc = start.AddMonths(1),
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
