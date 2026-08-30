namespace DmarcAnalyzer.Api.Contracts.Clients;

/// <summary>Body of POST /clients. The 27-month retention default covers two full DMARC year-over-years.</summary>
public sealed class CreateClientRequest
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int RetentionMonths { get; set; } = 27;
    public string Timezone { get; set; } = "UTC";
}
