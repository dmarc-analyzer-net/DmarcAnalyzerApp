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

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The report backfill (PR 4 of docs/planning/rfc-9990-plan.md) over a fake bucket: the
/// archive walk, matching by dedup key and source, the two-pass rule that nothing is
/// written until every copy has been seen and they agree, resume from the saved key, and
/// that a bad object is counted rather than fatal.
/// </summary>
public sealed class ReportBackfillTests
{
    private static readonly Guid SourceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SourceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime Day1 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    private const long RangeBegin = 1759708800; // 2026-10-06T00:00:00Z
    private const long RangeEnd = 1759795199;

    /// <summary>A bucket that lists in key order, pages, and can be told to fail on one key.</summary>
    private sealed class FakeStorage : IObjectStorage
    {
        public SortedDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public List<(string Prefix, string? StartAfter, string? Token)> Listings { get; } = [];
        public int PageSize { get; set; } = 1000;
        public string? ThrowWhenFetching { get; set; }
        public bool ThrowOnSecondPage { get; set; }
        public HashSet<string> Missing { get; } = [];

        public bool IsConfigured { get; set; } = true;
        public string Describe() => "fake://bucket";

        public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
        {
            Objects[key] = content;
            return Task.CompletedTask;
        }

        public Task<long?> GetLengthAsync(string key, CancellationToken ct)
            => Task.FromResult(Objects.TryGetValue(key, out var v) ? v.Length : (long?)null);

        public Task<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            if (key == ThrowWhenFetching)
            {
                throw new InvalidOperationException("bucket unreachable");
            }

            return Task.FromResult(Missing.Contains(key) || !Objects.TryGetValue(key, out var v) ? null : v);
        }

        public Task CopyAsync(string sourceKey, string destinationKey, CancellationToken ct)
            => Task.CompletedTask;

        public Task<ObjectStorageVersioning> GetVersioningAsync(CancellationToken ct)
            => Task.FromResult(ObjectStorageVersioning.Unknown);

        public Task<ObjectListing> ListAsync(string prefix, string? startAfter, string? continuationToken, CancellationToken ct)
        {
            Listings.Add((prefix, startAfter, continuationToken));
            if (ThrowOnSecondPage && continuationToken is not null)
            {
                throw new InvalidOperationException("second page unavailable");
            }

            var keys = Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            var start = continuationToken is not null
                ? int.Parse(continuationToken)
                : startAfter is null ? 0 : keys.Count(k => string.CompareOrdinal(k, startAfter) <= 0);
            var page = keys.Skip(start).Take(PageSize)
                .Select(k => new StoredObject(k, Objects[k].Length, DateTime.UtcNow))
                .ToList();
            var next = start + PageSize < keys.Count ? (start + PageSize).ToString() : null;
            return Task.FromResult(new ObjectListing(page, next));
        }
    }

    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static ReportBackfillService NewService(DmarcAnalyzerDbContext db, FakeStorage storage)
        => new(
            db,
            storage,
            new DmarcRuaReportParser(),
            Options.Create(new BackupOptions { Bucket = "bucket", Prefix = "dmarc" }),
            Options.Create(new WorkerOptions()),
            NullLogger<ReportBackfillService>.Instance);

    private static (Client Client, Domain Domain) Seed(DmarcAnalyzerDbContext db)
    {
        var client = new Client { Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC" };
        var domain = new Domain { Id = Guid.NewGuid(), ClientId = client.Id, Name = "acme.example", IsActive = true };
        db.AddRange(client, domain);
        db.AddRange(
            new ReportSource { Id = SourceA, Name = "A", Protocol = "imap", DefaultClientId = client.Id, Host = "h", Port = 993, Username = "u", PasswordEncrypted = "x", IsActive = true },
            new ReportSource { Id = SourceB, Name = "B", Protocol = "imap", DefaultClientId = client.Id, Host = "h", Port = 993, Username = "u", PasswordEncrypted = "x", IsActive = true });
        return (client, domain);
    }

    /// <summary>A stored report with the dedup key the XML below produces, and no format recorded unless given.</summary>
    private static DmarcReport StoredReport(Guid domainId, string reportId, Guid? sourceId = null, string? format = null, DateTime? ingestedAt = null)
        => new()
        {
            Id = Guid.NewGuid(), DomainId = domainId, ReportSourceId = sourceId ?? SourceA,
            OrganizationName = "reporter.example", ReportId = reportId,
            RangeBeginUtc = DateTimeOffset.FromUnixTimeSeconds(RangeBegin).UtcDateTime,
            RangeEndUtc = DateTimeOffset.FromUnixTimeSeconds(RangeEnd).UtcDateTime,
            RecordCount = 1, IngestedAtUtc = ingestedAt ?? Day2, PublishedPolicy = "none", PublishedPct = 100,
            ReportFormat = format,
        };

    private static string ReportXml(string reportId, string domain = "acme.example", bool rfc9990 = false, string? np = null)
    {
        var ns = rfc9990 ? " xmlns=\"urn:ietf:params:xml:ns:dmarc-2.0\"" : string.Empty;
        var pct = rfc9990 ? string.Empty : "<pct>100</pct>";
        var npTag = np is null ? string.Empty : $"<np>{np}</np>";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback{ns}>
              <report_metadata>
                <org_name>reporter.example</org_name>
                <report_id>{reportId}</report_id>
                <date_range><begin>{RangeBegin}</begin><end>{RangeEnd}</end></date_range>
              </report_metadata>
              <policy_published><domain>{domain}</domain><p>none</p>{npTag}{pct}</policy_published>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>1</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>{domain}</header_from></identifiers>
                <auth_results><spf><domain>{domain}</domain><result>pass</result></spf></auth_results>
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

    /// <summary>An archived mail as <see cref="ReportMailArchive"/> writes it: gzipped RFC822 with gzipped attachments.</summary>
    private static byte[] ArchivedMail(params (string FileName, string Content)[] attachments)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("reporter", "noreply@reporter.example"));
        message.To.Add(new MailboxAddress("dmarc", "dmarc@acme.example"));
        message.Subject = "Report domain: acme.example";
        message.Date = Day1;

        var multipart = new Multipart("mixed") { new TextPart("plain") { Text = "attached" } };
        foreach (var (fileName, content) in attachments)
        {
            multipart.Add(new MimePart("application", "gzip")
            {
                Content = new MimeContent(new MemoryStream(Gzip(Encoding.UTF8.GetBytes(content)))),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = fileName,
            });
        }

        message.Body = multipart;

        using var raw = new MemoryStream();
        message.WriteTo(raw);
        return Gzip(raw.ToArray());
    }

    private static string Key(Guid sourceId, string uid, DateTime? day = null)
        => ReportMailArchive.Key("dmarc", sourceId, new ReportMailIdentity("1", uid), day ?? Day1);

    private static async Task<ReportBackfillStateDto> RunAsync(DmarcAnalyzerDbContext db, FakeStorage storage)
    {
        var service = NewService(db, storage);
        var requested = await service.RequestRunAsync(CancellationToken.None);
        Assert.True(requested.IsSuccess, requested.Error);
        Assert.True(await service.RunIfRequestedAsync(CancellationToken.None));
        return await service.GetStateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ArchiveWalk_FillsRowsWithNoFormat_AndSkipsRowsThatHaveOne()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        var v1 = StoredReport(domain.Id, "r-v1");
        var v2 = StoredReport(domain.Id, "r-v2");
        var done = StoredReport(domain.Id, "r-done", format: DmarcReportFormats.Rfc7489);
        db.AddRange(v1, v2, done);
        await db.SaveChangesAsync();

        var storage = new FakeStorage();
        storage.Objects[Key(SourceA, "1")] = ArchivedMail(("v1.xml.gz", ReportXml("r-v1", np: "reject")));
        storage.Objects[Key(SourceA, "2")] = ArchivedMail(("v2.xml.gz", ReportXml("r-v2", rfc9990: true)));
        storage.Objects[Key(SourceA, "3")] = ArchivedMail(("done.xml.gz", ReportXml("r-done")));

        var state = await RunAsync(db, storage);

        Assert.Equal(ReportBackfillStatuses.Completed, state.Status);
        Assert.Null(state.Pass);
        Assert.Null(state.LastObjectKey);
        Assert.Equal(3, state.ObjectsSeen);
        Assert.Equal(3, state.PayloadsParsed);
        Assert.Equal(2, state.Matched);
        Assert.Equal(2, state.Updated);
        Assert.Equal(1, state.Skipped);
        Assert.Equal(0, state.Ambiguous);
        Assert.Equal(0, state.Failed);

        db.ChangeTracker.Clear();
        var stored1 = await db.DmarcReports.SingleAsync(x => x.Id == v1.Id);
        Assert.Equal(DmarcReportFormats.Rfc7489, stored1.ReportFormat);
        Assert.Equal("reject", stored1.NonexistentSubdomainPolicy);
        Assert.Equal(100, stored1.PublishedPct);
        Assert.Equal("[]", stored1.ParseMessages);
        Assert.Equal(0, stored1.ParseWarningCount);
        Assert.Equal(DmarcReportParseOrigins.Backfill, stored1.ParseMessagesOrigin);
        Assert.NotNull(stored1.ParseMessagesRecordedAtUtc);

        var stored2 = await db.DmarcReports.SingleAsync(x => x.Id == v2.Id);
        Assert.Equal(DmarcReportFormats.Rfc9990, stored2.ReportFormat);
        Assert.Null(stored2.PublishedPct);
        Assert.Contains("stripped XML namespace", stored2.ParseMessages);

        // Untouched: it had a format, so its messages were never rewritten.
        var storedDone = await db.DmarcReports.SingleAsync(x => x.Id == done.Id);
        Assert.Null(storedDone.ParseMessagesOrigin);

        Assert.Equal(0, await db.ReportBackfillCandidates.CountAsync());
    }

    /// <summary>One mail can hold several reports, and TLS-RPT payloads have nothing to fill.</summary>
    [Fact]
    public async Task MultiReportMail_StagesEveryDmarcPayload_AndSkipsTls()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        db.AddRange(StoredReport(domain.Id, "r-1"), StoredReport(domain.Id, "r-2"));
        await db.SaveChangesAsync();

        const string tls = """{"organization-name":"x","date-range":{"start-datetime":"2026-10-06T00:00:00Z","end-datetime":"2026-10-06T23:59:59Z"},"report-id":"t","policies":[]}""";
        var storage = new FakeStorage();
        storage.Objects[Key(SourceA, "1")] = ArchivedMail(
            ("r1.xml.gz", ReportXml("r-1")), ("r2.xml.gz", ReportXml("r-2")), ("tls.json.gz", tls));

        var state = await RunAsync(db, storage);

        Assert.Equal(1, state.ObjectsSeen);
        Assert.Equal(2, state.PayloadsParsed);
        Assert.Equal(2, state.Updated);
        Assert.Equal(0, state.Failed);
    }

    /// <summary>
    /// The dedup key has no source in it, so a report archived by two sources has one row,
    /// from whichever source stored it first. Only that source's copy is used.
    /// </summary>
    [Fact]
    public async Task CopyArchivedByAnotherSource_IsSkipped()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        var report = StoredReport(domain.Id, "r-1", sourceId: SourceA);
        db.Add(report);
        await db.SaveChangesAsync();

        var storage = new FakeStorage();
        storage.Objects[Key(SourceB, "9")] = ArchivedMail(("r1.xml.gz", ReportXml("r-1")));

        var state = await RunAsync(db, storage);

        Assert.Equal(1, state.Skipped);
        Assert.Equal(0, state.Matched);
        db.ChangeTracker.Clear();
        Assert.Null((await db.DmarcReports.SingleAsync(x => x.Id == report.Id)).ReportFormat);
    }

    /// <summary>
    /// Copies of one report archived on different days: nothing is written until both have
    /// been seen, and when they disagree the row is left alone and counted.
    /// </summary>
    [Fact]
    public async Task ConflictingCopiesOnDifferentDays_LeaveTheRowUnchanged()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        var report = StoredReport(domain.Id, "r-1");
        db.Add(report);
        await db.SaveChangesAsync();

        var storage = new FakeStorage();
        storage.Objects[Key(SourceA, "1", Day1)] = ArchivedMail(("a.xml.gz", ReportXml("r-1", np: "reject")));
        storage.Objects[Key(SourceA, "2", Day2)] = ArchivedMail(("b.xml.gz", ReportXml("r-1")));

        var state = await RunAsync(db, storage);

        Assert.Equal(ReportBackfillStatuses.Completed, state.Status);
        Assert.Equal(2, state.Matched);
        Assert.Equal(1, state.Ambiguous);
        Assert.Equal(0, state.Updated);
        db.ChangeTracker.Clear();
        Assert.Null((await db.DmarcReports.SingleAsync(x => x.Id == report.Id)).ReportFormat);
        Assert.Equal(0, await db.ReportBackfillCandidates.CountAsync());
    }

    [Fact]
    public async Task IdenticalCopiesOnDifferentDays_AreApplied()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        var report = StoredReport(domain.Id, "r-1");
        db.Add(report);
        await db.SaveChangesAsync();

        var storage = new FakeStorage();
        storage.Objects[Key(SourceA, "1", Day1)] = ArchivedMail(("a.xml.gz", ReportXml("r-1", np: "reject")));
        storage.Objects[Key(SourceA, "2", Day2)] = ArchivedMail(("b.xml.gz", ReportXml("r-1", np: "reject")));

        var state = await RunAsync(db, storage);

        Assert.Equal(2, state.Matched);
        Assert.Equal(1, state.Updated);
        Assert.Equal(0, state.Ambiguous);
    }

    /// <summary>A fetch that throws is one object's failure, not the run's.</summary>
    [Fact]
    public async Task FetchFailure_IsCounted_AndTheRestStillApplies()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        db.AddRange(StoredReport(domain.Id, "r-1"), StoredReport(domain.Id, "r-2"));
        await db.SaveChangesAsync();

        var storage = new FakeStorage();
        storage.Objects[Key(SourceA, "1")] = ArchivedMail(("a.xml.gz", ReportXml("r-1")));
        storage.Objects[Key(SourceA, "2")] = ArchivedMail(("b.xml.gz", ReportXml("r-2")));
        storage.ThrowWhenFetching = Key(SourceA, "2");

        var state = await RunAsync(db, storage);

        Assert.Equal(ReportBackfillStatuses.Completed, state.Status);
        Assert.Equal(1, state.Failed);
        Assert.Equal(1, state.Updated);
    }

    /// <summary>
    /// A run that stops in the collect pass — here the listing itself fails — has written
    /// nothing to reports, keeps what it staged, and the next request resumes after the last
    /// object it finished rather than relisting the archive from the top.
    /// </summary>
    [Fact]
    public async Task ListingFailureDuringCollect_WritesNothing_AndTheNextRunResumesAfterTheSavedKey()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        var r1 = StoredReport(domain.Id, "r-1");
        var r2 = StoredReport(domain.Id, "r-2");
        db.AddRange(r1, r2);
        await db.SaveChangesAsync();

        var firstKey = Key(SourceA, "1");
        var secondKey = Key(SourceA, "2");
        var storage = new FakeStorage { PageSize = 1, ThrowOnSecondPage = true };
        storage.Objects[firstKey] = ArchivedMail(("a.xml.gz", ReportXml("r-1")));
        storage.Objects[secondKey] = ArchivedMail(("b.xml.gz", ReportXml("r-2")));

        var stopped = await RunAsync(db, storage);

        Assert.Equal(ReportBackfillStatuses.Failed, stopped.Status);
        Assert.Equal(ReportBackfillPasses.Collect, stopped.Pass);
        Assert.Equal(firstKey, stopped.LastObjectKey);
        Assert.Contains("second page", stopped.LastError);
        Assert.Equal(1, stopped.Matched);
        Assert.Equal(0, stopped.Updated);
        db.ChangeTracker.Clear();
        Assert.Null((await db.DmarcReports.SingleAsync(x => x.Id == r1.Id)).ReportFormat);
        Assert.Equal(1, await db.ReportBackfillCandidates.CountAsync());

        // Resume: the listing starts after the saved key, the staged copy is kept, both apply.
        storage.ThrowOnSecondPage = false;
        storage.Listings.Clear();

        var done = await RunAsync(db, storage);

        Assert.Equal(firstKey, storage.Listings[0].StartAfter);
        Assert.Equal(ReportBackfillStatuses.Completed, done.Status);
        Assert.Equal(2, done.Matched);
        Assert.Equal(2, done.Updated);
        Assert.Null(done.LastError);
        db.ChangeTracker.Clear();
        Assert.Equal(DmarcReportFormats.Rfc7489, (await db.DmarcReports.SingleAsync(x => x.Id == r2.Id)).ReportFormat);
        Assert.Equal(0, await db.ReportBackfillCandidates.CountAsync());
    }

    /// <summary>A missing, corrupt or foreign object is counted and the walk goes on.</summary>
    [Fact]
    public async Task BadObjects_AreCountedNotFatal()
    {
        await using var db = NewDb();
        var (_, domain) = Seed(db);
        var good = StoredReport(domain.Id, "r-good");
        db.Add(good);
        await db.SaveChangesAsync();

        var storage = new FakeStorage();
        storage.Objects[Key(SourceA, "1")] = ArchivedMail(("good.xml.gz", ReportXml("r-good")));
        storage.Objects[Key(SourceA, "2")] = Encoding.UTF8.GetBytes("not gzip, not mail");
        storage.Objects[Key(SourceA, "3")] = ArchivedMail(("x.xml.gz", ReportXml("r-unknown", domain: "nobody.example")));
        storage.Objects["dmarc/reports/2026/10/01/not-a-source/1-1.eml.gz"] = ArchivedMail(("y.xml.gz", ReportXml("r-good")));
        storage.Objects[Key(SourceA, "5")] = ArchivedMail(("z.xml.gz", ReportXml("r-good")));
        storage.Missing.Add(Key(SourceA, "5"));

        var state = await RunAsync(db, storage);

        Assert.Equal(ReportBackfillStatuses.Completed, state.Status);
        Assert.Equal(5, state.ObjectsSeen);
        Assert.Equal(3, state.Failed);   // corrupt, outside the layout, missing
        Assert.Equal(1, state.Skipped);  // unknown domain
        Assert.Equal(1, state.Updated);
    }

    [Fact]
    public async Task Preview_CountsTheArchiveAndTheRows_AndWritesNothing()
    {
        await using var db = NewDb();
        var (client, domain) = Seed(db);
        var pushedSourceId = Guid.NewGuid();
        db.Add(new ReportSource { Id = pushedSourceId, Name = "push", Protocol = ReportSourceProtocols.Api, DefaultClientId = client.Id, IsActive = true });
        db.AddRange(
            StoredReport(domain.Id, "r-1", ingestedAt: Day2),
            StoredReport(domain.Id, "r-old", ingestedAt: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
            StoredReport(domain.Id, "r-pushed", sourceId: pushedSourceId),
            StoredReport(domain.Id, "r-done", format: DmarcReportFormats.Rfc7489));
        await db.SaveChangesAsync();

        var storage = new FakeStorage();
        storage.Objects[Key(SourceA, "1", Day1)] = ArchivedMail(("a.xml.gz", ReportXml("r-1")));
        storage.Objects[Key(SourceA, "2", Day2)] = ArchivedMail(("b.xml.gz", ReportXml("r-1")));

        var preview = await NewService(db, storage).PreviewAsync(CancellationToken.None);

        Assert.True(preview.ArchiveConfigured);
        Assert.Equal(2, preview.ArchivedObjects);
        Assert.Equal(new DateOnly(2026, 10, 1), preview.OldestArchivedDay);
        Assert.Equal(new DateOnly(2026, 10, 2), preview.NewestArchivedDay);
        Assert.Equal(3, preview.ReportsNotRecorded);
        Assert.Equal(1, preview.ReportsFromPushedSources);
        Assert.Equal(2, preview.ReportsFromArchivableSources);
        Assert.Equal(1, preview.ReportsIngestedBeforeTheArchive);
        Assert.Equal(2, preview.EstimatedToStayUnrecorded);
        Assert.Equal(ReportBackfillStatuses.Idle, (await NewService(db, storage).GetStateAsync(CancellationToken.None)).Status);
        Assert.Equal(0, await db.ReportBackfillCandidates.CountAsync());
    }

    [Fact]
    public async Task RequestRun_RefusesWithoutAnArchive_AndWhileOneIsPending()
    {
        await using var db = NewDb();
        Seed(db);
        await db.SaveChangesAsync();

        var unconfigured = new FakeStorage { IsConfigured = false };
        var refused = await NewService(db, unconfigured).RequestRunAsync(CancellationToken.None);
        Assert.False(refused.IsSuccess);
        Assert.Equal(409, refused.StatusCode);

        var storage = new FakeStorage();
        var service = NewService(db, storage);
        Assert.True((await service.RequestRunAsync(CancellationToken.None)).IsSuccess);
        var again = await service.RequestRunAsync(CancellationToken.None);
        Assert.False(again.IsSuccess);
        Assert.Equal(409, again.StatusCode);
        Assert.Contains("requested", again.Error);
    }

    [Fact]
    public void ArchiveKeys_ParseBackToSourceAndDay()
    {
        var key = Key(SourceA, "42", new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(SourceA, ReportBackfillService.SourceIdOf(key));
        Assert.Equal(new DateOnly(2026, 3, 9), ReportBackfillService.ArchivedDayOf(key));
        Assert.Null(ReportBackfillService.SourceIdOf("dmarc/config/latest.json"));
        Assert.Null(ReportBackfillService.ArchivedDayOf("dmarc/config/latest.json"));
    }
}
