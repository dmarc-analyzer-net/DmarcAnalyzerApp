namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// How one client reaches a several-clients recipient (one whose
/// <see cref="NotificationRecipient.ClientId"/> is null). Clients without a row take the
/// recipient's <see cref="NotificationRecipient.DigestDefaultMode"/>, so an "all clients"
/// address picks up new clients automatically and a customer contact with a default of
/// <c>off</c> never does.
/// </summary>
public sealed class NotificationRecipientClient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecipientId { get; set; }
    public Guid ClientId { get; set; }

    /// <summary>
    /// <c>rollup</c> (in the recipient's one combined digest), <c>separate</c> (its own
    /// digest mail) or <c>off</c> (not covered: no digest and no alerts for this client).
    /// </summary>
    public string DigestMode { get; set; } = DigestModes.Rollup;

    public NotificationRecipient? Recipient { get; set; }
    public Client? Client { get; set; }
}

/// <summary>The values of <see cref="NotificationRecipientClient.DigestMode"/> and <see cref="NotificationRecipient.DigestDefaultMode"/>.</summary>
public static class DigestModes
{
    public const string Rollup = "rollup";
    public const string Separate = "separate";
    public const string Off = "off";

    public static readonly string[] All = [Rollup, Separate, Off];

    public static bool IsValid(string? mode) => mode is not null && All.Contains(mode);
}
