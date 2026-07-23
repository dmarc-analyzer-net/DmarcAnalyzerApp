namespace DmarcAnalyzer.Api.Application.Users;

public sealed record UserAdminDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<Guid> GrantedClientIds);
