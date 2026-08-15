using System.IO.Compression;
using System.Text;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// A real POP3 server, a real message, a real database: the pass an operator gets.
/// <para>
/// This exists because POP3 has been "supported" here once before without working. The value
/// validated, the console offered it, and nothing polled it — so a source could be created and
/// would silently never ingest a byte, which is indistinguishable from an empty mailbox. No
/// unit test caught that, because there was nothing to unit-test: the gap was between the
/// pieces. So this drives the whole path end to end and asserts the two things a mocked test
/// cannot — that a report actually lands, and that the second pass does not fetch it again.
/// </para>
/// <para>
/// The re-fetch case is not hypothetical. Its IMAP counterpart shipped: a checkpoint that
/// could not move past the newest message re-read it every 16 seconds, 5,162 times, on a real
/// instance. POP3 reaches the same state by a different route — a UIDL that is not found in
/// the listing — so it is asserted here rather than reasoned about.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class Pop3MailboxSyncTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const int Pop3Port = 3110;
    private const int SmtpPort = 3025;
    private const string Mailbox = "rua@acme.test";

    // GreenMail's own quirk, not POP3's: -Dgreenmail.users=rua:secret@acme.test creates the
    // address rua@acme.test but the login is the local part alone, and authenticating with
    // the address gets "User 'rua@acme.test' not found".
    private const string MailboxLogin = "rua";
    private const string MailboxPassword = "secret";

    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // GreenMail rather than a hand-rolled fake: the point of this suite is the behaviour a
    // stub would have to be written to have. Plaintext, because TLS is MailKit's concern and
    // is already exercised by every IMAP deployment — what is under test is POP3 itself.
    private readonly IContainer _mail = new ContainerBuilder()
        .WithImage("greenmail/standalone:2.1.9")
        .WithEnvironment(
            "GREENMAIL_OPTS",
            "-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.pop3 " +
            $"-Dgreenmail.users={MailboxLogin}:{MailboxPassword}@{Mailbox.Split('@')[1]} " +
            "-Dgreenmail.hostname=0.0.0.0")
        .WithPortBinding(Pop3Port, true)
        .WithPortBinding(SmtpPort, true)
        // The last line GreenMail logs at INFO, and it is logged once the mail servers are
        // up. The DEBUG "Started services" line reads better and is not there at all unless
        // -Dgreenmail.verbose is set, which is how this first hung until the run timed out.
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilMessageIsLogged("Starting GreenMail API server"))
        .Build();

    public async Task InitializeAsync()
    {
        await _mail.StartAsync();
        await postgres.ResetAsync();

        await using var db = postgres.CreateContext();
        db.Clients.Add(new Client
        {
            Id = ClientId, Name = "Acme", Slug = "acme", IsActive = true,
            RetentionMonths = 12, Timezone = "UTC",
        });
        db.ReportSources.Add(new ReportSource
        {
            Id = SourceId,
            Name = "Acme RUA over POP3",
            Protocol = ReportSourceProtocols.Pop3,
            Host = _mail.Hostname,
            Port = _mail.GetMappedPublicPort(Pop3Port),
            UseTls = false,
            Username = MailboxLogin,
            PasswordEncrypted = MailboxPassword,
            DefaultClientId = ClientId,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _mail.DisposeAsync();

    [Fact]
    public async Task AReportInAPop3MailboxIsIngestedAndCheckpointed()
    {
        await DeliverReportAsync("report-1", "acme.test");

        var result = await SyncAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.MessagesScanned);
        Assert.Equal(1, result.ReportsInserted);
        Assert.Equal(0, result.ParseFailures);

        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.DmarcReports.CountAsync());

        var source = await db.ReportSources.SingleAsync(x => x.Id == SourceId);

        // A UIDL, and nothing in the IMAP columns. Writing a placeholder into those would
        // make the health view claim a checkpoint the protocol cannot honour.
        Assert.False(string.IsNullOrEmpty(source.LastProcessedUidl));
        Assert.Null(source.LastProcessedUid);
        Assert.Null(source.LastProcessedUidValidity);

        Assert.Equal("success", await LatestRunStatusAsync());
    }

    /// <summary>
    /// The whole reason the checkpoint is a UIDL. A caught-up mailbox must scan nothing at
    /// all — not "fetch it again and find it is a duplicate", which is how the IMAP path
    /// burned 5,162 passes and a sync-run row apiece before anyone noticed.
    /// </summary>
    [Fact]
    public async Task ASecondPassOverACaughtUpMailboxScansNothing()
    {
        await DeliverReportAsync("report-1", "acme.test");
        await SyncAsync();

        var second = await SyncAsync();

        Assert.True(second.Success, second.Error);
        Assert.Equal(0, second.MessagesScanned);
        Assert.Equal(0, second.ReportsInserted);
        Assert.Equal(0, second.ReportsSkippedAsDuplicate);
    }

    /// <summary>
    /// New mail after a checkpoint is picked up from the checkpoint, not from the start —
    /// the case that distinguishes a working checkpoint from one that merely happens to be
    /// stored.
    /// </summary>
    [Fact]
    public async Task MailArrivingAfterACheckpointIsPickedUpFromThere()
    {
        await DeliverReportAsync("report-1", "acme.test");
        await SyncAsync();

        await DeliverReportAsync("report-2", "acme.test");
        var second = await SyncAsync();

        Assert.Equal(1, second.MessagesScanned);
        Assert.Equal(1, second.ReportsInserted);

        await using var db = postgres.CreateContext();
        Assert.Equal(2, await db.DmarcReports.CountAsync());
    }

    /// <summary>
    /// The oldest-message date is what the console shows for "how far back could we replay?".
    /// POP3 answers it on every sync, unlike IMAP, where it would mean listing the whole
    /// folder — so a POP3 source has it before any retention pass has ever run.
    /// </summary>
    [Fact]
    public async Task TheOldestMessageDateIsRecordedOnAnOrdinarySync()
    {
        await DeliverReportAsync("report-1", "acme.test");
        await SyncAsync();

        await using var db = postgres.CreateContext();
        var source = await db.ReportSources.SingleAsync(x => x.Id == SourceId);

        Assert.NotNull(source.OldestMessageAtUtc);
    }

    /// <summary>
    /// A message with nothing to extract still has to advance the checkpoint. If it did not,
    /// one piece of unrelated mail in the report mailbox would stall the pass on it for ever.
    /// </summary>
    [Fact]
    public async Task AMessageWithNoAttachmentIsStillCheckpointed()
    {
        await DeliverAsync(NewMessage("Just a note", body: "no attachment here"));

        var result = await SyncAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.MessagesScanned);
        Assert.Equal(0, result.ReportsInserted);

        await using var db = postgres.CreateContext();
        Assert.False(string.IsNullOrEmpty(
            (await db.ReportSources.SingleAsync(x => x.Id == SourceId)).LastProcessedUidl));

        Assert.Equal(0, (await SyncAsync()).MessagesScanned);
    }

    /// <summary>
    /// A source pointed at a host that is not there fails as a failure — with the reason on
    /// the run row, which is the only place an operator will see it.
    /// </summary>
    [Fact]
    public async Task AnUnreachableMailboxFailsTheRunWithAReason()
    {
        await using (var db = postgres.CreateContext())
        {
            var source = await db.ReportSources.SingleAsync(x => x.Id == SourceId);
            source.Port = 1;
            await db.SaveChangesAsync();
        }

        var result = await SyncAsync();

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal("failed", await LatestRunStatusAsync());
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
            new PolledSourceTransportFactory(
            [
                new ImapMailboxTransport(NullLogger<ImapMailboxTransport>.Instance),
                new Pop3MailboxTransport(NullLogger<Pop3MailboxTransport>.Instance),
            ]),
            Options.Create(new WorkerOptions()),
            NullLogger<MailboxSyncService>.Instance);

        var result = await service.SyncReportSourceAsync(SourceId, "test", CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        return result.Value!;
    }

    private async Task<string?> LatestRunStatusAsync()
    {
        await using var db = postgres.CreateContext();
        return await db.MailboxSyncRuns
            .Where(x => x.ReportSourceId == SourceId)
            .OrderByDescending(x => x.StartedAtUtc)
            .Select(x => x.Status)
            .FirstOrDefaultAsync();
    }

    private async Task DeliverReportAsync(string reportId, string policyDomain)
    {
        var message = NewMessage(
            $"Report domain: {policyDomain} Submitter: google.com Report-ID: {reportId}",
            body: "Report attached.");

        var body = new Multipart("mixed")
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
        message.Body = body;

        await DeliverAsync(message);
    }

    private static MimeMessage NewMessage(string subject, string body)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            // A real Date header, because POP3 has no server-side arrival time and the
            // retention pass and the oldest-message date both read this one.
            Date = DateTimeOffset.UtcNow,
        };
        message.From.Add(new MailboxAddress("noreply", "noreply@google.com"));
        message.To.Add(new MailboxAddress("RUA", Mailbox));
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    private async Task DeliverAsync(MimeMessage message)
    {
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(
            _mail.Hostname, _mail.GetMappedPublicPort(SmtpPort), SecureSocketOptions.None);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
    }

    private static byte[] GzipReport(string reportId, string policyDomain)
    {
        var xml = $"""
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
            """;

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(xml));
        }

        return compressed.ToArray();
    }

    /// <summary>
    /// Archiving off, which is the default and the only state that keeps this suite from
    /// needing an object store too. The archive's own key behaviour is covered by
    /// <c>ReportMailIdentityTests</c>.
    /// </summary>
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
}
