using System.IO.Compression;
using System.Text;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// The cases issue #156 asked to see covered before an upload endpoint existed: tenant
/// isolation, cross-domain report ids, and corrupt archives.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class IngestionTenancyAndMalformedInputTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid ClientA = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
    private static readonly Guid ClientB = Guid.Parse("0b000000-0000-0000-0000-00000000000b");
    private static readonly Guid SourceOfA = Guid.Parse("05000000-0000-0000-0000-000000000005");
    private static readonly Guid DomainOfB = Guid.Parse("0d000000-0000-0000-0000-00000000000d");

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();

        await using var db = postgres.CreateContext();
        db.Clients.Add(Client(ClientA, "A", "a"));
        db.Clients.Add(Client(ClientB, "B", "b"));
        db.Domains.Add(new Domain { Id = DomainOfB, ClientId = ClientB, Name = "owned-by-b.test", IsActive = true });
        db.ReportSources.Add(new ReportSource
        {
            Id = SourceOfA, Name = "A's source", Protocol = "api",
            Host = string.Empty, Port = 0, UseTls = false,
            Username = string.Empty, PasswordEncrypted = string.Empty,
            DefaultClientId = ClientA, IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A report arriving through one client's source, for a domain another client already
    /// owns, is stored against the domain's owner — not the ingesting source's client.
    /// <para>
    /// This is the documented routing rule ("by policy domain map; source has default
    /// client fallback for unmatched domains") and it is what lets an agency poll one
    /// shared mailbox for many clients. Asserted here because it is the tenancy question
    /// a machine credential makes sharper: the credential resolves the source, but the
    /// domain decides whose data this becomes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AReportForAnotherClientsDomainIsStoredAgainstThatClientNotTheIngestingOne()
    {
        await IngestAsync(Report("owned-by-b.test", "x-1"));

        await using var db = postgres.CreateContext();

        // No second domain: the resolver returns the existing row rather than claiming it.
        Assert.Equal(1, await db.Domains.CountAsync(x => x.Name == "owned-by-b.test"));
        Assert.Equal(ClientB, (await db.Domains.SingleAsync(x => x.Name == "owned-by-b.test")).ClientId);

        // Tenancy for a report is transitive through its domain, so this is B's data and
        // A's viewers never see it.
        var report = await db.DmarcReports.SingleAsync();
        Assert.Equal(DomainOfB, report.DomainId);
    }

    /// <summary>
    /// The ledger, by contrast, records the client the <em>source</em> belongs to. The two
    /// disagree for a cross-client report, and this test exists to pin that down rather
    /// than to bless it: the report is B's and the ledger row says A.
    /// </summary>
    [Fact]
    public async Task TheIngestLedgerRecordsTheSourcesClientEvenWhenTheDomainBelongsToAnother()
    {
        await IngestAsync(Report("owned-by-b.test", "x-1"));

        await using var db = postgres.CreateContext();
        Assert.Equal(ClientA, (await db.DmarcReportIngests.SingleAsync()).ClientId);
    }

    /// <summary>
    /// Two clients' domains using the same report id must not collide. The dedup key
    /// starts with DomainId, so they are separate reports — but "should be" is what this
    /// harness exists to replace.
    /// </summary>
    [Fact]
    public async Task TheSameReportIdForTwoDifferentDomainsIsTwoReports()
    {
        await IngestAsync(Report("owned-by-b.test", "shared-id"));
        await IngestAsync(Report("owned-by-a.test", "shared-id"));

        await using var db = postgres.CreateContext();
        Assert.Equal(2, await db.DmarcReports.CountAsync());
        Assert.Equal(2, await db.DmarcReports.Select(x => x.DomainId).Distinct().CountAsync());
    }

    [Fact]
    public async Task TheSameReportIdForTheSameDomainIsStillOneReport()
    {
        await IngestAsync(Report("owned-by-a.test", "shared-id"));
        await IngestAsync(Report("owned-by-a.test", "shared-id"));

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.DmarcReports.CountAsync());
    }

    /// <summary>
    /// Found by writing this test: a truncated gzip used to be ingested as a valid report.
    /// See <c>ReportPayloadIngestor.EnsureWellFormed</c> for why that was worse than
    /// losing the upload.
    /// </summary>
    [Fact]
    public async Task ATruncatedGzipIsRefusedWithoutStoringAnythingOrRecordingAReceipt()
    {
        var whole = Gzip(Encoding.UTF8.GetBytes(ReportXml("owned-by-a.test", "x-1")));
        var truncated = whole[..(whole.Length / 2)];

        var result = await PushAsync(truncated, "report.xml.gz", "application/gzip");

        Assert.False(result.IsSuccess);

        await using var db = postgres.CreateContext();
        // Nothing stored is the whole point. A truncated payload parses into a report
        // carrying the real report id and window with no records, and deduplication keys
        // on exactly those — so storing it would reject the complete report as a duplicate
        // when it arrived, permanently.
        Assert.Equal(0, await db.DmarcReports.CountAsync());
        // Retryable: a damaged upload must not be remembered as delivered.
        Assert.Equal(0, await db.ReportIngestReceipts.CountAsync());
    }

    [Fact]
    public async Task AZipWithACorruptEntryStillIngestsTheEntriesThatAreIntact()
    {
        // A multi-entry archive where one member is damaged. The intact report must still
        // land — dropping a good report because a neighbour was corrupt would lose data
        // for a reason the sender cannot see or fix.
        var zip = ZipWithCorruptSecondEntry(ReportXml("owned-by-a.test", "good-1"));

        var result = await PushAsync(zip, "reports.zip", "application/zip");

        await using var db = postgres.CreateContext();
        Assert.True(result.IsSuccess);
        Assert.Equal(1, await db.DmarcReports.CountAsync());
    }

    private async Task IngestAsync(DmarcReportParseResult parsed)
    {
        await using var db = postgres.CreateContext();
        var source = await db.ReportSources.SingleAsync(x => x.Id == SourceOfA);
        await new DmarcReportIngestor(db, new DomainIngestResolver(db))
            .IngestAsync(parsed, source, CancellationToken.None);
    }

    private async Task<Application.Common.ServiceResult<PushedReportResult>> PushAsync(
        byte[] body, string fileName, string contentType, string? provenance = null)
    {
        await using var db = postgres.CreateContext();
        var service = new PushedReportIngestService(
            db,
            new ReportPayloadIngestor(
                new DmarcRuaReportParser(),
                new TlsRptReportParser(),
                new DmarcReportIngestor(db, new DomainIngestResolver(db)),
                new TlsReportIngestor(db, new DomainIngestResolver(db))),
            Options.Create(new WorkerOptions()),
            NullLogger<PushedReportIngestService>.Instance);

        return await service.IngestAsync(SourceOfA, body, fileName, contentType, provenance, CancellationToken.None);
    }

    private static Client Client(Guid id, string name, string slug) => new()
    {
        Id = id, Name = name, Slug = slug, IsActive = true, RetentionMonths = 12, Timezone = "UTC",
    };

    private static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private static byte[] ZipWithCorruptSecondEntry(string goodXml)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var good = archive.CreateEntry("good.xml", CompressionLevel.Optimal).Open())
            {
                good.Write(Encoding.UTF8.GetBytes(goodXml));
            }

            using var bad = archive.CreateEntry("bad.xml", CompressionLevel.Optimal).Open();
            bad.Write(Encoding.UTF8.GetBytes("<feedback><this is not"));
        }

        return buffer.ToArray();
    }

    private static DmarcReportParseResult Report(string policyDomain, string reportId) => new(
        "google.com", reportId,
        new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
        policyDomain, 1,
        [new DmarcReportRecordParseResult("203.0.113.4", 1, "none", "pass", "pass", policyDomain, policyDomain, "x.test", [], [])],
        false, false, [], "none", null, 100, "relaxed", "relaxed");

    private static string ReportXml(string policyDomain, string reportId) =>
        $"""
        <?xml version="1.0"?>
        <feedback>
          <report_metadata>
            <org_name>google.com</org_name><report_id>{reportId}</report_id>
            <date_range><begin>1754006400</begin><end>1754092800</end></date_range>
          </report_metadata>
          <policy_published><domain>{policyDomain}</domain><p>none</p><pct>100</pct></policy_published>
          <record>
            <row><source_ip>203.0.113.4</source_ip><count>1</count>
              <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
            </row>
            <identifiers><header_from>{policyDomain}</header_from></identifiers>
            <auth_results>
              <dkim><domain>{policyDomain}</domain><result>pass</result></dkim>
              <spf><domain>{policyDomain}</domain><result>pass</result></spf>
            </auth_results>
          </record>
        </feedback>
        """;
}
