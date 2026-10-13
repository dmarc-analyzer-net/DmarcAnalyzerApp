using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// The ingestion behaviour that only a real PostgreSQL can demonstrate.
/// <para>
/// Every one of these exercises something the InMemory provider cannot execute at all —
/// <c>ON CONFLICT</c>, a transaction boundary, a unique index, a column width. That gap is
/// not academic: two real bugs shipped through a green test run because the suite could
/// not reach this code, and both were then verified by hand against a database, which is
/// honest but happens once. These are the same checks, repeatable.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DmarcReportIngestorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();

        await using var db = postgres.CreateContext();
        db.Clients.Add(new Client
        {
            Id = ClientId, Name = "Acme", Slug = "acme", IsActive = true,
            RetentionMonths = 12, Timezone = "UTC",
        });
        db.ReportSources.Add(new ReportSource
        {
            Id = SourceId, Name = "Acme RUA", Protocol = "imap", Host = "imap.example.test",
            Port = 993, UseTls = true, Username = "rua@acme.test", PasswordEncrypted = "x",
            DefaultClientId = ClientId, IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReportRecordsAuthResultsAndLedgerAllLandTogether()
    {
        var outcome = await IngestAsync(Report("acme.test", "report-1"));

        Assert.Equal(DmarcReportIngestOutcome.Inserted, outcome);

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.DmarcReports.CountAsync());
        Assert.Equal(2, await db.DmarcReportRecords.CountAsync());
        Assert.Equal(1, await db.DmarcReportRecordDkimAuthResults.CountAsync());
        Assert.Equal(1, await db.DmarcReportRecordSpfAuthResults.CountAsync());
        Assert.Equal(1, await db.DmarcReportIngests.CountAsync());
    }

    [Fact]
    public async Task SameReportTwiceIsReportedDuplicateAndWritesNothingTheSecondTime()
    {
        await IngestAsync(Report("acme.test", "report-1"));
        var second = await IngestAsync(Report("acme.test", "report-1"));

        Assert.Equal(DmarcReportIngestOutcome.Duplicate, second);

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.DmarcReports.CountAsync());
        Assert.Equal(2, await db.DmarcReportRecords.CountAsync());
        Assert.Equal(1, await db.DmarcReportIngests.CountAsync());
    }

    /// <summary>
    /// The regression test for the bug this seam exists to make testable.
    /// <para>
    /// A record that violates a column width fails mid-insert. The report row must not
    /// survive that: it did once, because it had auto-committed before the records were
    /// written, and since deduplication keys on that row every later sync then saw a
    /// duplicate and skipped it. The report stayed permanently empty and silently wrong,
    /// and no retry could ever fix it. Zero and zero is the only acceptable outcome —
    /// one and zero is the bug.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFailedRecordRollsBackTheReportSoARetryCanStillSucceed()
    {
        var oversized = Report("acme.test", "report-1") with
        {
            Records =
            [
                Record() with { SourceIp = new string('9', 200) },   // SourceIp is varchar(64)
            ],
        };

        await Assert.ThrowsAnyAsync<Exception>(() => IngestAsync(oversized));

        await using (var db = postgres.CreateContext())
        {
            Assert.Equal(0, await db.DmarcReports.CountAsync());
            Assert.Equal(0, await db.DmarcReportRecords.CountAsync());
            Assert.Equal(0, await db.DmarcReportIngests.CountAsync());
        }

        // And the retry the old behaviour made impossible now works.
        Assert.Equal(DmarcReportIngestOutcome.Inserted, await IngestAsync(Report("acme.test", "report-1")));
    }

    [Fact]
    public async Task TheDomainSurvivesAFailedReportBecauseItIsNotOwnedByOne()
    {
        var oversized = Report("newdomain.test", "report-1") with
        {
            Records = [Record() with { SourceIp = new string('9', 200) }],
        };

        await Assert.ThrowsAnyAsync<Exception>(() => IngestAsync(oversized));

        await using var db = postgres.CreateContext();
        // Resolved outside the transaction on purpose: a domain is shared by every report
        // for it, so rolling it back with one failed report would be wrong.
        Assert.True(await db.Domains.AnyAsync(x => x.Name == "newdomain.test"));
    }

    [Fact]
    public async Task PolicyDomainIsNormalisedSoCasingDoesNotCreateASecondDomain()
    {
        await IngestAsync(Report("ACME.test", "report-1"));
        await IngestAsync(Report("acme.TEST", "report-2"));

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.Domains.CountAsync(x => x.Name == "acme.test"));
        Assert.Equal(2, await db.DmarcReports.CountAsync());
    }

    [Fact]
    public async Task ADifferentReportingWindowForTheSameReportIdIsNotADuplicate()
    {
        var first = Report("acme.test", "report-1");
        var later = first with
        {
            RangeBeginUtc = first.RangeBeginUtc.AddDays(1),
            RangeEndUtc = first.RangeEndUtc.AddDays(1),
        };

        Assert.Equal(DmarcReportIngestOutcome.Inserted, await IngestAsync(first));
        Assert.Equal(DmarcReportIngestOutcome.Inserted, await IngestAsync(later));

        await using var db = postgres.CreateContext();
        Assert.Equal(2, await db.DmarcReports.CountAsync());
    }

    /// <summary>
    /// The report-level RFC 9990 columns go through the raw-SQL insert, not EF, so only a
    /// real database shows they land: the column list, the jsonb cast on the error list, a
    /// null pct with no column default left to fill it, and a boolean read back as one.
    /// </summary>
    [Fact]
    public async Task Rfc9990ReportLevelFieldsRoundTripThroughTheRawInsert()
    {
        var v2 = Report("acme.test", "report-v2") with
        {
            PublishedPct = null,
            ReportFormat = DmarcReportFormats.Rfc9990,
            NonexistentSubdomainPolicy = "reject",
            Testing = true,
            DiscoveryMethod = "treewalk",
            Generator = "Example Reporter v1.2",
            ReportErrors = ["DNS timeout", "second error"],
        };

        Assert.Equal(DmarcReportIngestOutcome.Inserted, await IngestAsync(v2));
        Assert.Equal(DmarcReportIngestOutcome.Inserted, await IngestAsync(Report("acme.test", "report-v1")));

        await using var db = postgres.CreateContext();
        var stored = await db.DmarcReports.SingleAsync(x => x.ReportId == "report-v2");
        Assert.Null(stored.PublishedPct);
        Assert.Equal(DmarcReportFormats.Rfc9990, stored.ReportFormat);
        Assert.Equal("reject", stored.NonexistentSubdomainPolicy);
        Assert.True(stored.Testing);
        Assert.Equal("treewalk", stored.DiscoveryMethod);
        Assert.Equal("Example Reporter v1.2", stored.Generator);
        Assert.Equal(["DNS timeout", "second error"], stored.ReportErrors);

        // An RFC 7489 report that sent none of them stores null, not a default, for each.
        var v1 = await db.DmarcReports.SingleAsync(x => x.ReportId == "report-v1");
        Assert.Equal(100, v1.PublishedPct);
        Assert.Equal(DmarcReportFormats.Rfc7489, v1.ReportFormat);
        Assert.Null(v1.NonexistentSubdomainPolicy);
        Assert.Null(v1.Testing);
        Assert.Null(v1.DiscoveryMethod);
        Assert.Null(v1.Generator);
        Assert.Null(v1.ReportErrors);
    }

    /// <summary>
    /// The parser's messages go through the raw insert as JSON, with their warning count
    /// and origin. A clean parse stores an empty list, not null: null is "not recorded", and
    /// only rows from before the column have it.
    /// </summary>
    [Fact]
    public async Task ParseMessagesRoundTripThroughTheRawInsert()
    {
        var noisy = Report("acme.test", "report-noisy") with
        {
            ValidationMessages =
            [
                DmarcReportParseMessage.Info("stripped XML namespace 'urn:ietf:params:xml:ns:dmarc-2.0' for compatibility"),
                DmarcReportParseMessage.Warning("replaced unrecognised policy_evaluated/dkim value 'x' with 'fail' for compatibility"),
                DmarcReportParseMessage.Error("The element 'feedback' has invalid child element 'extra'."),
            ],
        };

        Assert.Equal(DmarcReportIngestOutcome.Inserted, await IngestAsync(noisy));
        Assert.Equal(DmarcReportIngestOutcome.Inserted, await IngestAsync(Report("acme.test", "report-clean")));

        await using var db = postgres.CreateContext();
        var stored = await db.DmarcReports.SingleAsync(x => x.ReportId == "report-noisy");
        Assert.NotNull(stored.ParseMessages);
        Assert.Equal(noisy.ValidationMessages, DmarcReportParseMessagesJson.Deserialize(stored.ParseMessages));
        Assert.Equal(2, stored.ParseWarningCount);
        Assert.Equal(DmarcReportParseOrigins.Ingest, stored.ParseMessagesOrigin);

        var clean = await db.DmarcReports.SingleAsync(x => x.ReportId == "report-clean");
        Assert.Equal("[]", clean.ParseMessages);
        Assert.Equal(0, clean.ParseWarningCount);
        Assert.Equal(DmarcReportParseOrigins.Ingest, clean.ParseMessagesOrigin);
    }

    private async Task<DmarcReportIngestOutcome> IngestAsync(DmarcReportParseResult parsed)
    {
        await using var db = postgres.CreateContext();
        var source = await db.ReportSources.SingleAsync(x => x.Id == SourceId);
        var ingestor = new DmarcReportIngestor(db, new DomainIngestResolver(db));
        return await ingestor.IngestAsync(parsed, source, CancellationToken.None);
    }

    private static DmarcReportParseResult Report(string policyDomain, string reportId) => new(
        OrganizationName: "google.com",
        ReportId: reportId,
        RangeBeginUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        RangeEndUtc: new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
        PolicyDomain: policyDomain,
        RecordCount: 2,
        Records:
        [
            Record() with
            {
                DkimAuthResults = [new DmarcReportRecordDkimAuthParseResult("acme.test", "s1", "pass", "ok")],
                SpfAuthResults = [new DmarcReportRecordSpfAuthParseResult("acme.test", "mfrom", "pass", "ok")],
            },
            Record() with { SourceIp = "203.0.113.9" },
        ],
        HasValidationWarnings: false,
        HasValidationErrors: false,
        ValidationMessages: [],
        PublishedPolicy: "none",
        SubdomainPolicy: null,
        PublishedPct: 100,
        DkimAlignment: "relaxed",
        SpfAlignment: "relaxed",
        ReportFormat: DmarcReportFormats.Rfc7489,
        NonexistentSubdomainPolicy: null,
        Testing: null,
        DiscoveryMethod: null,
        Generator: null,
        ReportErrors: []);

    private static DmarcReportRecordParseResult Record() => new(
        SourceIp: "203.0.113.4",
        MessageCount: 5,
        Disposition: "none",
        DkimResult: "pass",
        SpfResult: "pass",
        HeaderFrom: "acme.test",
        EnvelopeFrom: "acme.test",
        EnvelopeTo: "example.test",
        DkimAuthResults: [],
        SpfAuthResults: []);
}
