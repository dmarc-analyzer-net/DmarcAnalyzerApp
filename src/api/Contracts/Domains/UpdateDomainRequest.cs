namespace DmarcAnalyzer.Api.Contracts.Domains;

public sealed class UpdateDomainRequest
{
    public Guid? ClientId { get; set; }
    public string? Name { get; set; }
    public bool? IsActive { get; set; }
}
