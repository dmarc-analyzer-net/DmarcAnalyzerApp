namespace DmarcAnalyzer.Api.Contracts.Clients;

public sealed class UpdateClientRequest
{
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public bool? IsActive { get; set; }
    public int? RetentionMonths { get; set; }
    public string? Timezone { get; set; }
}
