namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// The protocol values that mean something to code, named once. Three separate places ask
/// "is this a mailbox we poll", and a string literal in each is how they drift apart.
/// </summary>
public static class ReportSourceProtocols
{
    /// <summary>Polled over IMAP by the worker. The only protocol with a mailbox behind it.</summary>
    public const string Imap = "imap";
}

public sealed class ReportSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = "imap";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool UseTls { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string PasswordEncrypted { get; set; } = string.Empty;
    public Guid DefaultClientId { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Delete report mail from this mailbox once it is older than the retention window the
    /// app enforces on itself.
    /// <para>
    /// Off by default, because deleting a customer's mail is not a default behaviour and
    /// some operators poll a shared mailbox that other tooling also reads. Turning it on
    /// is what gives the system one retention window instead of two: without it, the same
    /// personal data the daily purge removes from the database sits in the mailbox
    /// indefinitely, which makes an erasure request impossible to satisfy — the reports
    /// come back on the next sync.
    /// </para>
    /// </summary>
    public bool DeleteAfterRetention { get; set; }

    /// <summary>
    /// Internal date of the oldest message still in the polled folder, refreshed on each
    /// sync. Null until a sync has looked.
    /// <para>
    /// This is the evidence for the claim that the mailbox is a usable archive. Compared
    /// against the oldest report in the database it answers "how far back could we
    /// actually replay?", and after a deletion pass it is how an operator confirms the cut
    /// landed where it was supposed to.
    /// </para>
    /// </summary>
    public DateTime? OldestMessageAtUtc { get; set; }

    public DateTime? LastSuccessSyncAtUtc { get; set; }
    public long? LastProcessedUid { get; set; }
    public long? LastProcessedUidValidity { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Client? DefaultClient { get; set; }
}
