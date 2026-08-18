using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// Mailbox operations must see exactly the sources that have a mailbox — no more, and since
/// POP3 shipped, no fewer.
/// <para>
/// Both of these read every report source and neither asked what protocol it was, which was
/// harmless while every source was IMAP and stops being harmless the moment one is not. A
/// pushed source has no sync run and no checkpoint, so it would sit in the health list
/// permanently "never synced" — and the console treats a missing last success as a problem,
/// so it would look broken while working perfectly.
/// </para>
/// <para>
/// The filter then had the opposite failure available to it. Written as "is IMAP" rather than
/// "is polled", it excluded POP3 too, which was correct for exactly as long as POP3 did not
/// work: a POP3 source would have synced while showing no health row and never having its
/// mailbox pruned. Hence a case per protocol below rather than one for the pushed source.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ProtocolScopedOperationsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid ClientId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid ImapSourceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid PushedSourceId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid Pop3SourceId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();

        await using var db = postgres.CreateContext();
        db.Clients.Add(new Client
        {
            Id = ClientId, Name = "Acme", Slug = "acme", IsActive = true,
            RetentionMonths = 12, Timezone = "UTC",
        });

        db.ReportSources.Add(Source(ImapSourceId, "Polled", ReportSourceProtocols.Imap));
        db.ReportSources.Add(Source(Pop3SourceId, "Polled over pop3", ReportSourceProtocols.Pop3,
            deleteAfterRetention: true));

        // Written straight to the database rather than through the service, so the row is
        // the shape the filters actually meet: no host, no port, no password.
        db.ReportSources.Add(Source(PushedSourceId, "Pushed", ReportSourceProtocols.Api));

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MailboxHealthListsEveryPolledSourceAndNothingElse()
    {
        await using var db = postgres.CreateContext();

        var health = await new MailboxHealthQueryService(db).ListAsync(null, CancellationToken.None);

        Assert.Equal(
            [ImapSourceId, Pop3SourceId],
            health.Select(x => x.ReportSourceId).OrderBy(x => x));
    }

    [Fact]
    public async Task AskingForAPushedSourceByIdReturnsNothingRatherThanAnEmptyMailbox()
    {
        await using var db = postgres.CreateContext();

        var health = await new MailboxHealthQueryService(db)
            .ListAsync(PushedSourceId, CancellationToken.None);

        Assert.Empty(health);
    }

    [Fact]
    public async Task RetentionPlansForEveryProtocolWithAMailbox()
    {
        await using var db = postgres.CreateContext();

        var plans = await new MailboxRetentionPlanner(db, Options.Create(new WorkerOptions()))
            .PlanAsync(CancellationToken.None);

        Assert.Equal(
            [ImapSourceId, Pop3SourceId],
            plans.Select(x => x.ReportSourceId).OrderBy(x => x));
    }

    /// <summary>
    /// The pushed source is the one that must never get a plan. The retention service opens
    /// a mailbox per plan, and a source with no host is not a mailbox that can be opened —
    /// the planner is where that is decided, before any connection is attempted.
    /// </summary>
    [Fact]
    public async Task APushedSourceGetsNoRetentionPlan()
    {
        await using var db = postgres.CreateContext();

        var plans = await new MailboxRetentionPlanner(db, Options.Create(new WorkerOptions()))
            .PlanAsync(CancellationToken.None);

        Assert.DoesNotContain(plans, p => p.ReportSourceId == PushedSourceId);
    }

    private static ReportSource Source(Guid id, string name, string protocol, bool deleteAfterRetention = false)
    {
        var polled = ReportSourceProtocols.IsPolled(protocol);

        return new ReportSource
        {
            Id = id,
            Name = name,
            Protocol = protocol,
            Host = polled ? $"{protocol}.example.test" : string.Empty,
            Port = polled ? 993 : 0,
            UseTls = polled,
            Username = polled ? "rua@acme.test" : string.Empty,
            PasswordEncrypted = polled ? "x" : string.Empty,
            DefaultClientId = ClientId,
            IsActive = true,
            DeleteAfterRetention = deleteAfterRetention,
        };
    }
}
