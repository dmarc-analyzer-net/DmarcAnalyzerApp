using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// The TLS half of ingestion, which had the same gap as the DMARC half and said so in its
/// own class comment: "InMemory tests cannot exercise it — the PR's manual verification
/// against Postgres is the proof". That proof happened once, by hand, and could not be
/// re-run. These are the same checks, and they run every build.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class TlsReportIngestorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid ClientId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SourceId = Guid.Parse("44444444-4444-4444-4444-444444444444");

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
            Id = SourceId, Name = "Acme TLS", Protocol = "imap", Host = "imap.example.test",
            Port = 993, UseTls = true, Username = "tls@acme.test", PasswordEncrypted = "x",
            DefaultClientId = ClientId, IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReportPoliciesFailureDetailsAndLedgerAllLandTogether()
    {
        Assert.Equal(TlsReportIngestOutcome.Inserted, await IngestAsync(Report("report-1")));

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.SmtpTlsReports.CountAsync());
        Assert.Equal(2, await db.SmtpTlsReportPolicies.CountAsync());
        Assert.Equal(1, await db.SmtpTlsFailureDetails.CountAsync());
        Assert.Equal(1, await db.TlsReportIngests.CountAsync());
    }

    [Fact]
    public async Task SameReportTwiceIsReportedDuplicateAndWritesNothingTheSecondTime()
    {
        await IngestAsync(Report("report-1"));

        Assert.Equal(TlsReportIngestOutcome.Duplicate, await IngestAsync(Report("report-1")));

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.SmtpTlsReports.CountAsync());
        Assert.Equal(2, await db.SmtpTlsReportPolicies.CountAsync());
        Assert.Equal(1, await db.TlsReportIngests.CountAsync());
    }

    [Fact]
    public async Task EachPolicyDomainBecomesADomainAndTheyAreSharedNotDuplicated()
    {
        await IngestAsync(Report("report-1"));
        await IngestAsync(Report("report-2"));

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.Domains.CountAsync(x => x.Name == "acme.test"));
        Assert.Equal(1, await db.Domains.CountAsync(x => x.Name == "mail.acme.test"));
    }

    /// <summary>
    /// Reporters control these strings, so the ingestor truncates them to the column
    /// widths. Without that a long organisation name is a 22001 from PostgreSQL and the
    /// whole report is lost — and the InMemory provider, which enforces no width at all,
    /// would never have shown it.
    /// </summary>
    [Fact]
    public async Task ReporterControlledStringsAreTruncatedRatherThanRejected()
    {
        var shouty = Report("report-1") with { OrganizationName = new string('o', 400) };

        Assert.Equal(TlsReportIngestOutcome.Inserted, await IngestAsync(shouty));

        await using var db = postgres.CreateContext();
        var stored = await db.SmtpTlsReports.SingleAsync();
        Assert.Equal(255, stored.OrganizationName.Length);
    }

    private async Task<TlsReportIngestOutcome> IngestAsync(TlsRptParseResult parsed)
    {
        await using var db = postgres.CreateContext();
        var source = await db.ReportSources.SingleAsync(x => x.Id == SourceId);
        var ingestor = new TlsReportIngestor(db, new DomainIngestResolver(db));
        return await ingestor.IngestAsync(parsed, source, CancellationToken.None);
    }

    private static TlsRptParseResult Report(string reportId) => new(
        OrganizationName: "google.com",
        ReportId: reportId,
        ContactInfo: "tls-reports@google.com",
        RangeBeginUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        RangeEndUtc: new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
        Policies:
        [
            new TlsRptPolicyParseResult("sts", "acme.test", "v=STSv1", "mx.acme.test", 100, 1,
            [
                new TlsRptFailureDetailParseResult(
                    "certificate-expired", "203.0.113.4", "mx.acme.test", "mx", "203.0.113.9", 1, null, null),
            ]),
            new TlsRptPolicyParseResult("tlsa", "mail.acme.test", null, null, 50, 0, []),
        ],
        ValidationMessages: []);
}
