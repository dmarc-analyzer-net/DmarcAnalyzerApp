namespace DmarcAnalyzer.Api.Contracts.Clients;

public sealed class UpdateClientRequest
{
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public bool? IsActive { get; set; }
    public int? RetentionMonths { get; set; }
    public bool? LegalHold { get; set; }
    public bool? AlertsEnabled { get; set; }

    /// <summary>Null clears the override so the configured default applies again.</summary>
    public int? AlertComplianceDropPercent { get; set; }
    public int? AlertMinMessages { get; set; }

    /// <summary>Set true to clear the threshold overrides (a null value alone is indistinguishable from "unchanged").</summary>
    public bool? ClearAlertThresholds { get; set; }
    public string? Timezone { get; set; }
}
