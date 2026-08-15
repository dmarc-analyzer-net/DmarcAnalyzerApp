using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// Deleting a customer's mail over POP3, against a real server.
/// <para>
/// This is the one thing in the application that destroys data it does not own, and POP3 is
/// where it is least like the design the rules were written for. There is no server-side date
/// search, so eligibility is decided from the sender's own <c>Date</c> header; there is no
/// expunge, so <c>DELE</c> only marks and the deletion does not happen until the session ends
/// with <c>QUIT</c>. Both of those are assertions about a server's behaviour rather than about
/// this code, which is why they are tested against one.
/// </para>
/// <para>
/// The QUIT-timing case in particular cannot be checked any other way. If the pass closed the
/// session by disposing the client instead of quitting, every test that only counted intended
/// deletions would still pass and no mail would ever actually be deleted.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class Pop3MailboxRetentionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const int Pop3Port = 3110;
    private const int SmtpPort = 3025;
    private const string Mailbox = "rua@acme.test";
    private const string MailboxLogin = "rua";
    private const string MailboxPassword = "secret";

    private static readonly Guid ClientId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SourceId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly IContainer _mail = new ContainerBuilder()
        .WithImage("greenmail/standalone:2.1.9")
        .WithEnvironment(
            "GREENMAIL_OPTS",
            "-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.pop3 " +
            $"-Dgreenmail.users={MailboxLogin}:{MailboxPassword}@{Mailbox.Split('@')[1]} " +
            "-Dgreenmail.hostname=0.0.0.0")
        .WithPortBinding(Pop3Port, true)
        .WithPortBinding(SmtpPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilMessageIsLogged("Starting GreenMail API server"))
        .Build();

    public async Task InitializeAsync()
    {
        await _mail.StartAsync();
        await postgres.ResetAsync();

        await using var db = postgres.CreateContext();

        // One month, so the planner's cutoff — widest window plus a 30-day grace — lands
        // about two months back, between the two messages delivered below.
        db.Clients.Add(new Client
        {
            Id = ClientId, Name = "Acme", Slug = "acme", IsActive = true,
            RetentionMonths = 1, Timezone = "UTC",
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
            DeleteAfterRetention = true,
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _mail.DisposeAsync();

    [Fact]
    public async Task OnlyMailPastTheCutoffIsDeletedAndItIsActuallyGone()
    {
        await DeliverAsync("ancient", DateTimeOffset.UtcNow.AddYears(-1));
        await DeliverAsync("recent", DateTimeOffset.UtcNow.AddDays(-1));

        var result = await RunRetentionAsync(dryRun: false);

        var source = Assert.Single(result.Sources);
        Assert.Null(source.Error);
        Assert.False(source.Suspended);
        Assert.Equal(1, source.Eligible);
        Assert.Equal(1, source.Deleted);

        // Re-read over a fresh session, because a DELE that was never committed by QUIT
        // still reports a deletion to the caller that issued it.
        Assert.Equal(["recent"], await RemainingSubjectsAsync());
    }

    /// <summary>
    /// A dry run is not "delete but do not count"; nothing may leave the mailbox. It is the
    /// only way an operator can find out what a real pass would do to a mailbox they cannot
    /// get back.
    /// </summary>
    [Fact]
    public async Task ADryRunReportsTheCutWithoutMakingIt()
    {
        await DeliverAsync("ancient", DateTimeOffset.UtcNow.AddYears(-1));
        await DeliverAsync("recent", DateTimeOffset.UtcNow.AddDays(-1));

        var result = await RunRetentionAsync(dryRun: true);

        var source = Assert.Single(result.Sources);
        Assert.Equal(1, source.Eligible);
        Assert.Equal(0, source.Deleted);

        Assert.Equal(["ancient", "recent"], await RemainingSubjectsAsync());
    }

    /// <summary>
    /// Legal hold suspends the pass before a mailbox is opened at all. The database
    /// exemption is worthless if the upstream copy is being deleted.
    /// </summary>
    [Fact]
    public async Task LegalHoldStopsThePassBeforeAnythingIsOpened()
    {
        await DeliverAsync("ancient", DateTimeOffset.UtcNow.AddYears(-1));

        await using (var db = postgres.CreateContext())
        {
            var client = await db.Clients.SingleAsync(x => x.Id == ClientId);
            client.LegalHold = true;
            await db.SaveChangesAsync();
        }

        var result = await RunRetentionAsync(dryRun: false);

        var source = Assert.Single(result.Sources);
        Assert.True(source.Suspended);
        Assert.Equal(0, source.Deleted);
        Assert.Equal(["ancient"], await RemainingSubjectsAsync());
    }

    /// <summary>
    /// The oldest-message date is what an operator reads to confirm the cut landed where it
    /// was meant to, so it has to reflect the pass that just ran rather than the mailbox as
    /// it was found — including the messages the pass has marked but not yet committed.
    /// </summary>
    [Fact]
    public async Task TheOldestMessageDateReflectsTheCut()
    {
        var recent = DateTimeOffset.UtcNow.AddDays(-1);
        await DeliverAsync("ancient", DateTimeOffset.UtcNow.AddYears(-1));
        await DeliverAsync("recent", recent);

        await RunRetentionAsync(dryRun: false);

        await using var db = postgres.CreateContext();
        var oldest = (await db.ReportSources.SingleAsync(x => x.Id == SourceId)).OldestMessageAtUtc;

        Assert.NotNull(oldest);
        Assert.True(
            oldest!.Value > DateTime.UtcNow.AddMonths(-1),
            $"expected the surviving message's date, got {oldest:O}");
    }

    private async Task<MailboxRetentionRunResult> RunRetentionAsync(bool dryRun)
    {
        await using var db = postgres.CreateContext();
        var options = Options.Create(new WorkerOptions());

        var service = new MailboxRetentionService(
            db,
            new MailboxRetentionPlanner(db, options),
            new NullCredentialProtector(),
            new ArchiveOff(),
            new PolledSourceTransportFactory(
            [
                new ImapMailboxTransport(NullLogger<ImapMailboxTransport>.Instance),
                new Pop3MailboxTransport(NullLogger<Pop3MailboxTransport>.Instance),
            ]),
            new AuditToNowhere(),
            NullLogger<MailboxRetentionService>.Instance);

        return await service.RunAsync(dryRun, CancellationToken.None);
    }

    /// <summary>
    /// What is left in the mailbox, read over a connection of its own. Asking the session
    /// that did the deleting would answer from its own marks rather than from the server.
    /// </summary>
    private async Task<string[]> RemainingSubjectsAsync()
    {
        using var pop3 = new Pop3Client();
        await pop3.ConnectAsync(
            _mail.Hostname, _mail.GetMappedPublicPort(Pop3Port), SecureSocketOptions.None);
        await pop3.AuthenticateAsync(MailboxLogin, MailboxPassword);

        var subjects = new List<string>();
        for (var index = 0; index < pop3.Count; index++)
        {
            subjects.Add((await pop3.GetMessageAsync(index)).Subject ?? string.Empty);
        }

        await pop3.DisconnectAsync(true);
        return [.. subjects.OrderBy(x => x, StringComparer.Ordinal)];
    }

    private async Task DeliverAsync(string subject, DateTimeOffset date)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            // POP3 has no server-side arrival time, so this header is the only thing the
            // retention pass can judge age by — which is exactly what is under test.
            Date = date,
            Body = new TextPart("plain") { Text = subject },
        };
        message.From.Add(new MailboxAddress("noreply", "noreply@google.com"));
        message.To.Add(new MailboxAddress("RUA", Mailbox));

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(
            _mail.Hostname, _mail.GetMappedPublicPort(SmtpPort), SecureSocketOptions.None);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
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
