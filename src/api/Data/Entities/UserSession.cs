namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// A server-side session, named by the random CookieId the dmarc_session cookie
/// carries. Two clocks, both checked on every request: ExpiresAtUtc is the
/// absolute cap fixed at login, and LastSeenAtUtc drives the idle timeout.
/// RevokedAtUtc kills a session before either.
/// </summary>
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
