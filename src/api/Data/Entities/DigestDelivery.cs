namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// A digest that was sent, one row per client per period. Exists to make sending
/// idempotent: the unique (ClientId, PeriodStartUtc) index is what stops a
/// restart or an extra worker pass from emailing the same month twice.
/// </summary>
public sealed class DigestDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>0 when the digest was recorded but no email went out (no relay or no recipients).</summary>
    public int RecipientCount { get; set; }

    public Client? Client { get; set; }
}
