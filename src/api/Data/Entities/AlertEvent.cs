namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// A raised alert. Persisted rather than fired-and-forgotten so the same problem
/// isn't emailed repeatedly (see the cooldown in the evaluation service) and so
/// operators can see history.
/// </summary>
public sealed class AlertEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }

    /// <summary>Null for client-wide alerts that aren't about one domain.</summary>
    public Guid? DomainId { get; set; }

    /// <summary>
    /// `failure_spike`, `policy_regression`, `mta_sts_policy_change`,
    /// `mta_sts_broken` or `mta_sts_mx_mismatch`.
    /// </summary>
    public string RuleType { get; set; } = string.Empty;

    /// <summary>`info`, `warning`, or `critical`.</summary>
    public string Severity { get; set; } = "warning";

    /// <summary>`open`, `acknowledged`, or `closed`.</summary>
    public string Status { get; set; } = "open";

    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;

    public DateTime DetectedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the notification email went out; null if delivery is off or failed.</summary>
    public DateTime? NotifiedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Client? Client { get; set; }
    public Domain? Domain { get; set; }
}
