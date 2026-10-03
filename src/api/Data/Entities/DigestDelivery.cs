namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// A digest mail that was attempted: one row per address, per mail, per period. Exists
/// to make sending idempotent — the unique (RecipientEmail, ClientId, PeriodStartUtc)
/// index, with nulls not distinct, is what stops a restart or an extra worker pass from
/// emailing the same month twice.
/// <para>
/// A null <see cref="ClientId"/> is a roll-up covering several clients. A null
/// <see cref="RecipientEmail"/> is a row written before digests were per recipient, when
/// one mail per client went to every recipient at once; it still marks that client's
/// period as sent for everyone.
/// </para>
/// </summary>
public sealed class DigestDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ClientId { get; set; }

    /// <summary>The address the mail went to; null on rows from before per-recipient digests.</summary>
    public string? RecipientEmail { get; set; }

    /// <summary>How many clients the mail covered: 1 for a single-client digest, more for a roll-up.</summary>
    public int ClientCount { get; set; } = 1;
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>0 when the digest was recorded but no email went out (no relay or no recipients).</summary>
    public int RecipientCount { get; set; }

    public Client? Client { get; set; }
}
