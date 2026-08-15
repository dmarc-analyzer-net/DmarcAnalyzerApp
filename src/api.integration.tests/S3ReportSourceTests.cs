using System.IO.Compression;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// A real S3-compatible bucket, real objects, a real database.
/// <para>
/// MinIO rather than a mocked <c>IAmazonS3</c>, for the reason the POP3 suite uses a real mail
/// server: what is under test is mostly not this application's logic but its agreement with a
/// service — that a listing pages, that <c>LastModified</c> comes back in UTC, that a delete
/// is effective when it returns, that the SDK's path-style addressing is what a compatible
/// service needs. A mock asserts the assumptions rather than checking them.
/// </para>
/// <para>
/// It is also the closest thing to the shipped configuration: an S3 source pointed at a custom
/// endpoint with a per-source key is exactly what an operator using MinIO, R2 or B2 will have.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class S3ReportSourceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const int MinioPort = 9000;
    private const string AccessKey = "dmarcanalyzer";
    private const string SecretKey = "dmarcanalyzer-secret";
    private const string Bucket = "reports";

    private static readonly Guid ClientId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid SourceId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private readonly IContainer _minio = new ContainerBuilder()
        .WithImage("minio/minio:RELEASE.2025-04-22T22-12-26Z")
        .WithEnvironment("MINIO_ROOT_USER", AccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
        .WithCommand("server", "/data")
        .WithPortBinding(MinioPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r =>
            r.ForPort(MinioPort).ForPath("/minio/health/live")))
        .Build();

    private AmazonS3Client _s3 = null!;

    private string Endpoint => $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(MinioPort)}";

    public async Task InitializeAsync()
    {
        await _minio.StartAsync();

        _s3 = new AmazonS3Client(AccessKey, SecretKey, new AmazonS3Config
        {
            ServiceURL = Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        });

        await _s3.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });

        await postgres.ResetAsync();

        await using var db = postgres.CreateContext();
        db.Clients.Add(new Client
        {
            Id = ClientId, Name = "Acme", Slug = "acme", IsActive = true,
            RetentionMonths = 1, Timezone = "UTC",
        });
        db.ReportSources.Add(new ReportSource
        {
            Id = SourceId,
            Name = "Acme bucket",
            Protocol = ReportSourceProtocols.S3,
            Username = AccessKey,
            PasswordEncrypted = SecretKey,
            S3Bucket = Bucket,
            S3Endpoint = Endpoint,
            S3Region = "us-east-1",
            S3ForcePathStyle = true,
            DefaultClientId = ClientId,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _s3?.Dispose();
        await _minio.DisposeAsync();
    }

    [Fact]
    public async Task AGzippedReportInTheBucketIsIngestedAndCheckpointed()
    {
        await PutAsync("reports/2026/08/google.com!acme.test!1.xml.gz", GzipReport("report-1", "acme.test"));

        var result = await SyncAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.MessagesScanned);
        Assert.Equal(1, result.ReportsInserted);
        Assert.Equal(0, result.ParseFailures);

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.DmarcReports.CountAsync());

        var source = await db.ReportSources.SingleAsync(x => x.Id == SourceId);

        // The object checkpoint, and nothing in the two mail checkpoints. A bucket has no
        // UID and no UIDL, and writing a placeholder into either would make the health view
        // claim a checkpoint that means nothing.
        Assert.Equal("reports/2026/08/google.com!acme.test!1.xml.gz", source.LastProcessedObjectKey);
        Assert.NotNull(source.LastProcessedObjectAtUtc);
        Assert.Null(source.LastProcessedUid);
        Assert.Null(source.LastProcessedUidl);
    }

    /// <summary>
    /// The other delivery shape: SES's "deliver to S3" writes the whole message. Handing that
    /// to the payload extractor would count it a parse failure, so the object has to be
    /// recognised as mail and its own attachments used.
    /// </summary>
    [Fact]
    public async Task AWholeMessageInTheBucketIsParsedAsMail()
    {
        await PutAsync("ses/abc123", await MessageBytesAsync("report-1", "acme.test", gzip: false));

        var result = await SyncAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.ReportsInserted);
        Assert.Equal(0, result.ParseFailures);
    }

    /// <summary>
    /// And the same message gzipped, which is what this application's own report-mail archive
    /// writes — so an operator can point a source at their archive bucket and replay it.
    /// </summary>
    [Fact]
    public async Task AGzippedMessageFromTheArchiveIsReplayable()
    {
        await PutAsync("archive/2026/08/01/x.eml.gz", await MessageBytesAsync("report-1", "acme.test", gzip: true));

        var result = await SyncAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.ReportsInserted);
        Assert.Equal(0, result.ParseFailures);
    }

    [Fact]
    public async Task ASecondPassOverACaughtUpBucketScansNothing()
    {
        await PutAsync("reports/a.xml.gz", GzipReport("report-1", "acme.test"));
        await SyncAsync();

        var second = await SyncAsync();

        Assert.True(second.Success, second.Error);
        Assert.Equal(0, second.MessagesScanned);
        Assert.Equal(0, second.ReportsInserted);
    }

    /// <summary>
    /// The failure mode a <c>StartAfter</c> resume would have, against a real bucket: the new
    /// object's key sorts below the checkpointed one, so key-ordered resumption would never
    /// return it and nothing would ever say so.
    /// </summary>
    [Fact]
    public async Task AnObjectWhoseKeySortsBelowTheCheckpointIsStillPickedUp()
    {
        await PutAsync("zzz-first.xml.gz", GzipReport("report-1", "acme.test"));
        await SyncAsync();

        await PutAsync("aaa-second.xml.gz", GzipReport("report-2", "acme.test"));
        var second = await SyncAsync();

        Assert.Equal(1, second.MessagesScanned);
        Assert.Equal(1, second.ReportsInserted);

        await using var db = postgres.CreateContext();
        Assert.Equal(2, await db.DmarcReports.CountAsync());
    }

    /// <summary>
    /// A "folder" in a console is a zero-byte key ending in a slash. Fetching one yields
    /// nothing, so counting it would mean a parse failure on every pass for ever.
    /// </summary>
    [Fact]
    public async Task DirectoryMarkersAreNotTreatedAsObjects()
    {
        await PutAsync("reports/", []);
        await PutAsync("reports/a.xml.gz", GzipReport("report-1", "acme.test"));

        var result = await SyncAsync();

        Assert.Equal(1, result.MessagesScanned);
        Assert.Equal(0, result.ParseFailures);
    }

    /// <summary>
    /// The prefix is what stops a bucket that holds more than reports from being read whole,
    /// so it has to actually bound the listing rather than only filter afterwards.
    /// </summary>
    [Fact]
    public async Task OnlyThePrefixIsPolled()
    {
        await PutAsync("reports/in.xml.gz", GzipReport("report-1", "acme.test"));
        await PutAsync("other/out.xml.gz", GzipReport("report-2", "acme.test"));

        await using (var db = postgres.CreateContext())
        {
            var source = await db.ReportSources.SingleAsync(x => x.Id == SourceId);
            source.S3Prefix = "reports/";
            await db.SaveChangesAsync();
        }

        var result = await SyncAsync();

        Assert.Equal(1, result.MessagesScanned);

        await using var verify = postgres.CreateContext();
        Assert.Equal(1, await verify.DmarcReports.CountAsync());
    }

    [Fact]
    public async Task AMissingBucketFailsTheRunWithAReason()
    {
        await using (var db = postgres.CreateContext())
        {
            var source = await db.ReportSources.SingleAsync(x => x.Id == SourceId);
            source.S3Bucket = "no-such-bucket";
            await db.SaveChangesAsync();
        }

        var result = await SyncAsync();

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal("failed", await LatestRunStatusAsync());
    }

    [Fact]
    public async Task RetentionDeletesOnlyObjectsPastTheCutoffAndTheyAreActuallyGone()
    {
        // MinIO stamps LastModified at upload, so an "old" object cannot be created by
        // writing one — the cutoff is moved instead, which tests the same comparison from
        // the other side.
        await PutAsync("reports/old.xml.gz", GzipReport("report-1", "acme.test"));
        await Task.Delay(1100);
        await PutAsync("reports/new.xml.gz", GzipReport("report-2", "acme.test"));

        var between = (await ListAsync()).OrderBy(x => x.Value).Skip(1).First().Value;

        var result = await RunRetentionAsync(dryRun: false, cutoffOverrideUtc: between);

        var source = Assert.Single(result.Sources);
        Assert.Null(source.Error);
        Assert.Equal(1, source.Eligible);
        Assert.Equal(1, source.Deleted);

        Assert.Equal(["reports/new.xml.gz"], (await ListAsync()).Keys.Order());
    }

    [Fact]
    public async Task ARetentionDryRunReportsTheCutWithoutMakingIt()
    {
        await PutAsync("reports/old.xml.gz", GzipReport("report-1", "acme.test"));
        await Task.Delay(1100);
        await PutAsync("reports/new.xml.gz", GzipReport("report-2", "acme.test"));

        var between = (await ListAsync()).OrderBy(x => x.Value).Skip(1).First().Value;

        var result = await RunRetentionAsync(dryRun: true, cutoffOverrideUtc: between);

        var source = Assert.Single(result.Sources);
        Assert.Equal(1, source.Eligible);
        Assert.Equal(0, source.Deleted);

        Assert.Equal(["reports/new.xml.gz", "reports/old.xml.gz"], (await ListAsync()).Keys.Order());
    }

    private async Task<MailboxSyncResult> SyncAsync()
    {
        await using var db = postgres.CreateContext();

        var service = new MailboxSyncService(
            db,
            new ReportPayloadIngestor(
                new DmarcRuaReportParser(),
                new TlsRptReportParser(),
                new DmarcReportIngestor(db, new DomainIngestResolver(db)),
                new TlsReportIngestor(db, new DomainIngestResolver(db))),
            new NullCredentialProtector(),
            new ArchiveOff(),
            Transports(),
            Options.Create(new WorkerOptions()),
            NullLogger<MailboxSyncService>.Instance);

        var result = await service.SyncReportSourceAsync(SourceId, "test", CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    /// <summary>
    /// Runs a retention pass with the cutoff forced to a known instant. The planner derives
    /// the real cutoff from the client's retention window plus a grace margin, which is months
    /// in the past and unreachable in a test where every object was written seconds ago —
    /// so the plan is built by hand and only the executor is under test.
    /// </summary>
    private async Task<MailboxRetentionRunResult> RunRetentionAsync(bool dryRun, DateTime cutoffOverrideUtc)
    {
        await using var db = postgres.CreateContext();

        var service = new MailboxRetentionService(
            db,
            new FixedCutoffPlanner(SourceId, "Acme bucket", cutoffOverrideUtc),
            new NullCredentialProtector(),
            new ArchiveOff(),
            Transports(),
            new AuditToNowhere(),
            NullLogger<MailboxRetentionService>.Instance);

        return await service.RunAsync(dryRun, CancellationToken.None);
    }

    private static PolledSourceTransportFactory Transports()
        => new(
        [
            new ImapMailboxTransport(NullLogger<ImapMailboxTransport>.Instance),
            new Pop3MailboxTransport(NullLogger<Pop3MailboxTransport>.Instance),
            new S3ReportSourceTransport(
                Options.Create(new WorkerOptions()), NullLogger<S3ReportSourceTransport>.Instance),
        ]);

    private async Task<string?> LatestRunStatusAsync()
    {
        await using var db = postgres.CreateContext();
        return await db.MailboxSyncRuns
            .Where(x => x.ReportSourceId == SourceId)
            .OrderByDescending(x => x.StartedAtUtc)
            .Select(x => x.Status)
            .FirstOrDefaultAsync();
    }

    private async Task PutAsync(string key, byte[] content)
        => await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            InputStream = new MemoryStream(content),
        });

    /// <summary>What is in the bucket now, read back over its own request.</summary>
    private async Task<Dictionary<string, DateTime>> ListAsync()
    {
        var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket });

        return (response.S3Objects ?? [])
            .ToDictionary(x => x.Key, x => (x.LastModified ?? DateTime.UtcNow).ToUniversalTime());
    }

    private static async Task<byte[]> MessageBytesAsync(string reportId, string policyDomain, bool gzip)
    {
        var message = new MimeMessage { Subject = $"Report domain: {policyDomain}", Date = DateTimeOffset.UtcNow };
        message.From.Add(new MailboxAddress("noreply", "noreply@google.com"));
        message.To.Add(new MailboxAddress("RUA", "rua@acme.test"));
        message.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "Report attached." },
            new MimePart("application", "gzip")
            {
                Content = new MimeContent(new MemoryStream(GzipReport(reportId, policyDomain))),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = $"google.com!{policyDomain}!1754006400!1754092800.xml.gz",
            },
        };

        using var raw = new MemoryStream();
        await message.WriteToAsync(raw);

        return gzip ? GzipBytes(raw.ToArray()) : raw.ToArray();
    }

    private static byte[] GzipReport(string reportId, string policyDomain) => GzipBytes(Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <feedback>
          <report_metadata>
            <org_name>google.com</org_name>
            <email>noreply-dmarc-support@google.com</email>
            <report_id>{reportId}</report_id>
            <date_range><begin>1754006400</begin><end>1754092800</end></date_range>
          </report_metadata>
          <policy_published>
            <domain>{policyDomain}</domain>
            <adkim>r</adkim><aspf>r</aspf><p>none</p><sp>none</sp><pct>100</pct>
          </policy_published>
          <record>
            <row>
              <source_ip>203.0.113.4</source_ip>
              <count>5</count>
              <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
            </row>
            <identifiers><header_from>{policyDomain}</header_from></identifiers>
            <auth_results>
              <dkim><domain>{policyDomain}</domain><result>pass</result></dkim>
              <spf><domain>{policyDomain}</domain><result>pass</result></spf>
            </auth_results>
          </record>
        </feedback>
        """));

    private static byte[] GzipBytes(byte[] content)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return compressed.ToArray();
    }

    /// <summary>
    /// A plan with the cutoff already decided. The planner's own rules — widest window, legal
    /// hold, grace margin — are protocol-independent and covered by their own tests; what this
    /// suite needs from a plan is only a cutoff it can put objects either side of.
    /// </summary>
    private sealed class FixedCutoffPlanner(Guid sourceId, string name, DateTime cutoffUtc)
        : IMailboxRetentionPlanner
    {
        public Task<IReadOnlyList<MailboxRetentionPlan>> PlanAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailboxRetentionPlan>>(
            [
                new MailboxRetentionPlan(
                    sourceId, name, Enabled: true, Suspended: false, Reason: null,
                    CutoffUtc: cutoffUtc, RetentionMonths: 1, GraceDays: 0,
                    ClientSlugs: ["acme"], LegalHoldClientSlugs: [], OldestMessageAtUtc: null),
            ]);
    }

    private sealed class ArchiveOff : IReportMailArchive
    {
        public bool IsEnabled => false;

        public Task<bool> TryArchiveAsync(
            MimeMessage message, Guid reportSourceId, ReportMailIdentity identity,
            DateTime receivedAtUtc, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> ExistsAsync(
            Guid reportSourceId, ReportMailIdentity identity,
            DateTime receivedAtUtc, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class AuditToNowhere : IAuditLog
    {
        public Task RecordAsync(
            string eventType, string summary, string? targetType = null, Guid? targetId = null,
            Guid? clientId = null, string? details = null, string? actorEmailOverride = null,
            Guid? actorUserIdOverride = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordSystemAsync(
            string eventType, string summary, string? details = null, Guid? clientId = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
