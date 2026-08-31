namespace DmarcAnalyzer.Api.Application.Clients;

/// <summary>
/// A client (tenant) as the console reads it — identity, retention settings,
/// and the per-client alert thresholds (null means the global defaults apply).
/// </summary>
public sealed record ClientDto(
    Guid Id,
    string Name,
    string Slug,
    bool IsActive,
    int RetentionMonths,
    bool LegalHold,
    bool AlertsEnabled,
    int? AlertComplianceDropPercent,
    int? AlertMinMessages,
    string Timezone,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
