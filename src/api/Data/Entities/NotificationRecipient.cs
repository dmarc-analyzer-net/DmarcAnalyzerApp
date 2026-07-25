namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// Who receives notifications. A null <see cref="ClientId"/> means agency-wide:
/// that address receives notifications for every client.
/// </summary>
public sealed class NotificationRecipient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ClientId { get; set; }
    public string Email { get; set; } = string.Empty;

    /// <summary>`alert`, `digest`, or `both`.</summary>
    public string Kind { get; set; } = "both";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Client? Client { get; set; }
}
