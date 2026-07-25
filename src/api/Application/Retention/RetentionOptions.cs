namespace DmarcAnalyzer.Api.Application.Retention;

/// <summary>Retention settings that aren't per client (`Retention:*`).</summary>
public sealed class RetentionOptions
{
    /// <summary>
    /// How long the audit trail is kept, in days. Two years by default. Separate
    /// from a client's report retention because the trail is a compliance record
    /// spanning the whole install. Set 0 to keep it indefinitely.
    /// </summary>
    public int AuditRetentionDays { get; set; } = 730;
}
