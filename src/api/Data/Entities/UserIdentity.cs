namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>Maps an external identity (OIDC issuer + subject) to a local agency user.</summary>
public sealed class UserIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string? EmailAtLink { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAtUtc { get; set; }

    public AgencyUser? User { get; set; }
}
