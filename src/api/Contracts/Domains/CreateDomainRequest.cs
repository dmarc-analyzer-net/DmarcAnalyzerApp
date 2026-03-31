namespace DmarcAnalyzer.Api.Contracts.Domains;

public sealed class CreateDomainRequest
{
    public Guid ClientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
