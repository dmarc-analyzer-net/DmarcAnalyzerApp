namespace DmarcAnalyzer.Api.Application.Auth;

public sealed record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record LoginResultDto(
    UserDto User,
    string CookieId);

public sealed record SessionUserDto(
    UserDto User,
    IReadOnlyList<Guid> GrantedClientIds);
