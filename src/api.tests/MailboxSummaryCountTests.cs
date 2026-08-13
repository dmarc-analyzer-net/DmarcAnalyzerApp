using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The dashboard's "n/m mailboxes healthy" line, which counted every report source
/// regardless of whether one had a mailbox behind it.
/// <para>
/// A pushed source has no sync run and never will, so it arrived in the total and then
/// failed to be counted as failing — silently healthy. An install with nothing but pushed
/// sources therefore read "1/1 mailboxes healthy" while owning no mailbox at all, and
/// disagreed with the report sources screen beside it, which derives its own count from
/// <c>/mailbox-health</c> and so had already excluded them.
/// </para>
/// </summary>
public sealed class MailboxSummaryCountTests
{
    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static Guid SeedClient(DmarcAnalyzerDbContext db)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = "acme", Slug = "acme", Timezone = "UTC",
            RetentionMonths = 27, IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Add(client);
        return client.Id;
    }

    private static ReportSource AddSource(DmarcAnalyzerDbContext db, Guid clientId, string protocol)
    {
        var source = new ReportSource
        {
            Id = Guid.NewGuid(), Name = $"{protocol} source", Protocol = protocol,
            Host = protocol == ReportSourceProtocols.Imap ? "imap.example" : string.Empty,
            Port = protocol == ReportSourceProtocols.Imap ? 993 : 0,
            Username = string.Empty, PasswordEncrypted = string.Empty,
            DefaultClientId = clientId, IsActive = true,
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Add(source);
        return source;
    }

    private static void AddRun(DmarcAnalyzerDbContext db, Guid sourceId, string status)
        => db.Add(new MailboxSyncRun
        {
            Id = Guid.NewGuid(), ReportSourceId = sourceId, Trigger = "scheduled", Status = status,
            StartedAtUtc = DateTime.UtcNow, FinishedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
        });

    [Fact]
    public async Task PushedSourcesAreNotCountedAsMailboxes()
    {
        using var db = NewDb();
        var clientId = SeedClient(db);
        AddSource(db, clientId, "api");
        AddSource(db, clientId, "api");
        await db.SaveChangesAsync();

        var summary = await TestAnalytics
            .Service(db, TestCurrentUserContext.Admin())
            .GetSummaryAsync(30, CancellationToken.None);

        Assert.NotNull(summary.Mailboxes);
        Assert.Equal(0, summary.Mailboxes!.Total);
        Assert.Equal(0, summary.Mailboxes.Healthy);
        Assert.Equal(0, summary.Mailboxes.Failing);
    }

    [Fact]
    public async Task OnlyPolledSourcesCountTowardTheTotal()
    {
        using var db = NewDb();
        var clientId = SeedClient(db);
        var healthy = AddSource(db, clientId, ReportSourceProtocols.Imap);
        var failing = AddSource(db, clientId, ReportSourceProtocols.Imap);
        AddSource(db, clientId, "api");
        AddRun(db, healthy.Id, "success");
        AddRun(db, failing.Id, "failed");
        await db.SaveChangesAsync();

        var summary = await TestAnalytics
            .Service(db, TestCurrentUserContext.Admin())
            .GetSummaryAsync(30, CancellationToken.None);

        Assert.NotNull(summary.Mailboxes);
        Assert.Equal(2, summary.Mailboxes!.Total);
        Assert.Equal(1, summary.Mailboxes.Healthy);
        Assert.Equal(1, summary.Mailboxes.Failing);
    }

    /// <summary>
    /// A legacy <c>pop3</c> row is not polled either — the worker has only ever selected
    /// <c>imap</c> — so it must not inflate the denominator the way it used to.
    /// </summary>
    [Fact]
    public async Task LegacyPop3RowsAreNotCountedAsMailboxes()
    {
        using var db = NewDb();
        var clientId = SeedClient(db);
        AddSource(db, clientId, ReportSourceProtocols.Imap);
        AddSource(db, clientId, "pop3");
        await db.SaveChangesAsync();

        var summary = await TestAnalytics
            .Service(db, TestCurrentUserContext.Admin())
            .GetSummaryAsync(30, CancellationToken.None);

        Assert.NotNull(summary.Mailboxes);
        Assert.Equal(1, summary.Mailboxes!.Total);
    }
}
