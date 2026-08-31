namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>A user as the console sees one — no password hash, no identities.</summary>
public sealed record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>A successful sign-in: the user plus the session id the cookie will carry.</summary>
public sealed record LoginResultDto(
    UserDto User,
    string CookieId);

/// <summary>
/// The session-resolution result SessionAuthMiddleware scopes each request
/// with: the user plus, for client viewers, their granted clients.
/// </summary>
public sealed record SessionUserDto(
    UserDto User,
    IReadOnlyList<Guid> GrantedClientIds);
