namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// A console user. Password sign-in and OIDC both land on this row; an empty
/// PasswordHash means OIDC-only. Role is one of <c>Roles.All</c> — grants in
/// <see cref="UserClientGrant"/> only matter for client_viewer.
/// </summary>
public sealed class AgencyUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "agency_admin";
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
