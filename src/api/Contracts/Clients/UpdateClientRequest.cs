namespace DmarcAnalyzer.Api.Contracts.Clients;

/// <summary>
/// Note the absence of <c>Slug</c>: it is set once at creation and immutable after, because
/// it is the identity an export is matched on when a configuration import merges clients
/// (see <c>BackupImportService.ImportClients</c>), and the domain list keys the
/// "needs client" flag off the default client's slug. Renaming one silently re-points both.
/// </summary>
public sealed class UpdateClientRequest
{
    public string? Name { get; set; }
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
