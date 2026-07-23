using System.Security.Claims;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class OidcSignInServiceTests
{
    private const string Issuer = "http://localhost:8082";

    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new DmarcAnalyzerDbContext(options);
    }

    private static OidcSignInService NewService(DmarcAnalyzerDbContext db, bool autoProvision)
    {
        var opts = Options.Create(new OidcOptions
        {
            Enabled = true,
            Authority = Issuer,
            AutoProvision = autoProvision,
            DefaultRole = Roles.ClientViewer,
        });
        return new OidcSignInService(db, new AuthService(db), opts, NullLogger<OidcSignInService>.Instance);
    }

    private static ClaimsPrincipal Principal(string subject, string? email, bool emailVerified, string name = "Ext User")
    {
        var claims = new List<Claim> { new("sub", subject), new("name", name) };
        if (email is not null)
        {
            claims.Add(new Claim("email", email));
            claims.Add(new Claim("email_verified", emailVerified ? "true" : "false"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
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
    public async Task LinksToExistingUser_OnVerifiedEmail()
    {
        await using var db = NewDb();
        var existing = NewUser("staff@agency.tld", Roles.AgencyAnalyst);
        db.AgencyUsers.Add(existing);
        await db.SaveChangesAsync();

        var result = await NewService(db, autoProvision: false)
            .SignInAsync(Principal("sub-1", "staff@agency.tld", emailVerified: true), null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var identity = await db.UserIdentities.SingleAsync();
        Assert.Equal(existing.Id, identity.UserId);
        Assert.Equal("sub-1", identity.Subject);
        // Role is preserved — the IdP does not decide authorization.
        Assert.Equal(Roles.AgencyAnalyst, (await db.AgencyUsers.SingleAsync(x => x.Id == existing.Id)).Role);
    }

    [Fact]
    public async Task RefusesToLink_OnUnverifiedEmail()
    {
        await using var db = NewDb();
        db.AgencyUsers.Add(NewUser("staff@agency.tld", Roles.AgencyAdmin));
        await db.SaveChangesAsync();

        var result = await NewService(db, autoProvision: true)
            .SignInAsync(Principal("sub-2", "staff@agency.tld", emailVerified: false), null, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("email_not_verified", result.ErrorCode);
        Assert.Equal(0, await db.UserIdentities.CountAsync());
    }

    [Fact]
    public async Task AutoProvisionsViewer_WhenEnabledAndNoMatch()
    {
        await using var db = NewDb();

        var result = await NewService(db, autoProvision: true)
            .SignInAsync(Principal("sub-3", "new@external.tld", emailVerified: true), null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var user = await db.AgencyUsers.SingleAsync();
        Assert.Equal("new@external.tld", user.Email);
        Assert.Equal(Roles.ClientViewer, user.Role);
        // Empty hash can never pass password verification — no password back door.
        Assert.Equal(string.Empty, user.PasswordHash);
        Assert.False(PasswordHasher.Verify("", user.PasswordHash));
    }

    [Fact]
    public async Task Refuses_WhenNoMatchAndAutoProvisionOff()
    {
        await using var db = NewDb();

        var result = await NewService(db, autoProvision: false)
            .SignInAsync(Principal("sub-4", "new@external.tld", emailVerified: true), null, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("no_account", result.ErrorCode);
        Assert.Equal(0, await db.AgencyUsers.CountAsync());
    }

    [Fact]
    public async Task ReusesIdentity_OnRepeatLogin()
    {
        await using var db = NewDb();
        var service = NewService(db, autoProvision: true);
        var principal = Principal("sub-5", "repeat@external.tld", emailVerified: true);

        var first = await service.SignInAsync(principal, null, null, CancellationToken.None);
        var second = await service.SignInAsync(principal, null, null, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(1, await db.AgencyUsers.CountAsync());
        Assert.Equal(1, await db.UserIdentities.CountAsync());
    }

    [Fact]
    public async Task Refuses_WhenLinkedUserDeactivated()
    {
        await using var db = NewDb();
        var user = NewUser("disabled@external.tld", Roles.ClientViewer, isActive: false);
        db.AgencyUsers.Add(user);
        db.UserIdentities.Add(new UserIdentity { UserId = user.Id, Issuer = Issuer, Subject = "sub-6", CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await NewService(db, autoProvision: false)
            .SignInAsync(Principal("sub-6", "disabled@external.tld", emailVerified: true), null, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("account_disabled", result.ErrorCode);
    }
}
