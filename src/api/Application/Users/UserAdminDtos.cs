namespace DmarcAnalyzer.Api.Application.Users;

public sealed record UserAdminDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    // False for an account with no password, which can only sign in through OIDC.
    // Never carries the hash itself — only whether password sign-in is possible.
    bool HasPassword,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<Guid> GrantedClientIds);
