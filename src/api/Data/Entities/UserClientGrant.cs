namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class UserClientGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid ClientId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }

    public AgencyUser? User { get; set; }
    public Client? Client { get; set; }
    public AgencyUser? CreatedByUser { get; set; }
}
