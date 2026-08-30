namespace DmarcAnalyzer.Api.Contracts.Domains;

/// <summary>Body of POST /domains.</summary>
public sealed class CreateDomainRequest
{
    public Guid ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
