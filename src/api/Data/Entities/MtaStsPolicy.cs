namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// A hosted MTA-STS policy: what this instance serves at
/// https://mta-sts.{domain}/.well-known/mta-sts.txt for a domain whose
/// mta-sts CNAME points here.
///
/// Inside-out serving config, deliberately separate from <see cref="MtaStsState"/>
/// (outside-in monitoring): once the operator's CNAME is live, the check pass
/// validates a hosted policy exactly like any external one, with zero extra code.
/// </summary>
public sealed class MtaStsPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }

    /// <summary>Serving requires this and the domain being active; off keeps the settings but answers 404.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>enforce, testing or none.</summary>
    public string Mode { get; set; } = "testing";

    /// <summary>Seconds senders may cache the policy. RFC 8461 caps at 31557600.</summary>
    public int MaxAgeSeconds { get; set; } = 86400;

    /// <summary>
    /// Newline-joined mx patterns, normalized lowercase. Empty is legal only for
    /// mode none, but stored lines survive a mode switch so no data is lost.
    /// </summary>
    public string MxPatterns { get; set; } = string.Empty;

    /// <summary>
    /// The id= senders see in the _mta-sts TXT record. Server-generated
    /// (yyyyMMddHHmmss UTC) and bumped exactly when the rendered policy content
    /// changes — senders only refetch when it moves, so an unchanged id on a
    /// changed policy strands them on the old one until max_age expires.
    /// </summary>
    public string PolicyId { get; set; } = string.Empty;

    /// <summary>
    /// When <see cref="Mode"/> last changed (set on create too). The
    /// testing-clock input for the promotion gate: "how long has this domain
    /// been clean in testing" starts here.
    /// </summary>
    public DateTime ModeChangedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Domain? Domain { get; set; }
}
