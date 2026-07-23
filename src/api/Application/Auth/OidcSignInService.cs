using System.Security.Claims;
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
        var emailVerified = string.Equals(principal.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
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

        // 2. Link to an existing local account by verified email only — an
        //    unverified IdP email must never take over a local account.
        if (!string.IsNullOrWhiteSpace(email))
        {
            var existing = await db.AgencyUsers.SingleOrDefaultAsync(x => x.Email == email, ct);
            if (existing is not null)
            {
                if (!emailVerified)
                {
                    logger.LogWarning("OIDC login for {Email} refused: email not verified at IdP", email);
                    return OidcSignInResult.Failure("email_not_verified");
                }

                await AddIdentityAsync(existing.Id, issuer, subject, email, ct);
                logger.LogInformation("Linked OIDC identity {Issuer}/{Subject} to existing user {UserId}", issuer, subject, existing.Id);
                return await MintSessionAsync(existing.Id, ipAddress, userAgent, ct);
            }
        }

        // 3. Just-in-time provisioning.
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

        return await MintSessionAsync(user.Id, ipAddress, userAgent, ct);
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
