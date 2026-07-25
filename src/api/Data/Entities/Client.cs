namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int RetentionMonths { get; set; } = 27;

    /// <summary>
    /// Exempts this client from retention purging entirely. Set when data must be
    /// preserved for a dispute or investigation regardless of the retention window.
    /// </summary>
    public bool LegalHold { get; set; }

    /// <summary>Turns alerting off for this client without affecting others.</summary>
    public bool AlertsEnabled { get; set; } = true;

    /// <summary>Compliance-drop threshold in percentage points; null uses the configured default.</summary>
    public int? AlertComplianceDropPercent { get; set; }

    /// <summary>Ignore days quieter than this when spotting a spike; null uses the configured default.</summary>
    public int? AlertMinMessages { get; set; }
    public string Timezone { get; set; } = "UTC";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<Domain> Domains { get; set; } = [];
}
