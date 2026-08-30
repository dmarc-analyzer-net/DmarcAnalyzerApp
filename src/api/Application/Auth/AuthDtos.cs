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

/// <summary>The /auth/me payload: who is signed in and, for client viewers, which clients they may see.</summary>
public sealed record SessionUserDto(
    UserDto User,
    IReadOnlyList<Guid> GrantedClientIds);
