using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Auth;

namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>
/// Local (password) authentication and the session store both login paths share —
/// OIDC completion also lands in <see cref="LoginWithExternalIdentityAsync"/> and
/// mints the same session row.
/// </summary>
public interface IAuthService
{
    /// <summary>True while no user exists yet — the setup screen's one-time window.</summary>
    Task<bool> RequiresBootstrapAsync(CancellationToken ct);

    /// <summary>
    /// Creates the first admin account. Refuses itself once any user exists;
    /// after that, users are created through user administration.
    /// </summary>
    Task<ServiceResult<UserDto>> RegisterAsync(RegisterRequest request, CancellationToken ct);

    /// <summary>Password sign-in. Failures are audited with the attempted email; the error never says which half was wrong.</summary>
    Task<ServiceResult<LoginResultDto>> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct);

    /// <summary>Session minting for an already-verified external (OIDC) identity — no password involved.</summary>
    Task<ServiceResult<LoginResultDto>> LoginWithExternalIdentityAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken ct);

    /// <summary>Deletes the session row; the cookie is cleared by the endpoint.</summary>
    Task LogoutAsync(string cookieId, CancellationToken ct);

    /// <summary>The user behind a session cookie, or null when the session is missing or expired.</summary>
    Task<UserDto?> GetCurrentUserAsync(string cookieId, CancellationToken ct);

    /// <summary>Same as <see cref="GetCurrentUserAsync"/> plus the client grants, for scoping the request.</summary>
    Task<SessionUserDto?> GetSessionUserAsync(string cookieId, CancellationToken ct);
}
