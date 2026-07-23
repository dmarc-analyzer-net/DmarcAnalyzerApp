using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Users;
using DmarcAnalyzer.Api.Contracts.Auth;
using DmarcAnalyzer.Api.Contracts.Users;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class UserAdminServiceTests
{
    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static AgencyUser NewUser(string email, string role, bool isActive = true) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        PasswordHash = PasswordHasher.Hash("password-123456"),
        DisplayName = email,
        Role = role,
        IsActive = isActive,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task Register_WhenUsersExist_IsRejected()
    {
        await using var db = NewDb();
        db.AgencyUsers.Add(NewUser("existing@agency.tld", Roles.AgencyAdmin));
        await db.SaveChangesAsync();

        var auth = new AuthService(db);
        var result = await auth.RegisterAsync(new RegisterRequest
        {
            Email = "second@agency.tld",
            Password = "password-123456",
            DisplayName = "Second",
        }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task Register_Bootstrap_ForcesAdminRole()
    {
        await using var db = NewDb();
        var auth = new AuthService(db);

        var result = await auth.RegisterAsync(new RegisterRequest
        {
            Email = "first@agency.tld",
            Password = "password-123456",
            DisplayName = "First",
            Role = "client_viewer",
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Roles.AgencyAdmin, result.Value!.Role);
    }

    [Fact]
    public async Task Update_CannotDemoteLastActiveAdmin()
    {
        await using var db = NewDb();
        var admin = NewUser("admin@agency.tld", Roles.AgencyAdmin);
        db.AgencyUsers.Add(admin);
        await db.SaveChangesAsync();

        var service = new UserAdminService(db, TestCurrentUserContext.Admin());

        var demote = await service.UpdateAsync(admin.Id, new UpdateUserRequest { Role = Roles.AgencyAnalyst }, CancellationToken.None);
        Assert.Equal(409, demote.StatusCode);

        var deactivate = await service.UpdateAsync(admin.Id, new UpdateUserRequest { IsActive = false }, CancellationToken.None);
        Assert.Equal(409, deactivate.StatusCode);
    }

    [Fact]
    public async Task Update_DeactivatingUser_RevokesOpenSessions()
    {
        await using var db = NewDb();
        var admin = NewUser("admin@agency.tld", Roles.AgencyAdmin);
        var analyst = NewUser("analyst@agency.tld", Roles.AgencyAnalyst);
        db.AgencyUsers.AddRange(admin, analyst);
        db.UserSessions.Add(new UserSession
        {
            UserId = analyst.Id,
            CookieId = "cookie-1",
            CreatedAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync();

        var service = new UserAdminService(db, TestCurrentUserContext.Admin());
        var result = await service.UpdateAsync(analyst.Id, new UpdateUserRequest { IsActive = false }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var session = await db.UserSessions.SingleAsync();
        Assert.NotNull(session.RevokedAtUtc);
    }

    [Fact]
    public async Task ReplaceGrants_SetSemantics_AndViewerOnly()
    {
        await using var db = NewDb();
        var admin = NewUser("admin@agency.tld", Roles.AgencyAdmin);
        var viewer = NewUser("viewer@client.tld", Roles.ClientViewer);
        var clientA = new Client { Id = Guid.NewGuid(), Name = "A", Slug = "a", Timezone = "UTC", RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        var clientB = new Client { Id = Guid.NewGuid(), Name = "B", Slug = "b", Timezone = "UTC", RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.AddRange(admin, viewer, clientA, clientB);
        await db.SaveChangesAsync();

        var service = new UserAdminService(db, TestCurrentUserContext.Admin());

        var first = await service.ReplaceGrantsAsync(viewer.Id, new ReplaceUserGrantsRequest { ClientIds = [clientA.Id, clientB.Id] }, CancellationToken.None);
        Assert.True(first.IsSuccess);
        Assert.Equal(2, first.Value!.GrantedClientIds.Count);

        var second = await service.ReplaceGrantsAsync(viewer.Id, new ReplaceUserGrantsRequest { ClientIds = [clientB.Id] }, CancellationToken.None);
        Assert.True(second.IsSuccess);
        Assert.Single(second.Value!.GrantedClientIds);
        Assert.Equal(clientB.Id, second.Value.GrantedClientIds[0]);
        Assert.Equal(1, await db.UserClientGrants.CountAsync());

        var staffGrant = await service.ReplaceGrantsAsync(admin.Id, new ReplaceUserGrantsRequest { ClientIds = [clientA.Id] }, CancellationToken.None);
        Assert.Equal(400, staffGrant.StatusCode);

        var unknownClient = await service.ReplaceGrantsAsync(viewer.Id, new ReplaceUserGrantsRequest { ClientIds = [Guid.NewGuid()] }, CancellationToken.None);
        Assert.Equal(400, unknownClient.StatusCode);
    }

    [Fact]
    public async Task Update_PromotingViewerToStaff_DropsGrants()
    {
        await using var db = NewDb();
        var admin = NewUser("admin@agency.tld", Roles.AgencyAdmin);
        var viewer = NewUser("viewer@client.tld", Roles.ClientViewer);
        var client = new Client { Id = Guid.NewGuid(), Name = "A", Slug = "a", Timezone = "UTC", RetentionMonths = 27, IsActive = true, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow };
        db.AddRange(admin, viewer, client);
        db.UserClientGrants.Add(new UserClientGrant { UserId = viewer.Id, ClientId = client.Id });
        await db.SaveChangesAsync();

        var service = new UserAdminService(db, TestCurrentUserContext.Admin());
        var result = await service.UpdateAsync(viewer.Id, new UpdateUserRequest { Role = Roles.AgencyAnalyst }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.GrantedClientIds);
        Assert.Equal(0, await db.UserClientGrants.CountAsync());
    }
}
