using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Mailbox retention deletion is the only thing in this application that destroys data it
/// does not own, and it cannot be undone: once a message is expunged from a customer's
/// mailbox there is no second copy to fall back to unless the archive happens to be on.
/// <para>
/// Every rule that makes it safe is decided here rather than in the IMAP loop, precisely so
/// it can be tested. Three of them matter most. It is off unless someone turned it on for
/// that source. It uses the <em>widest</em> retention window among the clients a mailbox
/// serves, because one mailbox commonly receives reports for several. And it stops entirely
/// for a source serving any client under legal hold — data preserved for a dispute is
/// exactly the data whose upstream copy must not be deleted.
/// </para>
/// </summary>
public sealed class MailboxRetentionPlannerTests
{
    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new DmarcAnalyzerDbContext(options);
    }

    private static MailboxRetentionPlanner Planner(DmarcAnalyzerDbContext db, int graceDays = 30)
        => new(db, Options.Create(new WorkerOptions { MailboxRetentionGraceDays = graceDays }));

    private static Client NewClient(string slug, int retentionMonths, bool legalHold = false)
        => new()
        {
            Name = slug, Slug = slug, RetentionMonths = retentionMonths,
            LegalHold = legalHold, Timezone = "UTC",
        };

    private static MailboxSource NewSource(Guid defaultClientId, bool enabled)
        => new()
        {
            Name = "mailbox", Host = "imap.example", Port = 993, Username = "dmarc@example",
            PasswordEncrypted = "enc:v1:x", DefaultClientId = defaultClientId,
            DeleteAfterRetention = enabled,
        };

    /// <summary>
    /// Attributes a report to a client by giving that client a domain and filing a report
    /// for it against this source — which is how the planner discovers who a mailbox serves.
    /// </summary>
    private static void AddReportFor(
        DmarcAnalyzerDbContext db, Client client, MailboxSource source, string domainName)
    {
        var domain = new Domain { ClientId = client.Id, Name = domainName };
        db.Add(domain);
        db.Add(new DmarcReport
        {
            DomainId = domain.Id, ReportSourceId = source.Id, OrganizationName = "google.com",
            ReportId = Guid.NewGuid().ToString(), RangeBeginUtc = DateTime.UtcNow.AddDays(-2),
            RangeEndUtc = DateTime.UtcNow.AddDays(-1), RecordCount = 1,
        });
    }

    [Fact]
    public async Task IsSuspendedUntilSomeoneTurnsItOn()
    {
        await using var db = NewDb();
        var client = NewClient("acme", 12);
        var source = NewSource(client.Id, enabled: false);
        db.AddRange(client, source);
        await db.SaveChangesAsync();

        var plan = Assert.Single(await Planner(db).PlanAsync(default));

        Assert.False(plan.Enabled);
        Assert.True(plan.Suspended);
        Assert.Null(plan.CutoffUtc);
        Assert.Contains("not enabled", plan.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UsesTheWidestWindowAmongTheClientsTheMailboxServes()
    {
        await using var db = NewDb();
        var narrow = NewClient("narrow", 6);
        var wide = NewClient("wide", 27);
        var source = NewSource(narrow.Id, enabled: true);
        db.AddRange(narrow, wide, source);
        AddReportFor(db, narrow, source, "narrow.example");
        AddReportFor(db, wide, source, "wide.example");
        await db.SaveChangesAsync();

        var plan = Assert.Single(await Planner(db, graceDays: 0).PlanAsync(default));

        // Cutting on the 6-month client's schedule would delete the 27-month client's mail.
        Assert.Equal(27, plan.RetentionMonths);
        Assert.Equal(["narrow", "wide"], plan.ClientSlugs);

        var expected = DateTime.UtcNow.AddMonths(-27);
        Assert.True(Math.Abs((plan.CutoffUtc!.Value - expected).TotalMinutes) < 1);
    }

    [Fact]
    public async Task LegalHoldOnAnyServedClientStopsTheWholeSource()
    {
        await using var db = NewDb();
        var ordinary = NewClient("ordinary", 12);
        var held = NewClient("held", 12, legalHold: true);
        var source = NewSource(ordinary.Id, enabled: true);
        db.AddRange(ordinary, held, source);
        AddReportFor(db, ordinary, source, "ordinary.example");
        AddReportFor(db, held, source, "held.example");
        await db.SaveChangesAsync();

        var plan = Assert.Single(await Planner(db).PlanAsync(default));

        // Not "delete the other client's mail" — the pass cannot tell one client's messages
        // apart before deleting them, so the whole source stops.
        Assert.True(plan.Suspended);
        Assert.Null(plan.CutoffUtc);
        Assert.Equal(["held"], plan.LegalHoldClientSlugs);
        Assert.Contains("legal hold", plan.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheGraceMarginPushesTheCutoffFurtherBack()
    {
        await using var db = NewDb();
        var client = NewClient("acme", 12);
        var source = NewSource(client.Id, enabled: true);
        db.AddRange(client, source);
        await db.SaveChangesAsync();

        var tight = Assert.Single(await Planner(db, graceDays: 0).PlanAsync(default));
        db.ChangeTracker.Clear();
        var generous = Assert.Single(await Planner(db, graceDays: 30).PlanAsync(default));

        Assert.True(generous.CutoffUtc < tight.CutoffUtc);
        Assert.Equal(30, generous.GraceDays);
    }

    [Fact]
    public async Task ANewSourceWithNoReportsStillResolvesItsDefaultClient()
    {
        await using var db = NewDb();
        var client = NewClient("acme", 18);
        var source = NewSource(client.Id, enabled: true);
        db.AddRange(client, source);
        await db.SaveChangesAsync();

        var plan = Assert.Single(await Planner(db).PlanAsync(default));

        // Computed from an empty set, the window would be 0 months and the cutoff would be
        // now — which would delete the entire mailbox.
        Assert.False(plan.Suspended);
        Assert.Equal(18, plan.RetentionMonths);
        Assert.Equal(["acme"], plan.ClientSlugs);
    }

    [Fact]
    public async Task ASourceWhoseClientHasVanishedIsSuspendedRatherThanGuessed()
    {
        await using var db = NewDb();
        // A default client id pointing at nothing: no window, so no defensible cutoff.
        var source = NewSource(Guid.NewGuid(), enabled: true);
        db.Add(source);
        await db.SaveChangesAsync();

        var plan = Assert.Single(await Planner(db).PlanAsync(default));

        Assert.True(plan.Suspended);
        Assert.Null(plan.CutoffUtc);
        Assert.Contains("no client", plan.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlansSuspendedSourcesToo()
    {
        await using var db = NewDb();
        var client = NewClient("acme", 12);
        var on = NewSource(client.Id, enabled: true);
        var off = NewSource(client.Id, enabled: false);
        off.Name = "other mailbox";
        db.AddRange(client, on, off);
        await db.SaveChangesAsync();

        var plans = await Planner(db).PlanAsync(default);

        // A preview that hid the suspended sources could not answer the question an
        // operator actually asks: why is that mailbox still growing?
        Assert.Equal(2, plans.Count);
        Assert.Contains(plans, p => p.Suspended);
        Assert.Contains(plans, p => !p.Suspended);
    }

    [Theory]
    [InlineData(false, 1, "not enabled")]
    [InlineData(true, 0, "no client")]
    public void TheSuspensionRulesReadAsRules(bool enabled, int clientCount, string expected)
    {
        var (suspended, reason) = MailboxRetentionPlanner.Suspension(enabled, clientCount, []);

        Assert.True(suspended);
        Assert.Contains(expected, reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegalHoldOutranksEverythingElse()
    {
        var (suspended, reason) = MailboxRetentionPlanner.Suspension(
            enabled: true, clientCount: 3, legalHoldSlugs: ["held"]);

        Assert.True(suspended);
        Assert.Contains("legal hold", reason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The archive key has to be stable across passes: the ingest side writes it and the
    /// deletion side looks it up, and a mismatch would silently mean "not archived" for
    /// every message — which reads as the safety rule working when it is really a bug.
    /// </summary>
    [Fact]
    public void ArchiveKeysAreStableAndDatePartitioned()
    {
        var sourceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var at = new DateTime(2026, 7, 27, 6, 11, 0, DateTimeKind.Utc);

        var key = ReportMailArchive.Key("dmarc", sourceId, 4711, 9, at);

        Assert.Equal(
            $"dmarc/reports/2026/07/27/{sourceId}/9-4711.eml.gz",
            key);

        // Same inputs, same key — otherwise ExistsAsync can never find what TryArchiveAsync wrote.
        Assert.Equal(key, ReportMailArchive.Key("dmarc/", sourceId, 4711, 9, at));

        // UIDVALIDITY is part of the name because a UID only identifies a message within one
        // validity generation.
        Assert.NotEqual(key, ReportMailArchive.Key("dmarc", sourceId, 4711, 10, at));
    }
}
