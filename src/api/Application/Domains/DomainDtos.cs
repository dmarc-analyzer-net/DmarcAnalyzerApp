namespace DmarcAnalyzer.Api.Application.Domains;

/// <summary>A monitored domain and the client that owns it.</summary>
public sealed record DomainDto(
    Guid Id,
    string Name,
    bool IsActive,
    Guid ClientId,
    string? ClientName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
