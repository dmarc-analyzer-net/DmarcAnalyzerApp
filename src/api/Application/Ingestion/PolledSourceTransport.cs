using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Data.Entities;
using MimeKit;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// One item a pass has yet to handle: a message in a mailbox, or an object in a bucket.
/// </summary>
/// <param name="Token">
/// How the <em>open session</em> refers to this item: the IMAP UID, the POP3 index within
/// this session's listing, or the position in the S3 listing. Meaningless once the session
/// closes — POP3 renumbers from 1 on every connection — which is why it is not what gets
/// checkpointed.
/// </param>
/// <param name="Identity">
/// How the item is named <em>durably</em>: the IMAP UID as a string, the POP3 UIDL, or the S3
/// object key. This is what a later pass matches against, and what the checkpoint stores.
/// </param>
/// <param name="ArchiveIdentity">The key this item is archived under, if archiving is on.</param>
public sealed record PolledItemRef(
    long Token,
    string Identity,
    ReportMailIdentity ArchiveIdentity);

/// <summary>
/// An item old enough for the retention pass to consider deleting.
/// </summary>
/// <param name="ReceivedAtUtc">
/// When it arrived, as the protocol can tell it. Only used for logging and for the archive
/// key — whether it is past the cutoff has already been decided by the session that
/// produced it.
/// </param>
public sealed record PolledPruneCandidate(
    long Token,
    DateTime ReceivedAtUtc,
    ReportMailIdentity ArchiveIdentity);

/// <summary>
/// An open, read-only report source: what a sync pass needs and nothing else.
/// <para>
/// The point of the seam is that the hard part of a sync — the drain budget, the batched
/// checkpoint commits, the archive-before-parse rule, the counters, the run row, the
/// partial-versus-failed distinction — is protocol-independent, and duplicating it per
/// protocol is how the implementations would drift. What actually differs between IMAP, POP3
/// and S3 is only the handful of things below.
/// </para>
/// <para>
/// <see cref="FetchAsync"/> hands back a <see cref="MimeMessage"/> for all three, which is
/// exact for the two mail protocols and a deliberate wrapper for the third: a bucket holding
/// bare report files has no message, so the S3 transport puts each object in a stub message
/// as a single attachment. That is the same trick the pushed-ingestion endpoint already uses
/// on a raw request body, and it is what lets one extraction path serve every source. The
/// wrapper carries no invented provenance — see <c>PolledObjectContent</c>.
/// </para>
/// </summary>
public interface IPolledReadSession : IAsyncDisposable
{
    /// <summary>
    /// The items past the checkpoint, oldest first. Ordering is part of the contract: the
    /// drain loop's batch boundaries and the oldest-to-newest backfill both depend on it, and
    /// so does the checkpoint, which is only valid if everything before it is done.
    /// </summary>
    IReadOnlyList<PolledItemRef> Pending { get; }

    /// <summary>
    /// The source's own claim about how far back it reaches, if the protocol makes that cheap
    /// to answer while the session is open. Null when it does not.
    /// </summary>
    DateTime? OldestMessageAtUtc { get; }

    Task<MimeMessage> FetchAsync(PolledItemRef item, CancellationToken ct);

    /// <summary>
    /// Records which generation of the source this pass read, whether or not it handled
    /// anything. IMAP writes UIDVALIDITY; POP3 and S3 have no such concept and write nothing.
    /// </summary>
    void ApplyGeneration(ReportSource source);

    /// <summary>
    /// Records that everything up to and including this item is done. Called at batch
    /// boundaries and at the end, so a pass that dies mid-drain costs one batch rather than
    /// the whole backlog.
    /// </summary>
    void ApplyCheckpoint(ReportSource source, PolledItemRef handled);

    Task CloseAsync(CancellationToken ct);
}

/// <summary>
/// An open report source the retention pass may delete from. Separate from
/// <see cref="IPolledReadSession"/> because it is a different act: this is the only thing in
/// the application that destroys data it does not own, and it opens the source for writing
/// to do it.
/// </summary>
public interface IPolledPruneSession : IAsyncDisposable
{
    /// <summary>Everything already established to be older than the cutoff.</summary>
    IReadOnlyList<PolledPruneCandidate> Eligible { get; }

    /// <summary>Marks one item for deletion. Not necessarily effective until <see cref="CommitAsync"/>.</summary>
    Task DeleteAsync(PolledPruneCandidate candidate, CancellationToken ct);

    /// <summary>
    /// Makes the deletions permanent, for the protocols where that is a separate act. IMAP
    /// expunges here; POP3 cannot and does it as part of <see cref="CloseAsync"/>; S3 has no
    /// two-phase delete at all and is already done.
    /// </summary>
    Task CommitAsync(CancellationToken ct);

    /// <summary>
    /// How far back the source still reaches, asked after the deletions so the answer
    /// reflects them.
    /// </summary>
    Task<DateTime?> GetOldestMessageAtUtcAsync(CancellationToken ct);

    Task CloseAsync(CancellationToken ct);
}

/// <summary>
/// Opens a report source over whichever protocol it speaks.
/// </summary>
public interface IPolledSourceTransport
{
    /// <summary>The <see cref="ReportSourceProtocols"/> value this transport handles.</summary>
    string Protocol { get; }

    /// <summary>
    /// Connects, authenticates, and works out what is left to do. The checkpoint is read
    /// off <paramref name="source"/> — each transport knows which of its columns is the one
    /// that means something.
    /// </summary>
    /// <param name="secret">
    /// The source's decrypted secret: a mailbox password, or an S3 secret access key. Empty
    /// where the protocol can authenticate without one — an S3 source with no key falls back
    /// to the ambient credential chain, which is what an instance role or IRSA looks like.
    /// </param>
    Task<IPolledReadSession> OpenForReadAsync(
        ReportSource source,
        string secret,
        CancellationToken ct);

    /// <param name="dryRun">
    /// When true the source is opened read-only where the protocol allows it, and nothing is
    /// marked for deletion. A dry run must be incapable of deleting, not merely uninterested
    /// in it.
    /// </param>
    Task<IPolledPruneSession> OpenForPruneAsync(
        ReportSource source,
        string secret,
        DateTime cutoffUtc,
        bool dryRun,
        CancellationToken ct);
}

/// <summary>
/// Picks the transport for a source. Registered as a singleton over the registered
/// transports, so adding a protocol is adding an <see cref="IPolledSourceTransport"/> and a
/// constant — not another branch in the sync service.
/// </summary>
public interface IPolledSourceTransportFactory
{
    /// <summary>
    /// The transport for this protocol, or null when the protocol is not polled at all.
    /// Null rather than an exception: "this source is not polled" is an ordinary answer that
    /// the sync service turns into a 400, and a pushed source reaching here is a caller
    /// mistake rather than a fault.
    /// </summary>
    IPolledSourceTransport? For(string protocol);
}

public sealed class PolledSourceTransportFactory(IEnumerable<IPolledSourceTransport> transports) : IPolledSourceTransportFactory
{
    private readonly Dictionary<string, IPolledSourceTransport> _byProtocol =
        transports.ToDictionary(x => x.Protocol, StringComparer.OrdinalIgnoreCase);

    public IPolledSourceTransport? For(string protocol)
        => _byProtocol.TryGetValue(protocol?.Trim() ?? string.Empty, out var transport) ? transport : null;
}
