using System.IO.Compression;
using System.Text;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// The report backfill end to end against a real database, with an in-memory bucket.
/// <para>
/// The unit suite proves the logic on InMemory; this proves the parts InMemory cannot: the
/// apply pass's grouped <c>COUNT(DISTINCT)</c> translating, the staging rows' jsonb columns
/// round-tripping, <c>ExecuteDelete</c> clearing them, and the report update going through
/// EF's converter for <c>ReportErrors</c> next to the raw-SQL insert that wrote the row.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ReportBackfillPostgresTests(PostgresFixture postgres)
{
    private static readonly Guid ClientId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid SourceId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly DateTime Day = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private const long RangeBegin = 1759708800;
    private const long RangeEnd = 1759795199;

    private sealed class InMemoryObjectStorage : IObjectStorage
    {
        public SortedDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public bool IsConfigured => true;
        public string Describe() => "in-memory";
        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct) { Objects[key] = content; return Task.CompletedTask; }
        public Task<long?> GetLengthAsync(string key, CancellationToken ct) => Task.FromResult(Objects.TryGetValue(key, out var v) ? v.Length : (long?)null);
        public Task<byte[]?> GetAsync(string key, CancellationToken ct) => Task.FromResult(Objects.TryGetValue(key, out var v) ? v : null);
        public Task CopyAsync(string sourceKey, string destinationKey, CancellationToken ct) => Task.CompletedTask;
        public Task<ObjectStorageVersioning> GetVersioningAsync(CancellationToken ct) => Task.FromResult(ObjectStorageVersioning.Unknown);
        public Task<ObjectListing> ListAsync(string prefix, string? startAfter, string? continuationToken, CancellationToken ct)
            => Task.FromResult(new ObjectListing(
                Objects.Keys
                    .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && (startAfter is null || string.CompareOrdinal(k, startAfter) > 0))
                    .Select(k => new StoredObject(k, Objects[k].Length, DateTime.UtcNow))
                    .ToList(),
                null));
    }

    private static ReportBackfillService Service(DmarcAnalyzerDbContext db, IObjectStorage storage) => new(
        db, storage, new DmarcRuaReportParser(),
        Options.Create(new BackupOptions { Bucket = "bucket", Prefix = "dmarc" }),
        Options.Create(new WorkerOptions()),
        NullLogger<ReportBackfillService>.Instance);

    private static string ReportXml(string reportId, bool rfc9990, string? np = null, string? error = null)
    {
        var ns = rfc9990 ? " xmlns=\"urn:ietf:params:xml:ns:dmarc-2.0\"" : string.Empty;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback{ns}>
              <report_metadata>
                <org_name>reporter.example</org_name>
                <report_id>{reportId}</report_id>
                <date_range><begin>{RangeBegin}</begin><end>{RangeEnd}</end></date_range>
                {(error is null ? string.Empty : $"<error>{error}</error>")}
              </report_metadata>
              <policy_published><domain>acme.example</domain><p>none</p>{(np is null ? string.Empty : $"<np>{np}</np>")}{(rfc9990 ? string.Empty : "<pct>100</pct>")}</policy_published>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>1</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>acme.example</header_from></identifiers>
                <auth_results><spf><domain>acme.example</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;
    }

    private static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private static byte[] ArchivedMail(string xml)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("reporter", "noreply@reporter.example"));
        message.To.Add(new MailboxAddress("dmarc", "dmarc@acme.example"));
        message.Subject = "Report domain: acme.example";
        message.Date = Day;
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "attached" },
            new MimePart("application", "gzip")
            {
                Content = new MimeContent(new MemoryStream(Gzip(Encoding.UTF8.GetBytes(xml)))),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = "report.xml.gz",
            },
        };

        using var raw = new MemoryStream();
        message.WriteTo(raw);
        return Gzip(raw.ToArray());
    }

    private static DmarcReport StoredReport(Guid domainId, string reportId) => new()
    {
        Id = Guid.NewGuid(), DomainId = domainId, ReportSourceId = SourceId,
        OrganizationName = "reporter.example", ReportId = reportId,
        RangeBeginUtc = DateTimeOffset.FromUnixTimeSeconds(RangeBegin).UtcDateTime,
        RangeEndUtc = DateTimeOffset.FromUnixTimeSeconds(RangeEnd).UtcDateTime,
        RecordCount = 1, IngestedAtUtc = Day, PublishedPolicy = "none", PublishedPct = 100,
    };

    [Fact]
    public async Task CollectsStagesAndApplies_AgainstPostgres()
    {
        await postgres.ResetAsync();
        Guid v1Id, v2Id, conflictId;
        await using (var db = postgres.CreateContext())
        {
            var client = new Client { Id = ClientId, Name = "Acme", Slug = "acme", IsActive = true, RetentionMonths = 12, Timezone = "UTC" };
            var domain = new Domain { Id = Guid.NewGuid(), ClientId = ClientId, Name = "acme.example", IsActive = true };
            var source = new ReportSource
            {
                Id = SourceId, Name = "Acme RUA", Protocol = "imap", Host = "imap.example.test",
                Port = 993, UseTls = true, Username = "rua@acme.test", PasswordEncrypted = "x",
                DefaultClientId = ClientId, IsActive = true,
            };
            var v1 = StoredReport(domain.Id, "r-v1");
            var v2 = StoredReport(domain.Id, "r-v2");
            var conflict = StoredReport(domain.Id, "r-conflict");
            (v1Id, v2Id, conflictId) = (v1.Id, v2.Id, conflict.Id);
            db.AddRange(client, domain, source, v1, v2, conflict);
            await db.SaveChangesAsync();
        }

        var storage = new InMemoryObjectStorage();
        string Key(string uid, DateTime? day = null) => ReportMailArchive.Key("dmarc", SourceId, new ReportMailIdentity("1", uid), day ?? Day);
        storage.Objects[Key("1")] = ArchivedMail(ReportXml("r-v1", rfc9990: false, np: "reject", error: "DNS timeout"));
        storage.Objects[Key("2")] = ArchivedMail(ReportXml("r-v2", rfc9990: true));
        storage.Objects[Key("3", Day)] = ArchivedMail(ReportXml("r-conflict", rfc9990: false, np: "reject"));
        storage.Objects[Key("4", Day.AddDays(1))] = ArchivedMail(ReportXml("r-conflict", rfc9990: false));

        await using (var db = postgres.CreateContext())
        {
            var service = Service(db, storage);
            var requested = await service.RequestRunAsync(CancellationToken.None);
            Assert.True(requested.IsSuccess, requested.Error);
            Assert.True(await service.RunIfRequestedAsync(CancellationToken.None));
        }

        await using (var db = postgres.CreateContext())
        {
            var state = await db.ReportBackfillStates.SingleAsync();
            Assert.Equal(ReportBackfillStatuses.Completed, state.Status);
            Assert.Equal(4, state.ObjectsSeen);
            Assert.Equal(4, state.Matched);
            Assert.Equal(2, state.Updated);
            Assert.Equal(1, state.Ambiguous);
            Assert.Equal(0, state.Failed);
            Assert.Null(state.LastError);

            var v1 = await db.DmarcReports.SingleAsync(x => x.Id == v1Id);
            Assert.Equal(DmarcReportFormats.Rfc7489, v1.ReportFormat);
            Assert.Equal("reject", v1.NonexistentSubdomainPolicy);
            Assert.Equal(["DNS timeout"], v1.ReportErrors);
            Assert.Equal(100, v1.PublishedPct);
            Assert.Equal(DmarcReportParseOrigins.Backfill, v1.ParseMessagesOrigin);
            Assert.NotNull(v1.ParseMessagesRecordedAtUtc);
            Assert.Equal([], DmarcReportParseMessagesJson.Deserialize(v1.ParseMessages!));

            var v2 = await db.DmarcReports.SingleAsync(x => x.Id == v2Id);
            Assert.Equal(DmarcReportFormats.Rfc9990, v2.ReportFormat);
            Assert.Null(v2.PublishedPct);
            Assert.Equal(0, v2.ParseWarningCount);
            Assert.Contains(DmarcReportParseMessagesJson.Deserialize(v2.ParseMessages!), m => m.Severity == DmarcReportParseSeverity.Info);

            var conflict = await db.DmarcReports.SingleAsync(x => x.Id == conflictId);
            Assert.Null(conflict.ReportFormat);
            Assert.Null(conflict.ParseMessages);

            Assert.Equal(0, await db.ReportBackfillCandidates.CountAsync());
        }
    }
}
