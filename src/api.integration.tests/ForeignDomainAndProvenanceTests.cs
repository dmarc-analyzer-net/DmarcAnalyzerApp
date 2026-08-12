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
/// The per-source foreign-domain switch, and the provenance a caller may attach.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ForeignDomainAndProvenanceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid ClientA = Guid.Parse("1a000000-0000-0000-0000-00000000000a");
    private static readonly Guid ClientB = Guid.Parse("1b000000-0000-0000-0000-00000000000b");
    private static readonly Guid PermissiveSource = Guid.Parse("15000000-0000-0000-0000-000000000001");
    private static readonly Guid RestrictedSource = Guid.Parse("15000000-0000-0000-0000-000000000002");

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();

        await using var db = postgres.CreateContext();
        db.Clients.Add(Client(ClientA, "A", "a"));
        db.Clients.Add(Client(ClientB, "B", "b"));
        db.Domains.Add(new Domain { Id = Guid.NewGuid(), ClientId = ClientB, Name = "owned-by-b.test", IsActive = true });
        db.ReportSources.Add(Source(PermissiveSource, "permissive", allowForeign: true));
        db.ReportSources.Add(Source(RestrictedSource, "restricted", allowForeign: false));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The default, and what every source created before this switch existed does. It is
    /// what makes one shared mailbox usable for an agency with many clients.
    /// </summary>
    [Fact]
    public async Task APermissiveSourceStillIngestsForAnotherClientsDomain()
    {
        var outcome = await IngestAsync(PermissiveSource, Report("owned-by-b.test", "x-1"));

        Assert.Equal(DmarcReportIngestOutcome.Inserted, outcome);

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.DmarcReports.CountAsync());
    }

    [Fact]
    public async Task ARestrictedSourceRefusesAndStoresNothing()
    {
        var outcome = await IngestAsync(RestrictedSource, Report("owned-by-b.test", "x-1"));

        Assert.Equal(DmarcReportIngestOutcome.ForeignDomainRefused, outcome);

        await using var db = postgres.CreateContext();
        Assert.Equal(0, await db.DmarcReports.CountAsync());
        // Refused before the transaction opens, so no ledger row claims a report that is
        // not there.
        Assert.Equal(0, await db.DmarcReportIngests.CountAsync());
    }

    [Fact]
    public async Task ARestrictedSourceStillIngestsForItsOwnClientsDomain()
    {
        var outcome = await IngestAsync(RestrictedSource, Report("owned-by-a.test", "x-1"));

        Assert.Equal(DmarcReportIngestOutcome.Inserted, outcome);
    }

    /// <summary>
    /// A domain nobody owns yet is created under the resolving source's client, so it is
    /// never foreign — restricting a source must not stop it discovering its own domains.
    /// </summary>
    [Fact]
    public async Task ARestrictedSourceStillCreatesDomainsThatDoNotExistYet()
    {
        var outcome = await IngestAsync(RestrictedSource, Report("brand-new.test", "x-1"));

        Assert.Equal(DmarcReportIngestOutcome.Inserted, outcome);

        await using var db = postgres.CreateContext();
        Assert.Equal(ClientA, (await db.Domains.SingleAsync(x => x.Name == "brand-new.test")).ClientId);
    }

    [Fact]
    public async Task ExistingSourcesDefaultToPermissiveSoBehaviourIsUnchanged()
    {
        await using var db = postgres.CreateContext();
        // The column default is what an install upgrading into this migration gets.
        db.ReportSources.Add(new ReportSource
        {
            Id = Guid.NewGuid(), Name = "as-if-migrated", Protocol = "imap",
            Host = "imap.example.test", Port = 993, UseTls = true,
            Username = "u", PasswordEncrypted = "x", DefaultClientId = ClientA, IsActive = true,
        });
        await db.SaveChangesAsync();

        var stored = await db.ReportSources.SingleAsync(x => x.Name == "as-if-migrated");
        Assert.True(stored.AllowForeignDomains);
    }

    [Fact]
    public async Task ProvenanceIsStoredVerbatimWithItsDeclaredVersion()
    {
        const string provenance = """{"v":1,"tenant":"contoso.onmicrosoft.com","mailbox":"dmarc@contoso.com"}""";

        var result = await PushAsync(Gzip(ReportXml("owned-by-a.test", "p-1")), provenance);

        Assert.True(result.IsSuccess);

        await using var db = postgres.CreateContext();
        var receipt = await db.ReportIngestReceipts.SingleAsync();
        Assert.Equal(1, receipt.ProvenanceVersion);
        Assert.Contains("contoso.onmicrosoft.com", receipt.Provenance);
    }

    [Fact]
    public async Task ProvenanceIsOptional()
    {
        var result = await PushAsync(Gzip(ReportXml("owned-by-a.test", "p-1")), provenance: null);

        Assert.True(result.IsSuccess);

        await using var db = postgres.CreateContext();
        var receipt = await db.ReportIngestReceipts.SingleAsync();
        Assert.Null(receipt.Provenance);
        Assert.Null(receipt.ProvenanceVersion);
    }

    [Theory]
    [InlineData("not json at all", "not valid JSON")]
    [InlineData("""["v",1]""", "must be a JSON object")]
    [InlineData("""{"tenant":"contoso"}""", "declaring its shape")]
    [InlineData("""{"v":"one"}""", "declaring its shape")]
    public async Task MalformedProvenanceIsRefusedRatherThanDropped(string provenance, string expected)
    {
        // Refused, not ignored: silently dropping it would leave the caller believing the
        // origin was recorded, and provenance only earns its keep if it is there later.
        var result = await PushAsync(Gzip(ReportXml("owned-by-a.test", "p-1")), provenance);

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
        Assert.Contains(expected, result.Error);

        await using var db = postgres.CreateContext();
        Assert.Equal(0, await db.DmarcReports.CountAsync());
    }

    [Fact]
    public async Task OversizedProvenanceIsRefused()
    {
        var huge = $$"""{"v":1,"note":"{{new string('x', 5000)}}"}""";

        var result = await PushAsync(Gzip(ReportXml("owned-by-a.test", "p-1")), huge);

        Assert.False(result.IsSuccess);
        Assert.Contains("label, not a payload", result.Error);
    }

    private async Task<DmarcReportIngestOutcome> IngestAsync(Guid sourceId, DmarcReportParseResult parsed)
    {
        await using var db = postgres.CreateContext();
        var source = await db.ReportSources.SingleAsync(x => x.Id == sourceId);
        return await new DmarcReportIngestor(db, new DomainIngestResolver(db))
            .IngestAsync(parsed, source, CancellationToken.None);
    }

    private async Task<Application.Common.ServiceResult<PushedReportResult>> PushAsync(
        byte[] body, string? provenance)
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

        return await service.IngestAsync(
            PermissiveSource, body, "report.xml.gz", "application/gzip", provenance, CancellationToken.None);
    }

    private static Client Client(Guid id, string name, string slug) => new()
    {
        Id = id, Name = name, Slug = slug, IsActive = true, RetentionMonths = 12, Timezone = "UTC",
    };

    private static ReportSource Source(Guid id, string name, bool allowForeign) => new()
    {
        Id = id, Name = name, Protocol = "api", Host = string.Empty, Port = 0, UseTls = false,
        Username = string.Empty, PasswordEncrypted = string.Empty,
        DefaultClientId = ClientA, IsActive = true, AllowForeignDomains = allowForeign,
    };

    private static byte[] Gzip(string content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(content));
        }

        return output.ToArray();
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
