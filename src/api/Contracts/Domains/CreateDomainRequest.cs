namespace DmarcAnalyzer.Api.Contracts.Domains;

/// <summary>Body of POST /api/v1/domains.</summary>
public sealed class CreateDomainRequest
{
    public Guid ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
