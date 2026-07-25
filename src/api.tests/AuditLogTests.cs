using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class AuditLogTests
{
    private static DmarcAnalyzerDbContext NewDb()
        => new(new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static AuditLog Log(DmarcAnalyzerDbContext db, TestCurrentUserContext? user = null)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        return new AuditLog(db, user ?? TestCurrentUserContext.Admin(), accessor,
            NullLogger<AuditLog>.Instance);
    }

    [Fact]
    public async Task RecordsTheSignedInUserAsActor()
    {
        await using var db = NewDb();
        var user = TestCurrentUserContext.Admin();

        await Log(db, user).RecordAsync(AuditEvents.ClientCreated, "Created client Acme", "client");

        var entry = Assert.Single(await db.AuditEvents.ToListAsync());
        Assert.Equal("user", entry.ActorType);
        Assert.Equal(user.UserId, entry.ActorUserId);
        Assert.Equal(user.Email, entry.ActorEmail);
        Assert.Equal("client", entry.TargetType);
    }

    [Fact]
    public async Task AnUnauthenticatedActionIsRecordedAsAnonymous()
    {
        await using var db = NewDb();
        var anonymous = new TestCurrentUserContext { IsAuthenticated = false };

        await Log(db, anonymous).RecordAsync(
            AuditEvents.LoginFailed, "Failed sign-in for x@y.z", actorEmailOverride: "x@y.z");

        var entry = Assert.Single(await db.AuditEvents.ToListAsync());
        Assert.Equal("anonymous", entry.ActorType);
        Assert.Null(entry.ActorUserId);
        // The attempted address is still captured — that's the point of the record.
        Assert.Equal("x@y.z", entry.ActorEmail);
    }

    [Fact]
    public async Task ASuccessfulLoginIsAttributedToTheUserItAuthenticated()
    {
        await using var db = NewDb();
        var anonymous = new TestCurrentUserContext { IsAuthenticated = false };
        var userId = Guid.NewGuid();

        await Log(db, anonymous).RecordAsync(
            AuditEvents.LoginSucceeded, "Signed in", "user", userId,
            actorEmailOverride: "x@y.z", actorUserIdOverride: userId);

        var entry = Assert.Single(await db.AuditEvents.ToListAsync());
        Assert.Equal("user", entry.ActorType);
        Assert.Equal(userId, entry.ActorUserId);
    }

    [Fact]
    public async Task SystemEventsHaveNoUser()
    {
        await using var db = NewDb();

        await Log(db).RecordSystemAsync(AuditEvents.RetentionPurgeRan, "Purged 5 reports");

        var entry = Assert.Single(await db.AuditEvents.ToListAsync());
        Assert.Equal("system", entry.ActorType);
        Assert.Null(entry.ActorUserId);
    }

    [Fact]
    public async Task OverlongValuesAreTruncatedRatherThanFailingTheWrite()
    {
        await using var db = NewDb();

        await Log(db).RecordAsync(AuditEvents.ClientUpdated, new string('x', 900),
            details: new string('y', 5000));

        var entry = Assert.Single(await db.AuditEvents.ToListAsync());
        Assert.Equal(500, entry.Summary.Length);
        Assert.Equal(4000, entry.Details!.Length);
    }

    [Fact]
    public async Task AFailedWriteDoesNotThrow()
    {
        var db = NewDb();
        await db.DisposeAsync();   // force the write to fail

        // Auditing must never break the operation it describes.
        await Log(db).RecordAsync(AuditEvents.ClientCreated, "Created client Acme");
    }
}
