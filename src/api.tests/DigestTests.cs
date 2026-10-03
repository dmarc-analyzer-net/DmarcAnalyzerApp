using DmarcAnalyzer.Api.Application.Analytics;
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
    private const string GoodIp = "203.0.113.10";
    private const string BadIp = "198.51.100.24";

    private sealed class FakeEmailSender : IEmailSender
    {
        public List<(IReadOnlyCollection<string> To, string Subject, string Text, string? Html)> Sent { get; } = [];
        public bool Deliver { get; init; } = true;
        public bool IsConfigured => Deliver;

        public Task<bool> SendAsync(
            IReadOnlyCollection<string> to, string subject, string textBody, string? htmlBody, CancellationToken ct)
        {
            Sent.Add((to, subject, textBody, htmlBody));
            return Task.FromResult(Deliver);
        }
    }

    private sealed class FakeHostnames : IHostnameResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(IReadOnlyCollection<string> ips, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, string?>>(
                ips.ToDictionary(ip => ip, ip => ip == BadIp ? "mail.unknown-sender.example" : null));
    }

    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static DigestRenderer Renderer(BrandingOptions? branding = null)
        => new(Options.Create(branding ?? new BrandingOptions()),
            Options.Create(new EmailOptions { BaseUrl = "https://dmarc.example.com/" }));

    private static DigestService Service(
        DmarcAnalyzerDbContext db, IEmailSender email, DigestOptions? options = null)
        => new(db, email, new FakeHostnames(), Renderer(),
            Options.Create(options ?? new DigestOptions()),
            NullLogger<DigestService>.Instance);

    private static DateTime LastMonthStart()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
    }

    private static (Client, Domain) Seed(DmarcAnalyzerDbContext db, string slug = "acme", string? domainName = null)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = slug, Slug = slug, Timezone = "UTC", RetentionMonths = 27,
            IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Add(client);
        return (client, AddDomain(db, client, domainName ?? $"{slug}.example"));
    }

    private static Domain AddDomain(DmarcAnalyzerDbContext db, Client client, string name)
    {
        // Created well before the period, so a quiet month counts as "no reports".
        var domain = new Domain
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = name, IsActive = true,
            CreatedAtUtc = DateTime.UtcNow.AddYears(-1), UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Add(domain);
        return domain;
    }

    private static void AddTraffic(
        DmarcAnalyzerDbContext db, Guid domainId, DateTime day, int messages, int compliant,
        string policy = "none", string failingIp = BadIp)
    {
        var report = new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domainId, ReportSourceId = Guid.NewGuid(),
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
                Id = Guid.NewGuid(), DmarcReportId = report.Id, SourceIp = GoodIp,
                MessageCount = compliant, Disposition = "none", DkimResult = "pass", SpfResult = "pass",
                HeaderFrom = "x", EnvelopeFrom = "x", EnvelopeTo = "x",
            });
        }
        if (messages - compliant > 0)
        {
            db.Add(new DmarcReportRecord
            {
                ReportRangeBeginUtc = day,
                Id = Guid.NewGuid(), DmarcReportId = report.Id, SourceIp = failingIp,
                MessageCount = messages - compliant, Disposition = "none", DkimResult = "fail", SpfResult = "fail",
                HeaderFrom = "x", EnvelopeFrom = "x", EnvelopeTo = "x",
            });
        }
    }

    private static async Task<DigestSummary> Build(DmarcAnalyzerDbContext db, Guid clientId, DigestOptions? options = null)
    {
        var start = LastMonthStart();
        var summary = await Service(db, new FakeEmailSender(), options)
            .BuildAsync(clientId, start, start.AddMonths(1), CancellationToken.None);
        return summary!;
    }

    // ---- What a summary contains ---------------------------------------------------

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

        var summary = await Build(db, client.Id);

        Assert.Equal(1000, summary.Messages);
        Assert.Equal(900, summary.CompliantMessages);
        Assert.Equal(0.9, summary.ComplianceRate, 6);
        Assert.Equal(1, summary.Domains);
        Assert.Equal(1, summary.DomainsEnforcing);   // p=reject
        Assert.Equal(1, summary.FailingSources);
        var source = Assert.Single(summary.TopFailingSources);
        Assert.Equal(BadIp, source.Ip);
        Assert.Equal("mail.unknown-sender.example", source.Hostname);
        Assert.Equal(100, source.FailingMessages);
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

        var summary = await Build(db, client.Id);
        var mail = Renderer().RenderClient(summary);

        Assert.Equal(1.0, summary.ComplianceRate);
        Assert.Equal(0.5, summary.PreviousComplianceRate);
        Assert.Contains("+50.0 pts", mail.Text);
    }

    [Fact]
    public async Task AHealthyEnforcingDomain_IsNotFlagged()
    {
        // The case that started this: 5 failures in 6,728 at p=reject was listed under
        // "domains needing attention" because the list had no threshold at all.
        await using var db = NewDb();
        var (client, domain) = Seed(db, "bravo", "rejse.bravo.example");
        var start = LastMonthStart();
        AddTraffic(db, domain.Id, start.AddMonths(-1).AddDays(3), messages: 6500, compliant: 6495, policy: "reject");
        AddTraffic(db, domain.Id, start.AddDays(3), messages: 6728, compliant: 6723, policy: "reject");
        await db.SaveChangesAsync();

        var summary = await Build(db, client.Id);
        var mail = Renderer().RenderClient(summary);

        Assert.Empty(summary.Findings);
        Assert.EndsWith(": all clear", mail.Subject);
        Assert.Contains("99.92%", mail.Text);
        Assert.DoesNotContain("Needs attention", mail.Html);
    }

    [Fact]
    public async Task LowCompliance_IsFlagged_OnlyWithEnoughVolume()
    {
        await using var db = NewDb();
        var (client, busy) = Seed(db);
        var quiet = AddDomain(db, client, "quiet.acme.example");
        var start = LastMonthStart();
        AddTraffic(db, busy.Id, start.AddDays(3), messages: 1000, compliant: 950);
        AddTraffic(db, quiet.Id, start.AddDays(3), messages: 20, compliant: 10);   // 50%, but below MinMessages
        await db.SaveChangesAsync();

        var summary = await Build(db, client.Id);

        var finding = Assert.Single(summary.Findings);
        Assert.Equal(DigestFindingKind.LowCompliance, finding.Kind);
        Assert.Equal(busy.Name, finding.Domain);
        Assert.Contains("95.0%", finding.Summary);
        Assert.Equal(busy.Id, summary.DomainLines[0].DomainId);   // flagged domains sort first
    }

    [Fact]
    public async Task ACompliancedrop_IsFlagged()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        var start = LastMonthStart();
        AddTraffic(db, domain.Id, start.AddMonths(-1).AddDays(3), messages: 1000, compliant: 1000);
        AddTraffic(db, domain.Id, start.AddDays(3), messages: 1000, compliant: 985);   // 98.5%: above 98, but down 1.5
        await db.SaveChangesAsync();

        Assert.Empty((await Build(db, client.Id)).Findings);

        var finding = Assert.Single((await Build(db, client.Id, new DigestOptions { ComplianceDropPoints = 1 })).Findings);
        Assert.Equal(DigestFindingKind.ComplianceDrop, finding.Kind);
        Assert.Contains("fell 1.5 points", finding.Summary);
    }

    [Fact]
    public async Task ReportsThatStopped_AreFlagged_AndCanBeSwitchedOff()
    {
        await using var db = NewDb();
        var (client, stopped) = Seed(db);
        // Never reports: a seasonal or parked domain. Its silence is not news every month.
        AddDomain(db, client, "christmas.acme.example");
        AddTraffic(db, stopped.Id, LastMonthStart().AddMonths(-1).AddDays(3), messages: 500, compliant: 500);
        await db.SaveChangesAsync();

        var flagged = await Build(db, client.Id);
        var finding = Assert.Single(flagged.Findings);
        Assert.Equal(DigestFindingKind.NoReports, finding.Kind);
        Assert.Equal(stopped.Name, finding.Domain);
        Assert.Contains("Reports stopped: 500 messages", finding.Summary);
        Assert.EndsWith(": 1 thing needs attention", Renderer().RenderClient(flagged).Subject);

        var quiet = await Build(db, client.Id, new DigestOptions { NoReports = false });
        var mail = Renderer().RenderClient(quiet);
        Assert.Empty(quiet.Findings);
        Assert.Contains("No reports this month", mail.Text);
        Assert.DoesNotContain("NaN", mail.Text);
        Assert.DoesNotContain("NaN", mail.Html);
    }

    [Fact]
    public async Task ClientOverrides_ReplaceTheDefaults()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        AddTraffic(db, domain.Id, LastMonthStart().AddDays(3), messages: 1000, compliant: 950);
        await db.SaveChangesAsync();
        Assert.Single((await Build(db, client.Id)).Findings);

        // 0 switches a trigger off for this client.
        client.DigestThresholds = new DigestThresholds { LowCompliancePercent = 0 };
        await db.SaveChangesAsync();
        Assert.Empty((await Build(db, client.Id)).Findings);

        // A volume floor above the month's traffic means the rate is not judged at all.
        client.DigestThresholds = new DigestThresholds { MinMessages = 5000 };
        await db.SaveChangesAsync();
        Assert.Empty((await Build(db, client.Id)).Findings);
    }

    [Fact]
    public async Task ADomainHoldingAHighRateAtPNone_IsReadyToTighten()
    {
        await using var db = NewDb();
        var (client, steady) = Seed(db);
        var young = AddDomain(db, client, "new.acme.example");
        var end = LastMonthStart().AddMonths(1);
        for (var daysBack = 70; daysBack >= 5; daysBack -= 5)
        {
            AddTraffic(db, steady.Id, end.AddDays(-daysBack), messages: 200, compliant: 200);
        }
        // Only reporting for 20 days: not enough history to recommend anything.
        for (var daysBack = 20; daysBack >= 5; daysBack -= 5)
        {
            AddTraffic(db, young.Id, end.AddDays(-daysBack), messages: 200, compliant: 200);
        }
        await db.SaveChangesAsync();

        var summary = await Build(db, client.Id);

        var finding = Assert.Single(summary.Findings);
        Assert.Equal(DigestFindingKind.ReadyToTighten, finding.Kind);
        Assert.False(finding.IsProblem);
        Assert.Equal(steady.Name, finding.Domain);
        Assert.Contains("p=quarantine", finding.Advice);
        Assert.EndsWith(": all clear", Renderer().RenderClient(summary).Subject);
    }

    [Fact]
    public async Task ASourceFailingForTwoDomains_CountsOnce()
    {
        await using var db = NewDb();
        var (client, first) = Seed(db);
        var second = AddDomain(db, client, "shop.acme.example");
        var start = LastMonthStart();
        AddTraffic(db, first.Id, start.AddDays(3), messages: 100, compliant: 90);
        AddTraffic(db, second.Id, start.AddDays(3), messages: 100, compliant: 80);
        await db.SaveChangesAsync();

        var summary = await Build(db, client.Id);

        Assert.Equal(1, summary.FailingSources);
        var source = Assert.Single(summary.TopFailingSources);
        Assert.Equal(30, source.FailingMessages);
        Assert.Equal([second.Name, first.Name], source.Domains);   // worst first
        Assert.Equal($"{second.Name} and 1 more", source.DomainSummary);
    }

    [Fact]
    public async Task ASourceIsNew_OnlyIfUnseenInTheLookback()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        var start = LastMonthStart();
        AddTraffic(db, domain.Id, start.AddDays(-30), messages: 100, compliant: 90, failingIp: "192.0.2.1");
        AddTraffic(db, domain.Id, start.AddDays(3), messages: 100, compliant: 90, failingIp: "192.0.2.1");
        AddTraffic(db, domain.Id, start.AddDays(4), messages: 100, compliant: 80, failingIp: "192.0.2.2");
        await db.SaveChangesAsync();

        var sources = (await Build(db, client.Id)).TopFailingSources.ToDictionary(s => s.Ip);

        Assert.False(sources["192.0.2.1"].IsNew);
        Assert.True(sources["192.0.2.2"].IsNew);
    }

    // ---- Rendering -----------------------------------------------------------------

    [Fact]
    public async Task Html_EncodesEverythingThatCameFromData()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db, "<script>alert(1)</script>");
        AddTraffic(db, domain.Id, LastMonthStart().AddDays(3), messages: 1000, compliant: 500);
        await db.SaveChangesAsync();

        var html = Renderer().RenderClient(await Build(db, client.Id)).Html;

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task Links_AreScopedToTheClientAndDomain()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        AddTraffic(db, domain.Id, LastMonthStart().AddDays(3), messages: 1000, compliant: 500);
        await db.SaveChangesAsync();

        var mail = Renderer().RenderClient(await Build(db, client.Id));

        Assert.Contains($"https://dmarc.example.com/domains?client={client.Id}", mail.Text);
        Assert.Contains($"https://dmarc.example.com/domains/{domain.Id}", mail.Html);
    }

    [Fact]
    public void Branding_RejectsUnsafeValues()
    {
        var renderer = Renderer(new BrandingOptions
        {
            Name = "Example Agency",
            LogoUrl = "javascript:alert(1)",
            AccentColor = "red;background:url(x)",
        });
        var summary = new DigestSummary(
            Guid.NewGuid(), "acme", LastMonthStart(), LastMonthStart().AddMonths(1), 0, 0, 0, 0, 0, null, 0,
            [], [], [], []);

        var html = renderer.RenderClient(summary).Html;

        Assert.Contains("Example Agency", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("url(x)", html);
        Assert.Contains(BrandingOptions.DefaultAccentColor, html);
    }

    [Theory]
    [InlineData(1.0, "100%")]
    [InlineData(0.99999, "99.99%")]   // any failure never reads as 100%
    [InlineData(0.9992568, "99.92%")]
    [InlineData(0.97996, "97.9%")]    // just under a 98% threshold never reads as 98.0%
    [InlineData(0.0, "0.0%")]
    public void Percent_TruncatesRatherThanRounds(double rate, string expected)
        => Assert.Equal(expected, DigestFormat.Percent(rate));

    [Fact]
    public void Thresholds_KeepTheirDecimals()
        => Assert.Equal("99.95", DigestFormat.Number(99.95));

    // ---- Who gets what -------------------------------------------------------------

    private static async Task<(Client A, Client B, Client C)> ThreeClients(DmarcAnalyzerDbContext db)
    {
        var start = LastMonthStart();
        var (a, da) = Seed(db, "alpha");
        var (b, dbm) = Seed(db, "bravo");
        var (c, dc) = Seed(db, "charlie");
        AddTraffic(db, da.Id, start.AddDays(3), messages: 1000, compliant: 1000);
        AddTraffic(db, dbm.Id, start.AddDays(3), messages: 1000, compliant: 900);
        AddTraffic(db, dc.Id, start.AddDays(3), messages: 1000, compliant: 999);
        await db.SaveChangesAsync();
        return (a, b, c);
    }

    private static async Task<FakeEmailSender> SendDue(DmarcAnalyzerDbContext db)
    {
        var email = new FakeEmailSender();
        await Service(db, email, new DigestOptions { DayOfMonth = 1 }).SendDueAsync(CancellationToken.None);
        return email;
    }

    [Fact]
    public async Task AnAllClientsRecipient_GetsOneRollup()
    {
        await using var db = NewDb();
        await ThreeClients(db);
        db.Add(new NotificationRecipient { ClientId = null, Email = "agency@example.com", Kind = "both" });
        await db.SaveChangesAsync();

        var email = await SendDue(db);

        var mail = Assert.Single(email.Sent);
        Assert.Equal(["agency@example.com"], mail.To);
        Assert.EndsWith(": 1 of 3 clients need attention", mail.Subject);
        Assert.Contains("alpha", mail.Text);
        Assert.Contains("charlie", mail.Text);
        Assert.NotNull(mail.Html);
        var row = Assert.Single(await db.DigestDeliveries.ToListAsync());
        Assert.Null(row.ClientId);
        Assert.Equal(3, row.ClientCount);
        Assert.Equal(1, row.RecipientCount);
    }

    [Fact]
    public async Task PerClientModes_SplitOffOrDropAClient()
    {
        await using var db = NewDb();
        var (a, b, c) = await ThreeClients(db);
        var recipient = new NotificationRecipient { ClientId = null, Email = "agency@example.com", Kind = "digest" };
        recipient.ClientModes.Add(new NotificationRecipientClient { ClientId = b.Id, DigestMode = DigestModes.Separate });
        recipient.ClientModes.Add(new NotificationRecipientClient { ClientId = c.Id, DigestMode = DigestModes.Off });
        db.Add(recipient);
        await db.SaveChangesAsync();

        var email = await SendDue(db);

        // alpha alone in the roll-up becomes alpha's own mail; bravo is separate; charlie is off.
        Assert.Equal(2, email.Sent.Count);
        Assert.Contains(email.Sent, m => m.Subject.StartsWith("[DMARC] alpha,"));
        Assert.Contains(email.Sent, m => m.Subject.StartsWith("[DMARC] bravo,"));
        Assert.DoesNotContain(email.Sent, m => m.Text.Contains("charlie"));
    }

    [Fact]
    public async Task ACustomerContact_GetsOnlyTheClientsTheyWereGiven()
    {
        await using var db = NewDb();
        var (a, b, _) = await ThreeClients(db);
        var contact = new NotificationRecipient
        {
            ClientId = null, Email = "owner@group.example", Kind = "digest", DigestDefaultMode = DigestModes.Off,
        };
        contact.ClientModes.Add(new NotificationRecipientClient { ClientId = a.Id, DigestMode = DigestModes.Rollup });
        contact.ClientModes.Add(new NotificationRecipientClient { ClientId = b.Id, DigestMode = DigestModes.Rollup });
        db.Add(contact);
        await db.SaveChangesAsync();

        var mail = Assert.Single((await SendDue(db)).Sent);

        Assert.EndsWith(": 1 of 2 clients need attention", mail.Subject);
        Assert.DoesNotContain("charlie", mail.Text);
    }

    [Fact]
    public async Task ClientRecipients_GetTheirClientAlone_AndAlertOnlyRecipientsGetNothing()
    {
        await using var db = NewDb();
        var (_, b, _) = await ThreeClients(db);
        db.AddRange(
            new NotificationRecipient { ClientId = b.Id, Email = "cfo@bravo.example", Kind = "digest" },
            new NotificationRecipient { ClientId = b.Id, Email = "pager@bravo.example", Kind = "alert" });
        await db.SaveChangesAsync();

        var mail = Assert.Single((await SendDue(db)).Sent);

        Assert.Equal(["cfo@bravo.example"], mail.To);
        Assert.StartsWith("[DMARC] bravo,", mail.Subject);
    }

    [Fact]
    public async Task OneAddressOnTwoRows_GetsEachMailOnce()
    {
        await using var db = NewDb();
        var (_, b, _) = await ThreeClients(db);
        db.AddRange(
            new NotificationRecipient { ClientId = null, Email = "Agency@Example.com", Kind = "both" },
            new NotificationRecipient { ClientId = b.Id, Email = "agency@example.com", Kind = "digest" });
        await db.SaveChangesAsync();

        var sent = (await SendDue(db)).Sent;

        // bravo goes separately (the client row asks for it); alpha and charlie roll up.
        Assert.Equal(2, sent.Count);
        Assert.Single(sent, m => m.Subject.StartsWith("[DMARC] bravo,"));
        Assert.Single(sent, m => m.Subject.Contains("2 clients"));
    }

    [Fact]
    public async Task ClientsWithNoDomains_GetNoDigest()
    {
        await using var db = NewDb();
        await ThreeClients(db);
        db.Add(new Client
        {
            Id = Guid.NewGuid(), Name = "Default", Slug = "default", Timezone = "UTC", RetentionMonths = 27,
            IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        });
        db.Add(new NotificationRecipient { ClientId = null, Email = "agency@example.com", Kind = "digest" });
        await db.SaveChangesAsync();

        var mail = Assert.Single((await SendDue(db)).Sent);

        Assert.DoesNotContain("Default", mail.Text);
        Assert.Contains("3 clients", mail.Subject);
    }

    [Fact]
    public async Task DoesNotSendTheSamePeriodTwice()
    {
        await using var db = NewDb();
        await ThreeClients(db);
        db.Add(new NotificationRecipient { ClientId = null, Email = "agency@example.com", Kind = "digest" });
        await db.SaveChangesAsync();

        var email = new FakeEmailSender();
        var service = Service(db, email, new DigestOptions { DayOfMonth = 1 });
        var first = await service.SendDueAsync(CancellationToken.None);
        var second = await service.SendDueAsync(CancellationToken.None);

        Assert.Equal(1, first.Sent);
        Assert.Equal(0, second.Sent);
        Assert.Equal(1, second.Skipped);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task RowsFromBeforePerRecipientDigests_StillCountAsSent()
    {
        await using var db = NewDb();
        var (a, b, c) = await ThreeClients(db);
        db.Add(new NotificationRecipient { ClientId = null, Email = "agency@example.com", Kind = "digest" });
        // The upgrade lands mid-month after the old code already mailed alpha and bravo.
        foreach (var client in new[] { a, b })
        {
            db.Add(new DigestDelivery
            {
                ClientId = client.Id, PeriodStartUtc = LastMonthStart(), PeriodEndUtc = LastMonthStart().AddMonths(1),
                RecipientCount = 1,
            });
        }
        await db.SaveChangesAsync();

        var mail = Assert.Single((await SendDue(db)).Sent);

        Assert.StartsWith("[DMARC] charlie,", mail.Subject);
    }

    [Fact]
    public async Task ABrokenRelayStillMarksThePeriod_SoItDoesNotRetryForever()
    {
        await using var db = NewDb();
        var (_, b, _) = await ThreeClients(db);
        db.Add(new NotificationRecipient { ClientId = b.Id, Email = "cfo@bravo.example", Kind = "digest" });
        await db.SaveChangesAsync();

        var email = new FakeEmailSender { Deliver = false };
        var service = Service(db, email, new DigestOptions { DayOfMonth = 1 });
        var result = await service.SendDueAsync(CancellationToken.None);
        await service.SendDueAsync(CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Single(email.Sent);   // attempted once, not again on the next pass
        var delivery = Assert.Single(await db.DigestDeliveries.ToListAsync());
        Assert.Equal(0, delivery.RecipientCount);
        Assert.Equal("cfo@bravo.example", delivery.RecipientEmail);
    }

    [Fact]
    public async Task DisabledSendsNothing()
    {
        await using var db = NewDb();
        await ThreeClients(db);
        db.Add(new NotificationRecipient { ClientId = null, Email = "agency@example.com", Kind = "digest" });
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
        await ThreeClients(db);
        db.Add(new NotificationRecipient { ClientId = null, Email = "agency@example.com", Kind = "digest" });
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

    [Fact]
    public async Task PreviewForARecipient_ShowsEveryMailTheyWouldGet()
    {
        await using var db = NewDb();
        var (_, b, _) = await ThreeClients(db);
        var recipient = new NotificationRecipient { ClientId = null, Email = "agency@example.com", Kind = "digest" };
        recipient.ClientModes.Add(new NotificationRecipientClient { ClientId = b.Id, DigestMode = DigestModes.Separate });
        db.Add(recipient);
        await db.SaveChangesAsync();

        var mails = await Service(db, new FakeEmailSender())
            .PreviewRecipientAsync(recipient.Id, LastMonthStart(), CancellationToken.None);

        Assert.NotNull(mails);
        Assert.Equal(2, mails!.Count);
        Assert.True(mails[0].IsRollup);
        Assert.Equal([b.Id], mails[1].ClientIds);
        Assert.Empty(await db.DigestDeliveries.ToListAsync());   // a preview sends and records nothing
    }
}
