using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class AuditQueryServiceTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

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
