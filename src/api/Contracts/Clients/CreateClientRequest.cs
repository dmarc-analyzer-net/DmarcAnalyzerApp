namespace DmarcAnalyzer.Api.Contracts.Clients;

public sealed class CreateClientRequest
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int RetentionMonths { get; set; } = 27;
    public string Timezone { get; set; } = "UTC";
}
