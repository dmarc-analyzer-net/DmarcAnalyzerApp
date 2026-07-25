namespace DmarcAnalyzer.Api.Application.Clients;

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
