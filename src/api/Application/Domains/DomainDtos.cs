namespace DmarcAnalyzer.Api.Application.Domains;

public sealed record DomainDto(
    Guid Id,
    string Name,
    bool IsActive,
    Guid ClientId,
    string? ClientName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
