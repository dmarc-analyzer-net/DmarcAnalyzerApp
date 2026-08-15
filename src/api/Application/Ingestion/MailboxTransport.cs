using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Data.Entities;
using MimeKit;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// One message a pass has yet to handle.
/// </summary>
/// <param name="Token">
/// How the <em>open session</em> refers to this message: the IMAP UID, or the POP3 index
/// within this session's listing. Meaningless once the session closes — POP3 renumbers from
/// 1 on every connection — which is why it is not what gets checkpointed.
/// </param>
/// <param name="Identity">
/// How the message is named <em>durably</em>: the IMAP UID as a string, or the POP3 UIDL.
/// This is what a later pass matches against, and what the checkpoint stores.
/// </param>
/// <param name="ArchiveIdentity">The key this message is archived under, if archiving is on.</param>
public sealed record MailboxMessageRef(
    long Token,
    string Identity,
    ReportMailIdentity ArchiveIdentity);

/// <summary>
/// A message old enough for the retention pass to consider deleting.
/// </summary>
/// <param name="ReceivedAtUtc">
/// When it arrived, as the protocol can tell it. Only used for logging and for the archive
/// key — whether it is past the cutoff has already been decided by the session that
/// produced it.
/// </param>
public sealed record MailboxPruneCandidate(
    long Token,
    DateTime ReceivedAtUtc,
    ReportMailIdentity ArchiveIdentity);

/// <summary>
/// An open, read-only mailbox: what a sync pass needs and nothing else.
/// <para>
/// The point of the seam is that the hard part of a sync — the drain budget, the batched
/// checkpoint commits, the archive-before-parse rule, the counters, the run row, the
/// partial-versus-failed distinction — is protocol-independent, and duplicating it per
/// protocol is how the two implementations would drift. What actually differs between IMAP
/// and POP3 is only the four things below.
/// </para>
/// </summary>
public interface IMailboxReadSession : IAsyncDisposable
{
    /// <summary>
    /// The messages past the checkpoint, oldest first. Ordering is part of the contract:
    /// the drain loop's batch boundaries and the oldest-to-newest backfill both depend on
    /// it, and so does the checkpoint, which is only valid if everything before it is done.
    /// </summary>
    IReadOnlyList<MailboxMessageRef> Pending { get; }

    /// <summary>
    /// The mailbox's own claim about how far back it reaches, if the protocol makes that
    /// cheap to answer while the session is open. Null when it does not.
    /// </summary>
    DateTime? OldestMessageAtUtc { get; }

    Task<MimeMessage> FetchAsync(MailboxMessageRef message, CancellationToken ct);

    /// <summary>
    /// Records which generation of the mailbox this pass read, whether or not it handled
    /// anything. IMAP writes UIDVALIDITY; POP3 has no such concept and writes nothing.
    /// </summary>
    void ApplyGeneration(ReportSource source);

    /// <summary>
    /// Records that everything up to and including this message is done. Called at batch
    /// boundaries and at the end, so a pass that dies mid-drain costs one batch rather than
    /// the whole backlog.
    /// </summary>
    void ApplyCheckpoint(ReportSource source, MailboxMessageRef handled);

    Task CloseAsync(CancellationToken ct);
}

/// <summary>
/// An open mailbox the retention pass may delete from. Separate from
/// <see cref="IMailboxReadSession"/> because it is a different act: this is the only thing in
/// the application that destroys data it does not own, and it opens the mailbox for writing
/// to do it.
/// </summary>
public interface IMailboxPruneSession : IAsyncDisposable
{
    /// <summary>Everything already established to be older than the cutoff.</summary>
    IReadOnlyList<MailboxPruneCandidate> Eligible { get; }

    /// <summary>Marks one message for deletion. Not necessarily effective until <see cref="CommitAsync"/>.</summary>
    Task DeleteAsync(MailboxPruneCandidate candidate, CancellationToken ct);

    /// <summary>
    /// Makes the deletions permanent. IMAP expunges here; POP3 cannot, and does it as part
    /// of <see cref="CloseAsync"/> — see the note there.
    /// </summary>
    Task CommitAsync(CancellationToken ct);

    /// <summary>
    /// How far back the mailbox still reaches, asked after the deletions so the answer
    /// reflects them.
    /// </summary>
    Task<DateTime?> GetOldestMessageAtUtcAsync(CancellationToken ct);

    Task CloseAsync(CancellationToken ct);
}

/// <summary>
/// Opens a mailbox over whichever protocol a source speaks.
/// </summary>
public interface IMailboxTransport
{
    /// <summary>The <see cref="ReportSourceProtocols"/> value this transport handles.</summary>
    string Protocol { get; }

    /// <summary>
    /// Connects, authenticates, and works out what is left to do. The checkpoint is read
    /// off <paramref name="source"/> — each transport knows which of its columns is the one
    /// that means something.
    /// </summary>
    Task<IMailboxReadSession> OpenForReadAsync(
        ReportSource source,
        string password,
        CancellationToken ct);

    /// <param name="dryRun">
    /// When true the mailbox is opened read-only where the protocol allows it, and nothing
    /// is marked for deletion. A dry run must be incapable of deleting, not merely
    /// uninterested in it.
    /// </param>
    Task<IMailboxPruneSession> OpenForPruneAsync(
        ReportSource source,
        string password,
        DateTime cutoffUtc,
        bool dryRun,
        CancellationToken ct);
}

/// <summary>
/// Picks the transport for a source. Registered as a singleton over the registered
/// transports, so adding a protocol is adding an <see cref="IMailboxTransport"/> and a
/// constant — not another branch in the sync service.
/// </summary>
public interface IMailboxTransportFactory
{
    /// <summary>
    /// The transport for this protocol, or null when the protocol has no mailbox behind it.
    /// Null rather than an exception: "this source is not polled" is an ordinary answer that
    /// the sync service turns into a 400, and a pushed source reaching here is a caller
    /// mistake rather than a fault.
    /// </summary>
    IMailboxTransport? For(string protocol);
}

public sealed class MailboxTransportFactory(IEnumerable<IMailboxTransport> transports) : IMailboxTransportFactory
{
    private readonly Dictionary<string, IMailboxTransport> _byProtocol =
        transports.ToDictionary(x => x.Protocol, StringComparer.OrdinalIgnoreCase);

    public IMailboxTransport? For(string protocol)
        => _byProtocol.TryGetValue(protocol?.Trim() ?? string.Empty, out var transport) ? transport : null;
}
