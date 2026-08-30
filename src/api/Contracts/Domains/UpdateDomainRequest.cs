namespace DmarcAnalyzer.Api.Contracts.Domains;

/// <summary>Body of PATCH /domains/{id}; null fields stay unchanged.</summary>
public sealed class UpdateDomainRequest
{
    public Guid? ClientId { get; set; }
    public string? Name { get; set; }
    public bool? IsActive { get; set; }
}
