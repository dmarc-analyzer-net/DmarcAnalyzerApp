namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class UserSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string CookieId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public AgencyUser User { get; set; } = null!;
}
