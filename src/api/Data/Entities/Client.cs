namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int RetentionMonths { get; set; } = 27;
    public string Timezone { get; set; } = "UTC";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<Domain> Domains { get; set; } = [];
}
