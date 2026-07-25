namespace DmarcAnalyzer.Api.Application.Clients;

public sealed record ClientDto(
    Guid Id,
    string Name,
    string Slug,
    bool IsActive,
    int RetentionMonths,
    bool LegalHold,
    string Timezone,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
