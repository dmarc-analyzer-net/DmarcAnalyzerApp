namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// An immutable record of who did what. Actor details are denormalised on
/// purpose: an audit trail that loses its meaning when a user row is deleted
/// isn't an audit trail, so the email is copied in rather than joined.
/// </summary>
public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>`user`, `system`, or `anonymous` (a failed sign-in has no actor yet).</summary>
    public string ActorType { get; set; } = "user";

    public Guid? ActorUserId { get; set; }

    /// <summary>Copied at write time so the record survives the user being deleted.</summary>
    public string ActorEmail { get; set; } = string.Empty;

    /// <summary>Dotted event name, e.g. `auth.login.succeeded`, `client.updated`.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>What was acted on, e.g. `client`, `domain`, `mailbox_source`.</summary>
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }

    /// <summary>Set when the event concerns one client, so the trail can be filtered per tenant.</summary>
    public Guid? ClientId { get; set; }

    public string Summary { get; set; } = string.Empty;

    /// <summary>Optional extra context. Never store credentials here.</summary>
    public string? Details { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}
