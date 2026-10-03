namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// Who receives notifications. A set <see cref="ClientId"/> covers that one client.
/// A null one covers several: every client by default, narrowed or widened per client
/// by <see cref="ClientModes"/> and <see cref="DigestDefaultMode"/>. The same coverage
/// decides alerts, so a client set to <c>off</c> sends this address nothing at all.
/// </summary>
public sealed class NotificationRecipient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ClientId { get; set; }
    public string Email { get; set; } = string.Empty;

    /// <summary>`alert`, `digest`, or `both`.</summary>
    public string Kind { get; set; } = "both";

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Several-clients recipients only: the digest mode (see <see cref="DigestModes"/>)
    /// for any client without a row in <see cref="ClientModes"/>, including clients added
    /// later. Ignored when <see cref="ClientId"/> is set.
    /// </summary>
    public string DigestDefaultMode { get; set; } = DigestModes.Rollup;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Client? Client { get; set; }
    public List<NotificationRecipientClient> ClientModes { get; set; } = [];
}
