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

    private static OidcSignInService NewService(DmarcAnalyzerDbContext db, bool autoProvision, bool trustUnverifiedEmail = false)
    {
        var opts = Options.Create(new OidcOptions
        {
            Enabled = true,
            Authority = Issuer,
            AutoProvision = autoProvision,
            DefaultRole = Roles.ClientViewer,
            TrustUnverifiedEmail = trustUnverifiedEmail,
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

    /// <summary>
    /// A principal carrying whatever assurance claims are passed — including
    /// none, which is what Entra ID sends: it has no <c>email_verified</c> to
    /// send (#140).
    /// </summary>
    private static ClaimsPrincipal PrincipalWithAssurance(string subject, string email, params (string Type, string Value)[] assurance)
    {
        var claims = new List<Claim> { new("sub", subject), new("name", "Ext User"), new("email", email) };
        claims.AddRange(assurance.Select(x => new Claim(x.Type, x.Value)));
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
    public async Task LinksToPasswordlessUser_PreProvisionedByAnAdmin()
    {
        // The point of admin-created passwordless accounts: with AutoProvision off,
        // an admin pre-creates the account at the role they want, and the user's
        // first OIDC login lands on it instead of being refused as no_account.
        await using var db = NewDb();
        var created = await new Application.Users.UserAdminService(db, TestCurrentUserContext.Admin())
            .CreateAsync(new Contracts.Users.CreateUserRequest
            {
                Email = "sso.staff@agency.tld",
                DisplayName = "SSO Staff",
                Role = Roles.AgencyAnalyst,
            }, CancellationToken.None);
        Assert.True(created.IsSuccess);

        var result = await NewService(db, autoProvision: false)
            .SignInAsync(Principal("sub-sso", "sso.staff@agency.tld", emailVerified: true), null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var identity = await db.UserIdentities.SingleAsync();
        Assert.Equal(created.Value!.Id, identity.UserId);
        // Still no password door after the link.
        Assert.Equal(string.Empty, (await db.AgencyUsers.SingleAsync()).PasswordHash);
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

    /// <summary>
    /// #140. Entra ID issues no <c>email_verified</c> claim at all, so treating a
    /// missing claim as "not verified" locked every Entra user out of their own
    /// pre-existing account permanently. Silence is refused by default — but as
    /// its own error, because "your email is not verified" sends the operator
    /// looking for a mailbox to confirm when nothing is wrong with the mailbox.
    /// </summary>
    [Fact]
    public async Task RefusesToLink_WhenProviderSaysNothingAboutTheEmail()
    {
        await using var db = NewDb();
        db.AgencyUsers.Add(NewUser("staff@agency.tld", Roles.AgencyAdmin));
        await db.SaveChangesAsync();

        var result = await NewService(db, autoProvision: false)
            .SignInAsync(PrincipalWithAssurance("sub-entra", "staff@agency.tld"), null, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("email_verification_unknown", result.ErrorCode);
        Assert.Equal(0, await db.UserIdentities.CountAsync());
    }

    [Fact]
    public async Task LinksToExistingUser_WhenSilenceIsTrustedByConfiguration()
    {
        await using var db = NewDb();
        var existing = NewUser("staff@agency.tld", Roles.AgencyAdmin);
        db.AgencyUsers.Add(existing);
        await db.SaveChangesAsync();

        var result = await NewService(db, autoProvision: false, trustUnverifiedEmail: true)
            .SignInAsync(PrincipalWithAssurance("sub-entra", "staff@agency.tld"), null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, (await db.UserIdentities.SingleAsync()).UserId);
        // Still the IdP's job to authenticate and ours to authorize.
        Assert.Equal(Roles.AgencyAdmin, (await db.AgencyUsers.SingleAsync()).Role);
    }

    /// <summary>
    /// The flag buys trust in silence, not in a denial. A provider that answers
    /// "no" is still believed, or turning the flag on to accommodate one provider
    /// would quietly weaken every other one configured after it.
    /// </summary>
    [Fact]
    public async Task RefusesToLink_OnUnverifiedEmail_EvenWhenSilenceIsTrusted()
    {
        await using var db = NewDb();
        db.AgencyUsers.Add(NewUser("staff@agency.tld", Roles.AgencyAdmin));
        await db.SaveChangesAsync();

        var result = await NewService(db, autoProvision: false, trustUnverifiedEmail: true)
            .SignInAsync(Principal("sub-2", "staff@agency.tld", emailVerified: false), null, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("email_not_verified", result.ErrorCode);
        Assert.Equal(0, await db.UserIdentities.CountAsync());
    }

    /// <summary>
    /// Entra's own answer to the gap: the <c>xms_edov</c> optional claim, "email
    /// domain owner verified". It is a real assertion, so it links with no trust
    /// flag configured at all — which is the setup worth steering operators to.
    /// </summary>
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("1")]
    public async Task LinksToExistingUser_OnDomainOwnerVerifiedClaim(string claimValue)
    {
        await using var db = NewDb();
        var existing = NewUser("staff@agency.tld", Roles.AgencyAdmin);
        db.AgencyUsers.Add(existing);
        await db.SaveChangesAsync();

        var result = await NewService(db, autoProvision: false)
            .SignInAsync(
                PrincipalWithAssurance("sub-entra", "staff@agency.tld", ("xms_edov", claimValue)),
                null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, (await db.UserIdentities.SingleAsync()).UserId);
    }

    [Fact]
    public async Task RefusesToLink_WhenDomainOwnerNotVerified()
    {
        await using var db = NewDb();
        db.AgencyUsers.Add(NewUser("staff@agency.tld", Roles.AgencyAdmin));
        await db.SaveChangesAsync();

        var result = await NewService(db, autoProvision: false, trustUnverifiedEmail: true)
            .SignInAsync(
                PrincipalWithAssurance("sub-entra", "staff@agency.tld", ("xms_edov", "false")),
                null, null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("email_not_verified", result.ErrorCode);
    }

    /// <summary>
    /// Pins the deliberate asymmetry with the linking path above: provisioning
    /// asks nothing about assurance, because there is no account to take over and
    /// a new one lands on <c>DefaultRole</c>. Entra deployments with
    /// <c>AutoProvision</c> on worked throughout #140 for this reason — only
    /// users who already had an account were locked out.
    /// </summary>
    [Fact]
    public async Task AutoProvisions_WhenProviderSaysNothingAboutTheEmail()
    {
        await using var db = NewDb();

        var result = await NewService(db, autoProvision: true)
            .SignInAsync(PrincipalWithAssurance("sub-entra", "new@external.tld"), null, null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Roles.ClientViewer, (await db.AgencyUsers.SingleAsync()).Role);
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
