using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The report-notes endpoint (PR 2 of docs/planning/rfc-9990-plan.md): tenant scoping,
/// which reports are listed, the window, the cap, and the counts that say what the list
/// cannot show. The jsonb comparison behind the info-only filter is proved against
/// PostgreSQL in the integration suite; InMemory compares the string.
/// </summary>
public sealed class ReportNotesTests
{
    private static readonly DateTime Newest = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static (Client Client, Domain Domain) Seed(DmarcAnalyzerDbContext db, string slug)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = slug, Slug = slug, Timezone = "UTC" };
        var domain = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = $"{slug}.example", IsActive = true };
        db.AddRange(client, domain);
        return (client, domain);
    }

    /// <summary>
    /// A one-day report ending <paramref name="daysAgo"/> days before the newest one.
    /// <paramref name="messages"/> null means the row predates the column.
    /// </summary>
    private static DmarcReport AddReport(
        DmarcAnalyzerDbContext db, Guid domainId, int daysAgo,
        IReadOnlyList<DmarcReportParseMessage>? messages,
        string reporter = "reporter.example",
        string origin = DmarcReportParseOrigins.Ingest)
    {
        var end = Newest.AddDays(-daysAgo);
        var report = new DmarcReport
        {
            Id = Guid.NewGuid(), DomainId = domainId, ReportSourceId = Guid.NewGuid(),
            OrganizationName = reporter, ReportId = $"{reporter}-{daysAgo}",
            RangeBeginUtc = end.AddDays(-1), RangeEndUtc = end, RecordCount = 1,
            IngestedAtUtc = end,
            ParseMessages = messages is null ? null : DmarcReportParseMessagesJson.Serialize(messages),
            ParseWarningCount = messages?.Count(x => DmarcReportParseSeverity.IsAtLeastWarning(x.Severity)),
            ParseMessagesOrigin = messages is null ? null : origin,
        };
        db.Add(report);
        return report;
    }

    private static readonly DmarcReportParseMessage Namespace =
        DmarcReportParseMessage.Info("stripped XML namespace 'urn:ietf:params:xml:ns:dmarc-2.0' for compatibility");

    private static readonly DmarcReportParseMessage Repair =
        DmarcReportParseMessage.Warning("replaced unrecognised policy_evaluated/dkim value 'x' with 'fail' for compatibility");

    private static readonly DmarcReportParseMessage Schema =
        DmarcReportParseMessage.Error("The element 'feedback' has invalid child element 'extra'.");

    [Fact]
    public async Task CrossTenant_ReadsAsNotFound()
    {
        await using var db = NewDb();
        var (granted, grantedDomain) = Seed(db, "granted");
        var (_, otherDomain) = Seed(db, "other");
        AddReport(db, grantedDomain.Id, 0, [Repair]);
        AddReport(db, otherDomain.Id, 0, [Repair]);
        await db.SaveChangesAsync();

        var viewer = TestAnalytics.Service(db, TestCurrentUserContext.Viewer(granted.Id));

        Assert.Null(await viewer.GetReportNotesAsync(otherDomain.Id, 30, false, CancellationToken.None));
        Assert.NotNull(await viewer.GetReportNotesAsync(grantedDomain.Id, 30, false, CancellationToken.None));
    }

    /// <summary>
    /// Only reports with something at warning level are listed; the rest are counted, each
    /// kind on its own, so the panel can say what it shows and what it cannot see.
    /// </summary>
    [Fact]
    public async Task ListsReportsWithWarnings_AndCountsTheOthers()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db, "acme");
        var repaired = AddReport(db, domain.Id, 1, [Namespace, Repair], reporter: "repaired.example");
        var broken = AddReport(db, domain.Id, 3, [Schema], reporter: "broken.example");
        AddReport(db, domain.Id, 2, [Namespace], reporter: "info-only.example");
        AddReport(db, domain.Id, 0, [], reporter: "clean.example");
        AddReport(db, domain.Id, 4, null, reporter: "old.example");
        await db.SaveChangesAsync();

        var notes = await TestAnalytics.Service(db, TestCurrentUserContext.Admin())
            .GetReportNotesAsync(domain.Id, 30, false, CancellationToken.None);

        Assert.NotNull(notes);
        Assert.False(notes.IncludeInfo);
        Assert.Equal(5, notes.ReportsInWindow);
        Assert.Equal(2, notes.ReportsWithWarnings);
        Assert.Equal(1, notes.ReportsWithInfoOnly);
        Assert.Equal(1, notes.ReportsNotRecorded);

        // Newest first, with the messages, count and origin each row was stored with.
        Assert.Equal([repaired.Id, broken.Id], notes.Reports.Select(x => x.Id));
        var first = notes.Reports[0];
        Assert.Equal("repaired.example", first.OrganizationName);
        Assert.Equal(repaired.ReportId, first.ReportId);
        Assert.Equal(repaired.RangeBeginUtc, first.RangeBeginUtc);
        Assert.Equal(repaired.RangeEndUtc, first.RangeEndUtc);
        Assert.Equal(DmarcReportParseOrigins.Ingest, first.Origin);
        Assert.Equal(1, first.WarningCount);
        Assert.Equal([Namespace, Repair], first.Messages);
        Assert.Equal(1, notes.Reports[1].WarningCount);
    }

    [Fact]
    public async Task IncludeInfo_AlsoListsInfoOnlyReports_ButNeverCleanOrUnrecordedOnes()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db, "acme");
        var repaired = AddReport(db, domain.Id, 1, [Repair], reporter: "repaired.example");
        var infoOnly = AddReport(db, domain.Id, 0, [Namespace], reporter: "info-only.example");
        AddReport(db, domain.Id, 2, [], reporter: "clean.example");
        AddReport(db, domain.Id, 3, null, reporter: "old.example");
        await db.SaveChangesAsync();

        var notes = await TestAnalytics.Service(db, TestCurrentUserContext.Admin())
            .GetReportNotesAsync(domain.Id, 30, true, CancellationToken.None);

        Assert.NotNull(notes);
        Assert.True(notes.IncludeInfo);
        Assert.Equal([infoOnly.Id, repaired.Id], notes.Reports.Select(x => x.Id));
        Assert.Equal(0, notes.Reports[0].WarningCount);
    }

    /// <summary>
    /// A backfilled row's notes come from today's parser, not necessarily the one that
    /// stored the row, so the origin travels with them for the panel to say so.
    /// </summary>
    [Fact]
    public async Task BackfilledOrigin_IsPassedThrough()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db, "acme");
        AddReport(db, domain.Id, 0, [Repair], origin: DmarcReportParseOrigins.Backfill);
        await db.SaveChangesAsync();

        var notes = await TestAnalytics.Service(db, TestCurrentUserContext.Admin())
            .GetReportNotesAsync(domain.Id, 30, false, CancellationToken.None);

        Assert.Equal(DmarcReportParseOrigins.Backfill, Assert.Single(notes!.Reports).Origin);
    }

    /// <summary>
    /// The same window rule as the other analytics routes: anchored to the newest report
    /// the caller can see, and a report is inside it by its range start.
    /// </summary>
    [Fact]
    public async Task Window_AnchorsToTheNewestReport_AndExcludesOlderOnes()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db, "acme");
        var inside = AddReport(db, domain.Id, 6, [Repair], reporter: "inside.example");
        AddReport(db, domain.Id, 0, [], reporter: "newest.example");
        AddReport(db, domain.Id, 9, [Repair], reporter: "outside.example");
        AddReport(db, domain.Id, 10, null, reporter: "outside-old.example");
        await db.SaveChangesAsync();

        var notes = await TestAnalytics.Service(db, TestCurrentUserContext.Admin())
            .GetReportNotesAsync(domain.Id, 7, false, CancellationToken.None);

        Assert.NotNull(notes);
        Assert.Equal(Newest, notes.Window.EndUtc);
        Assert.Equal([inside.Id], notes.Reports.Select(x => x.Id));
        Assert.Equal(2, notes.ReportsInWindow);
        Assert.Equal(0, notes.ReportsNotRecorded);
    }

    [Fact]
    public async Task ListIsCapped_WhileTheCountsAreNot()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db, "acme");
        for (var i = 0; i < 105; i++)
        {
            AddReport(db, domain.Id, 0, [Repair], reporter: $"r{i}.example");
        }

        await db.SaveChangesAsync();

        var notes = await TestAnalytics.Service(db, TestCurrentUserContext.Admin())
            .GetReportNotesAsync(domain.Id, 30, false, CancellationToken.None);

        Assert.NotNull(notes);
        Assert.Equal(100, notes.Limit);
        Assert.Equal(100, notes.Reports.Count);
        Assert.Equal(105, notes.ReportsWithWarnings);
    }
}
