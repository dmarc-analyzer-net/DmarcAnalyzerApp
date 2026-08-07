using System.Security.Claims;
using DmarcAnalyzer.Api.Application.Clients;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Auth;

public interface IOidcSignInService
{
    /// <summary>Resolves an authenticated external principal to a local session. Returns an error code on failure.</summary>
    Task<OidcSignInResult> SignInAsync(ClaimsPrincipal principal, string? ipAddress, string? userAgent, CancellationToken ct);
}

public sealed record OidcSignInResult(string? CookieId, string? ErrorCode)
{
    public bool IsSuccess => CookieId is not null;

    public static OidcSignInResult Success(string cookieId) => new(cookieId, null);
    public static OidcSignInResult Failure(string errorCode) => new(null, errorCode);
}

public sealed class OidcSignInService(
    DmarcAnalyzerDbContext db,
    IAuthService authService,
    IOptions<OidcOptions> options,
    ILogger<OidcSignInService> logger) : IOidcSignInService
{
    public async Task<OidcSignInResult> SignInAsync(ClaimsPrincipal principal, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        // The issuer is the provider we configured, not a principal claim (the
        // handler does not always surface "iss"). The subject is the stable
        // per-user id from the token.
        var issuer = options.Value.Authority.Trim();
        var subject = principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning("OIDC sign-in failed: missing issuer or subject claim");
            return OidcSignInResult.Failure("oidc_failed");
        }

        var email = principal.FindFirstValue("email")?.Trim().ToLowerInvariant();
        var assurance = ResolveEmailAssurance(principal);
        var displayName = principal.FindFirstValue("name")?.Trim();

        // 1. Known external identity.
        var identity = await db.UserIdentities
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.Issuer == issuer && x.Subject == subject, ct);

        if (identity is not null)
        {
            identity.LastLoginAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return await MintSessionAsync(identity.UserId, ipAddress, userAgent, ct);
        }

        // 2. Link to an existing local account by email, but only on an assurance
        //    the provider actually gave — an unverified IdP email must never take
        //    over a local account. A provider that asserts nothing (Entra) is
        //    refused unless the deployment has opted into trusting its silence.
        if (!string.IsNullOrWhiteSpace(email))
        {
            var existing = await db.AgencyUsers.SingleOrDefaultAsync(x => x.Email == email, ct);
            if (existing is not null)
            {
                if (assurance is EmailAssurance.NotVerified)
                {
                    logger.LogWarning("OIDC login for {Email} refused: email not verified at IdP", email);
                    return OidcSignInResult.Failure("email_not_verified");
                }

                if (assurance is EmailAssurance.Unstated && !options.Value.TrustUnverifiedEmail)
                {
                    logger.LogWarning(
                        "OIDC login for {Email} refused: the provider asserted neither email_verified nor xms_edov, " +
                        "so the address cannot be trusted to identify the existing account. Configure the xms_edov " +
                        "optional claim (Entra ID), or set Auth:Oidc:TrustUnverifiedEmail=true if this provider's " +
                        "addresses are administered.", email);
                    return OidcSignInResult.Failure("email_verification_unknown");
                }

                await AddIdentityAsync(existing.Id, issuer, subject, email, ct);
                logger.LogInformation("Linked OIDC identity {Issuer}/{Subject} to existing user {UserId}", issuer, subject, existing.Id);
                return await MintSessionAsync(existing.Id, ipAddress, userAgent, ct);
            }
        }

        // 3. Just-in-time provisioning. Deliberately asymmetric with step 2: this
        //    path asks nothing about email assurance, because there is no account
        //    to take over. A fresh account inherits no role and no client grants
        //    beyond DefaultRole, so an unverified address buys the least
        //    privilege the deployment has. Step 2 is where assurance matters,
        //    since the account on the other side may be an admin's.
        if (!options.Value.AutoProvision)
        {
            return OidcSignInResult.Failure("no_account");
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return OidcSignInResult.Failure("no_account");
        }

        var role = options.Value.DefaultRole.Trim().ToLowerInvariant();
        if (!Roles.IsValid(role))
        {
            logger.LogError("Auth:Oidc:DefaultRole '{Role}' is not a valid role; refusing auto-provision", options.Value.DefaultRole);
            return OidcSignInResult.Failure("oidc_failed");
        }

        var now = DateTime.UtcNow;
        var user = new AgencyUser
        {
            Email = email,
            // Empty hash can never pass password verification, so provisioned
            // accounts have no password back door.
            PasswordHash = string.Empty,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName,
            Role = role,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.AgencyUsers.Add(user);
        await db.SaveChangesAsync(ct);

        await AddIdentityAsync(user.Id, issuer, subject, email, ct);
        logger.LogInformation("Auto-provisioned user {UserId} ({Email}) with role {Role} from {Issuer}", user.Id, email, role, issuer);

        // An SSO-only deployment can be bootstrapped entirely through here, never touching
        // local registration, so this path has to establish the default client as well —
        // otherwise "the client always exists" would hold for password installs only.
        await DefaultClient.EnsureAsync(db, ct);

        return await MintSessionAsync(user.Id, ipAddress, userAgent, ct);
    }

    /// <summary>
    /// What the provider said about the address — which is not the same question
    /// as whether it is verified. Silence is its own answer and has to be
    /// distinguishable from a "no": Entra ID never issues <c>email_verified</c>,
    /// so reading a missing claim as unverified refuses every Entra user forever
    /// (#140), while reading it as verified would trust an address no one
    /// vouched for.
    /// </summary>
    private enum EmailAssurance
    {
        Verified,
        NotVerified,
        Unstated,
    }

    private static EmailAssurance ResolveEmailAssurance(ClaimsPrincipal principal)
    {
        // The standard claim first. xms_edov ("email domain owner verified") is
        // the optional claim Entra offers instead, added by Microsoft for exactly
        // this gap — it is a genuine assertion, so it counts the same and needs
        // no configured trust.
        return Read("email_verified") ?? Read("xms_edov") ?? EmailAssurance.Unstated;

        EmailAssurance? Read(string claimType) => principal.FindFirstValue(claimType)?.Trim() switch
        {
            null or "" => null,
            var value when IsTrue(value) => EmailAssurance.Verified,
            _ => EmailAssurance.NotVerified,
        };

        // A JSON boolean reaches here as a string, and which string depends on the
        // handler and the provider, so match the forms that mean true rather than
        // pinning one and silently reading the others as a refusal.
        static bool IsTrue(string value) =>
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";
    }

    private async Task AddIdentityAsync(Guid userId, string issuer, string subject, string? email, CancellationToken ct)
    {
        db.UserIdentities.Add(new UserIdentity
        {
            UserId = userId,
            Issuer = issuer,
            Subject = subject,
            EmailAtLink = email,
            CreatedAtUtc = DateTime.UtcNow,
            LastLoginAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<OidcSignInResult> MintSessionAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        var result = await authService.LoginWithExternalIdentityAsync(userId, ipAddress, userAgent, ct);
        return result.IsSuccess
            ? OidcSignInResult.Success(result.Value!.CookieId)
            : OidcSignInResult.Failure("account_disabled");
    }
}
