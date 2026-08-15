using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Data.Entities;
using MailKit.Net.Pop3;
using MailKit.Security;
using MimeKit;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// POP3, behind the same seam as IMAP. Everything a sync pass does around this — the drain
/// budget, the batched checkpoints, archive-before-parse, the run rows, partial-versus-failed
/// — is shared; what is here is only the four things the protocol does differently.
/// <para>
/// The differences that matter, all of them consequences of POP3 having no server-side state
/// beyond a delete flag:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>No UIDs and no UIDVALIDITY.</b> A message is named by its UIDL, an opaque string, and
/// referred to within a session by a position that is renumbered on every connection. So the
/// checkpoint is a UIDL and resuming means finding it in the listing rather than asking the
/// server for a range — see <see cref="MailboxSyncService.SelectUidlsPastCheckpoint"/>.
/// </description></item>
/// <item><description>
/// <b>No search.</b> There is no <c>DELIVEREDBEFORE</c>, so the retention pass reads every
/// message's headers to find what is past the cutoff. That is the real cost of POP3 here, and
/// why the pass logs how many headers it had to read.
/// </description></item>
/// <item><description>
/// <b>No INTERNALDATE.</b> Arrival time comes from the message's own <c>Date</c> header,
/// which is written by the sender rather than by the receiving server. It is the only date
/// POP3 offers.
/// </description></item>
/// <item><description>
/// <b>Deletion is not effective until QUIT.</b> <c>DELE</c> only marks; the server expunges
/// when the session ends cleanly, and a dropped connection or a <c>RSET</c> undoes every mark.
/// So the commit point is the disconnect, not a separate command.
/// </description></item>
/// </list>
/// </summary>
public sealed class Pop3MailboxTransport(ILogger<Pop3MailboxTransport> logger) : IPolledSourceTransport
{
    /// <summary>
    /// How many messages' headers to ask for in one call. MailKit pipelines a batch into a
    /// single round trip where the server allows it, so this is the difference between one
    /// request per message and one per few hundred on a mailbox with a real backlog. Bounded
    /// rather than unbounded because the whole batch is held in memory at once.
    /// </summary>
    private const int HeaderBatchSize = 200;

    public string Protocol => ReportSourceProtocols.Pop3;

    public async Task<IPolledReadSession> OpenForReadAsync(
        ReportSource source, string password, CancellationToken ct)
    {
        var client = new Pop3Client();

        try
        {
            await ConnectAsync(client, source, password, ct);

            // UIDL is optional in RFC 1939, and without it nothing durable can be
            // checkpointed: every pass would see an unrecognisable mailbox and re-read all
            // of it, for ever. Refused loudly rather than run in that state — the failure
            // lands on the source's health row with this text, which is the only way an
            // operator finds out. Deduplication would still keep the data correct; it is
            // the work that would be unbounded.
            if (!client.SupportsUids)
            {
                throw new NotSupportedException(
                    "the POP3 server does not support UIDL, so no durable checkpoint is possible " +
                    "and every sync would re-read the whole mailbox; use IMAP for this mailbox");
            }

            var uidls = (await client.GetMessageUidsAsync(ct)).AsReadOnly();
            var pending = MailboxSyncService.SelectUidlsPastCheckpoint(uidls, source.LastProcessedUidl);

            if (source.LastProcessedUidl is { Length: > 0 } checkpoint &&
                !uidls.Contains(checkpoint, StringComparer.Ordinal))
            {
                // The checkpointed message is no longer in the mailbox — deleted by the
                // retention pass at the wrong end, by another client reading the same
                // mailbox, or by hand. There is no ordering to fall back on, so the whole
                // mailbox is pending again. Dedup makes that safe and slow rather than
                // wrong, and saying so is what stops the re-read looking like a fault.
                logger.LogWarning(
                    "POP3 checkpoint {Checkpoint} is no longer in the mailbox for report source " +
                    "{ReportSourceId}; re-reading all {Count} message(s), which deduplication will " +
                    "absorb but which costs a full pass",
                    checkpoint, source.Id, uidls.Count);
            }

            var oldest = await GetOldestMessageAtUtcAsync(client, ct);

            return new Pop3ReadSession(client, pending, oldest);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<IPolledPruneSession> OpenForPruneAsync(
        ReportSource source, string password, DateTime cutoffUtc, bool dryRun, CancellationToken ct)
    {
        var client = new Pop3Client();

        try
        {
            await ConnectAsync(client, source, password, ct);

            if (!client.SupportsUids)
            {
                // The same refusal as the read path, for a different reason: without a UIDL
                // there is no key to check the archive under, and "no delete without a
                // confirmed write" is the rule this pass is built around.
                throw new NotSupportedException(
                    "the POP3 server does not support UIDL, so archived mail cannot be identified " +
                    "and nothing may be deleted safely");
            }

            var uidls = await client.GetMessageUidsAsync(ct);
            var candidates = new List<PolledPruneCandidate>();

            // No DELIVEREDBEFORE in POP3, so every message's headers get read to find the
            // ones past the cutoff. Batched, but still the expensive half of a POP3
            // retention pass — hence the count in the log line below.
            for (var start = 0; start < uidls.Count; start += HeaderBatchSize)
            {
                ct.ThrowIfCancellationRequested();

                var count = Math.Min(HeaderBatchSize, uidls.Count - start);
                var headers = await client.GetMessageHeadersAsync(start, count, ct);

                for (var offset = 0; offset < headers.Count; offset++)
                {
                    var index = start + offset;
                    var date = HeaderDateUtc(headers[offset]);

                    // A message with no usable Date header is left alone. Guessing an age
                    // for it would mean deleting a customer's mail on the strength of a
                    // guess, and the alternative — it stays — is the recoverable one.
                    if (date is not { } receivedAtUtc || receivedAtUtc >= cutoffUtc)
                    {
                        continue;
                    }

                    candidates.Add(new PolledPruneCandidate(
                        index, receivedAtUtc, ReportMailIdentity.ForPop3(uidls[index])));
                }
            }

            logger.LogInformation(
                "POP3 retention scan for report source {ReportSourceId} read {Read} message header(s) " +
                "and found {Eligible} past {Cutoff:yyyy-MM-dd}",
                source.Id, uidls.Count, candidates.Count, cutoffUtc);

            return new Pop3PruneSession(client, candidates, dryRun);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task ConnectAsync(
        Pop3Client client, ReportSource source, string password, CancellationToken ct)
    {
        var socketOptions = source.UseTls
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        await client.ConnectAsync(source.Host, source.Port, socketOptions, ct);
        await client.AuthenticateAsync(source.Username, password, ct);
    }

    /// <summary>
    /// The date of the oldest message with a usable <c>Date</c> header. POP3 orders messages
    /// oldest-first, so this normally resolves on the first header, but a message at index 0
    /// with a missing or unparseable date must not be taken as "mailbox has no oldest date" —
    /// the next message may still have one.
    /// </summary>
    private static async Task<DateTime?> GetOldestMessageAtUtcAsync(Pop3Client client, CancellationToken ct)
    {
        for (var start = 0; start < client.Count; start += HeaderBatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var count = Math.Min(HeaderBatchSize, client.Count - start);
            var headers = await client.GetMessageHeadersAsync(start, count, ct);

            foreach (var header in headers)
            {
                if (HeaderDateUtc(header) is { } date)
                {
                    return date;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// When a message says it was sent. POP3 has no server-side arrival time, so the sender's
    /// own <c>Date</c> header is the only date available.
    /// <para>
    /// Absent or unparseable reads as null, and every caller treats null as "do not know"
    /// rather than as a date. That matters most in the retention pass, where a date is what
    /// authorises deleting someone's mail.
    /// </para>
    /// </summary>
    private static DateTime? HeaderDateUtc(HeaderList headers)
    {
        var raw = headers[HeaderId.Date];

        return !string.IsNullOrWhiteSpace(raw) && MimeKit.Utils.DateUtils.TryParse(raw, out var parsed)
            ? parsed.UtcDateTime
            : null;
    }

    private sealed class Pop3ReadSession(
        Pop3Client client,
        IReadOnlyList<PolledItemRef> pending,
        DateTime? oldestMessageAtUtc) : IPolledReadSession
    {
        public IReadOnlyList<PolledItemRef> Pending => pending;

        public DateTime? OldestMessageAtUtc => oldestMessageAtUtc;

        public async Task<MimeMessage> FetchAsync(PolledItemRef message, CancellationToken ct)
            => await client.GetMessageAsync((int)message.Token, ct);

        /// <summary>
        /// Nothing to record. POP3 has no UIDVALIDITY, and writing something into the IMAP
        /// columns to fill the space would make the health view claim a checkpoint the
        /// protocol cannot honour.
        /// </summary>
        public void ApplyGeneration(ReportSource source)
        {
        }

        public void ApplyCheckpoint(ReportSource source, PolledItemRef handled)
            => source.LastProcessedUidl = handled.Identity;

        /// <summary>
        /// Quits cleanly. The read session marks nothing for deletion, so unlike the prune
        /// session there is nothing riding on the difference.
        /// </summary>
        public async Task CloseAsync(CancellationToken ct)
            => await client.DisconnectAsync(true, ct);

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Pop3PruneSession(
        Pop3Client client,
        IReadOnlyList<PolledPruneCandidate> eligible,
        bool dryRun) : IPolledPruneSession
    {
        // A set, not a list: the messages this pass deletes are the oldest ones, so the scan
        // below walks past every one of them before it finds a survivor.
        private readonly HashSet<int> _marked = [];

        public IReadOnlyList<PolledPruneCandidate> Eligible => eligible;

        public async Task DeleteAsync(PolledPruneCandidate candidate, CancellationToken ct)
        {
            if (dryRun)
            {
                return;
            }

            await client.DeleteMessageAsync((int)candidate.Token, ct);
            _marked.Add((int)candidate.Token);
        }

        /// <summary>
        /// A no-op, and that is the whole point of it being called. POP3 has no expunge: the
        /// server applies the delete marks when the session ends with QUIT, so the commit
        /// happens in <see cref="CloseAsync"/>. Anything that stops the session reaching that
        /// point — a dropped connection, a cancellation, an exception on the way out — leaves
        /// the mailbox untouched, which is the failure direction this pass wants.
        /// </summary>
        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>
        /// Asked before QUIT, so the marked messages are still present and have to be
        /// discounted by hand — the reverse of IMAP, where the expunge has already happened
        /// by the time this is asked.
        /// </summary>
        public async Task<DateTime?> GetOldestMessageAtUtcAsync(CancellationToken ct)
        {
            for (var index = 0; index < client.Count; index++)
            {
                ct.ThrowIfCancellationRequested();

                if (_marked.Contains(index))
                {
                    continue;
                }

                if (HeaderDateUtc(await client.GetMessageHeadersAsync(index, ct)) is { } date)
                {
                    return date;
                }
            }

            return null;
        }

        public async Task CloseAsync(CancellationToken ct)
            => await client.DisconnectAsync(true, ct);

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
