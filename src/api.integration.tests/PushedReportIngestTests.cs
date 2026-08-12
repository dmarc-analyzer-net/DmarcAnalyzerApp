using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
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
/// Pushed ingestion, end to end against the real database — the same extractor, parsers and
/// ingestors the mailbox worker uses, reached over HTTP instead of IMAP.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PushedReportIngestTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid ClientId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid SourceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

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
            Id = SourceId, Name = "Bifrost", Protocol = "api",
            Host = string.Empty, Port = 0, UseTls = false,
            Username = string.Empty, PasswordEncrypted = string.Empty,
            DefaultClientId = ClientId, IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AGzippedReportIsStoredThroughTheSamePathAsAMailboxAttachment()
    {
        var result = await IngestAsync(Gzip(ReportXml("push-1")), "report.xml.gz", "application/gzip");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.Replay);
        Assert.Equal(1, result.Value.Inserted);

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.DmarcReports.CountAsync());
        Assert.Equal(1, await db.DmarcReportRecords.CountAsync());
        // The ledger row proves it went through the ingestor rather than a private copy.
        Assert.Equal(1, await db.DmarcReportIngests.CountAsync());
    }

    [Fact]
    public async Task AnIdenticalRepostIsAnswerdAsAReplayWithoutReparsing()
    {
        var body = Gzip(ReportXml("push-1"));

        await IngestAsync(body, "report.xml.gz", "application/gzip");
        var second = await IngestAsync(body, "report.xml.gz", "application/gzip");

        // The caller retried; it needs "yes, I have that", not a payload full of duplicates
        // that it cannot tell apart from a genuinely stale post.
        Assert.True(second.Value!.Replay);
        Assert.Empty(second.Value.Payloads);

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.ReportIngestReceipts.CountAsync());
    }

    [Fact]
    public async Task TheSameReportPostedAsDifferentBytesIsADuplicateNotAReplay()
    {
        // Two different gzip encodings of the same report: the transport hash differs, so
        // the receipt does not match, and it is the report-level dedup that catches it.
        await IngestAsync(Gzip(ReportXml("push-1")), "a.gz", "application/gzip");
        var second = await IngestAsync(Encoding.UTF8.GetBytes(ReportXml("push-1")), "a.xml", "application/xml");

        Assert.False(second.Value!.Replay);
        Assert.Equal(0, second.Value.Inserted);
        Assert.Equal(1, second.Value.Duplicate);

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.DmarcReports.CountAsync());
    }

    [Fact]
    public async Task AFailedPayloadWritesNoReceiptSoTheCallerCanRetry()
    {
        var result = await IngestAsync(Encoding.UTF8.GetBytes("not a report at all"), "x.txt", "text/plain");

        Assert.False(result.IsSuccess);

        await using var db = postgres.CreateContext();
        // Recording a receipt here would turn a transient failure into a permanent one:
        // the retry would be answered "already have it" and the report lost for good.
        Assert.Equal(0, await db.ReportIngestReceipts.CountAsync());
    }

    [Fact]
    public async Task ABombIsRefusedWithTheLimitNamedAndNothingIsStored()
    {
        var bomb = Gzip(new byte[200 * 1024 * 1024]);

        var result = await IngestAsync(bomb, "bomb.gz", "application/gzip");

        Assert.False(result.IsSuccess);
        Assert.Equal(413, result.StatusCode);
        Assert.Contains("MaxReportEntryBytes", result.Error);

        await using var db = postgres.CreateContext();
        Assert.Equal(0, await db.ReportIngestReceipts.CountAsync());
    }

    [Fact]
    public async Task TheReceiptIsScopedToTheSourceSoTwoSourcesCanReceiveTheSameBytes()
    {
        var otherSource = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        await using (var db = postgres.CreateContext())
        {
            db.ReportSources.Add(new ReportSource
            {
                Id = otherSource, Name = "Second", Protocol = "api",
                Host = string.Empty, Port = 0, UseTls = false,
                Username = string.Empty, PasswordEncrypted = string.Empty,
                DefaultClientId = ClientId, IsActive = true,
            });
            await db.SaveChangesAsync();
        }

        var body = Gzip(ReportXml("push-1"));
        var first = await IngestAsync(body, "r.gz", "application/gzip");
        var second = await IngestAsync(body, "r.gz", "application/gzip", otherSource);

        Assert.False(first.Value!.Replay);
        Assert.False(second.Value!.Replay);   // a different source, so not a replay

        await using var check = postgres.CreateContext();
        Assert.Equal(2, await check.ReportIngestReceipts.CountAsync());
    }

    [Fact]
    public async Task TheRecordedHashIsTheHashOfTheBytesAsPosted()
    {
        var body = Gzip(ReportXml("push-1"));
        var expected = Convert.ToHexStringLower(SHA256.HashData(body));

        var result = await IngestAsync(body, "r.gz", "application/gzip");

        Assert.Equal(expected, result.Value!.PayloadSha256);

        await using var db = postgres.CreateContext();
        Assert.Equal(expected, (await db.ReportIngestReceipts.SingleAsync()).PayloadSha256);
    }

    private async Task<Application.Common.ServiceResult<PushedReportResult>> IngestAsync(
        byte[] body, string fileName, string contentType, Guid? sourceId = null, string? provenance = null)
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

        return await service.IngestAsync(sourceId ?? SourceId, body, fileName, contentType, provenance, CancellationToken.None);
    }

    private static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private static byte[] Gzip(string content) => Gzip(Encoding.UTF8.GetBytes(content));

    private static string ReportXml(string reportId) =>
        $"""
        <?xml version="1.0"?>
        <feedback>
          <report_metadata>
            <org_name>google.com</org_name>
            <report_id>{reportId}</report_id>
            <date_range><begin>1754006400</begin><end>1754092800</end></date_range>
          </report_metadata>
          <policy_published><domain>acme.test</domain><p>none</p><pct>100</pct></policy_published>
          <record>
            <row>
              <source_ip>203.0.113.4</source_ip><count>5</count>
              <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
            </row>
            <identifiers><header_from>acme.test</header_from></identifiers>
            <auth_results>
              <dkim><domain>acme.test</domain><result>pass</result></dkim>
              <spf><domain>acme.test</domain><result>pass</result></spf>
            </auth_results>
          </record>
        </feedback>
        """;
}
