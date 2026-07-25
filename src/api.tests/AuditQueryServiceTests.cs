using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class AuditQueryServiceTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    /// <summary>Writes through the real AuditLog, so the snapshot is captured the
    /// same way production captures it.</summary>
    private static AuditLog Log(DmarcAnalyzerDbContext db)
        => new(db, TestCurrentUserContext.Admin(),
               new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
               NullLogger<AuditLog>.Instance);

    private static Client SeedClient(DmarcAnalyzerDbContext db, string name = "Acme")
    {
        var client = new Client
        {
            Id = Guid.NewGuid(), Name = name, Slug = name.ToLowerInvariant(), Timezone = "UTC",
            RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        };
        db.Add(client);
        return client;
    }

    private static void Add(
        DmarcAnalyzerDbContext db, string eventType, string actor,
        double minutesAgo, Guid? clientId = null, string summary = "did a thing")
        => db.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OccurredAtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo),
            ActorType = "user",
            ActorEmail = actor,
            EventType = eventType,
            Summary = summary,
            ClientId = clientId,
        });

    [Fact]
    public async Task ReturnsNewestFirstWithTheUnpagedTotal()
    {
        await using var db = NewDb();
        for (var i = 1; i <= 250; i++) Add(db, "auth.login.succeeded", "a@x.example", i, summary: $"event {i}");
        await db.SaveChangesAsync();

        var page = await new AuditQueryService(db).QueryAsync(new AuditQuery(Limit: 100), CancellationToken.None);

        Assert.Equal(250, page.Total);          // total ignores paging
        Assert.Equal(100, page.Items.Count);
        Assert.Equal("event 1", page.Items[0].Summary);   // newest first
    }

    [Fact]
    public async Task PagesWithoutOverlapOrGaps()
    {
        await using var db = NewDb();
        for (var i = 1; i <= 250; i++) Add(db, "auth.login.succeeded", "a@x.example", i, summary: $"event {i}");
        await db.SaveChangesAsync();
        var service = new AuditQueryService(db);

        var first = await service.QueryAsync(new AuditQuery(Limit: 100, Offset: 0), CancellationToken.None);
        var second = await service.QueryAsync(new AuditQuery(Limit: 100, Offset: 100), CancellationToken.None);
        var last = await service.QueryAsync(new AuditQuery(Limit: 100, Offset: 200), CancellationToken.None);

        Assert.Equal(50, last.Items.Count);
        var seen = first.Items.Concat(second.Items).Concat(last.Items).Select(x => x.Id).ToList();
        Assert.Equal(250, seen.Count);
        Assert.Equal(250, seen.Distinct().Count());   // every entry exactly once
    }

    [Fact]
    public async Task EventTypeFilterMatchesTheWholeFamilyByPrefix()
    {
        await using var db = NewDb();
        Add(db, "client.created", "a@x.example", 1);
        Add(db, "client.updated", "a@x.example", 2);
        Add(db, "auth.login.succeeded", "a@x.example", 3);
        await db.SaveChangesAsync();

        var page = await new AuditQueryService(db).QueryAsync(new AuditQuery(EventType: "client"), CancellationToken.None);

        Assert.Equal(2, page.Total);
        Assert.All(page.Items, x => Assert.StartsWith("client.", x.EventType));
    }

    [Fact]
    public async Task ActorFilterIsACaseInsensitiveSubstring()
    {
        await using var db = NewDb();
        Add(db, "auth.login.succeeded", "Ops@Agency.example", 1);
        Add(db, "auth.login.succeeded", "admin@other.example", 2);
        await db.SaveChangesAsync();

        var page = await new AuditQueryService(db).QueryAsync(new AuditQuery(Actor: "OPS@"), CancellationToken.None);

        Assert.Equal("Ops@Agency.example", Assert.Single(page.Items).ActorEmail);
    }

    /// <summary>
    /// The defect this guards, seen in real data: a row's summary read "Updated
    /// client Middelfart Sparrekasse" — the old, misspelled name frozen into the
    /// text — while the Client column beside it read the corrected name, because
    /// that column was a live join. The row disagreed with itself.
    /// </summary>
    [Fact]
    public async Task ARenamedClientDoesNotRelabelHistory()
    {
        await using var db = NewDb();
        var client = SeedClient(db, "Middelfart Sparrekasse");
        await db.SaveChangesAsync();

        // written while the misspelling was live
        await Log(db).RecordAsync(AuditEvents.ClientUpdated,
            "Updated client Middelfart Sparrekasse", "client", client.Id, client.Id);

        client.Name = "Middelfart Sparekasse";
        await db.SaveChangesAsync();

        var page = await new AuditQueryService(db).QueryAsync(new AuditQuery(), CancellationToken.None);

        var row = Assert.Single(page.Items);
        Assert.Equal("Middelfart Sparrekasse", row.ClientName);   // as it was, not as it is
        Assert.Contains("Middelfart Sparrekasse", row.Summary);   // and it agrees with its own summary
    }

    [Fact]
    public async Task RowsWrittenBeforeTheSnapshotFallBackToTheCurrentName()
    {
        await using var db = NewDb();
        var client = SeedClient(db, "Acme");
        // a pre-migration row: ClientId set, ClientName never recorded
        db.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OccurredAtUtc = DateTime.UtcNow, ActorType = "user",
            ActorEmail = "a@x.example", EventType = "client.updated", Summary = "legacy row",
            ClientId = client.Id, ClientName = null,
        });
        await db.SaveChangesAsync();

        var page = await new AuditQueryService(db).QueryAsync(new AuditQuery(), CancellationToken.None);

        Assert.Equal("Acme", Assert.Single(page.Items).ClientName);
    }

    [Fact]
    public async Task ADeletedClientKeepsTheNameItWasRecordedWith()
    {
        await using var db = NewDb();
        var client = SeedClient(db, "Gone Ltd");
        await db.SaveChangesAsync();
        await Log(db).RecordAsync(AuditEvents.ClientUpdated, "Updated client Gone Ltd", "client", client.Id, client.Id);

        db.Clients.Remove(client);
        await db.SaveChangesAsync();

        var page = await new AuditQueryService(db).QueryAsync(new AuditQuery(), CancellationToken.None);

        var row = Assert.Single(page.Items);
        Assert.Equal("Gone Ltd", row.ClientName);   // previously this went blank
        Assert.NotNull(row.ClientId);
    }

    [Fact]
    public async Task ResolvesTheClientName_AndSurvivesADeletedClient()
    {
        await using var db = NewDb();
        var client = SeedClient(db);
        Add(db, "client.updated", "a@x.example", 1, client.Id);
        // A client id with no surviving row: audit_event has no FK precisely so the
        // trail outlives what it refers to.
        Add(db, "client.updated", "a@x.example", 2, Guid.NewGuid());
        await db.SaveChangesAsync();

        var page = await new AuditQueryService(db).QueryAsync(new AuditQuery(), CancellationToken.None);

        Assert.Equal("Acme", page.Items[0].ClientName);
        Assert.Null(page.Items[1].ClientName);
        Assert.NotNull(page.Items[1].ClientId);   // the id is still there
    }

    [Fact]
    public async Task ExcludesEntriesOlderThanTheWindow()
    {
        await using var db = NewDb();
        Add(db, "auth.login.succeeded", "a@x.example", minutesAgo: 60);
        Add(db, "auth.login.succeeded", "a@x.example", minutesAgo: 60 * 24 * 40);
        await db.SaveChangesAsync();

        var page = await new AuditQueryService(db).QueryAsync(new AuditQuery(Days: 30), CancellationToken.None);

        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task ClampsAnAbsurdLimitRatherThanReturningEverything()
    {
        await using var db = NewDb();
        for (var i = 1; i <= 1200; i++) Add(db, "auth.login.succeeded", "a@x.example", i);
        await db.SaveChangesAsync();

        var page = await new AuditQueryService(db).QueryAsync(new AuditQuery(Limit: 99999), CancellationToken.None);

        Assert.Equal(AuditQueryService.MaxLimit, page.Items.Count);
        Assert.Equal(1200, page.Total);
    }
}
