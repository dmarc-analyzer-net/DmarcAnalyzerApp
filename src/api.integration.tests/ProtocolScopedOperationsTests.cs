using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.IntegrationTests;

/// <summary>
/// Mailbox operations must only see sources that have a mailbox.
/// <para>
/// Both of these read every report source and neither asked what protocol it was, which
/// was harmless while every source was IMAP and stops being harmless the moment one is
/// not. A pushed source has no sync run and no checkpoint, so it would sit in the health
/// list permanently "never synced" — and the console treats a missing last success as a
/// problem, so it would look broken while working perfectly. A retention plan for it is
/// worse than cosmetic: the service connects over IMAP per plan.
/// </para>
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ProtocolScopedOperationsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Guid ClientId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid ImapSourceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid PushedSourceId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid LegacyPop3SourceId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    public async Task InitializeAsync()
    {
        await postgres.ResetAsync();

        await using var db = postgres.CreateContext();
        db.Clients.Add(new Client
        {
            Id = ClientId, Name = "Acme", Slug = "acme", IsActive = true,
            RetentionMonths = 12, Timezone = "UTC",
        });

        db.ReportSources.Add(Source(ImapSourceId, "Polled", "imap"));

        // Written straight to the database: the service refuses to create these, but rows
        // can exist — 'api' once the endpoint ships, and 'pop3' on any install predating
        // its removal. Both are exactly the cases these filters exist for.
        db.ReportSources.Add(Source(PushedSourceId, "Pushed", "api"));
        db.ReportSources.Add(Source(LegacyPop3SourceId, "Legacy", "pop3", deleteAfterRetention: true));

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task MailboxHealthListsOnlyPolledSources()
    {
        await using var db = postgres.CreateContext();

        var health = await new MailboxHealthQueryService(db).ListAsync(null, CancellationToken.None);

        var only = Assert.Single(health);
        Assert.Equal(ImapSourceId, only.ReportSourceId);
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
    public async Task RetentionPlansOnlyForSourcesThatHaveAMailbox()
    {
        await using var db = postgres.CreateContext();

        var plans = await new MailboxRetentionPlanner(db, Options.Create(new WorkerOptions()))
            .PlanAsync(CancellationToken.None);

        var only = Assert.Single(plans);
        Assert.Equal(ImapSourceId, only.ReportSourceId);
    }

    /// <summary>
    /// The legacy pop3 row has deletion enabled, so before this filter it produced a plan
    /// the retention service would have acted on by opening an IMAP connection to a source
    /// that is not IMAP. That is a live bug on any install with such a row, independent of
    /// the pushed-source work.
    /// </summary>
    [Fact]
    public async Task ALegacyPop3SourceWithDeletionEnabledGetsNoPlan()
    {
        await using var db = postgres.CreateContext();

        var plans = await new MailboxRetentionPlanner(db, Options.Create(new WorkerOptions()))
            .PlanAsync(CancellationToken.None);

        Assert.DoesNotContain(plans, p => p.ReportSourceId == LegacyPop3SourceId);
    }

    private static ReportSource Source(Guid id, string name, string protocol, bool deleteAfterRetention = false)
        => new()
        {
            Id = id,
            Name = name,
            Protocol = protocol,
            Host = protocol == "imap" ? "imap.example.test" : string.Empty,
            Port = protocol == "imap" ? 993 : 0,
            UseTls = protocol == "imap",
            Username = protocol == "imap" ? "rua@acme.test" : string.Empty,
            PasswordEncrypted = protocol == "imap" ? "x" : string.Empty,
            DefaultClientId = ClientId,
            IsActive = true,
            DeleteAfterRetention = deleteAfterRetention,
        };
}
