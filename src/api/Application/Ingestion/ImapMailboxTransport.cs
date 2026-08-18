using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Data.Entities;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// IMAP, behind the transport seam. This is the code that used to sit inline in
/// <see cref="MailboxSyncService"/> and <see cref="MailboxRetentionService"/>; the behaviour is
/// unchanged, including the checkpoint filter that a search range alone does not give you.
/// </summary>
public sealed class ImapMailboxTransport(ILogger<ImapMailboxTransport> logger) : IMailboxTransport
{
    public string Protocol => ReportSourceProtocols.Imap;

    public async Task<IMailboxReadSession> OpenForReadAsync(
        ReportSource source, string password, CancellationToken ct)
    {
        var client = new ImapClient();

        try
        {
            await ConnectAsync(client, source, password, ct);

            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

            var uidValidity = (long)inbox.UidValidity;

            // A UID only means anything within the generation it was issued in. When the
            // mailbox has been recreated the stored UID names a different message, or none,
            // so the only safe reading is that there is no checkpoint.
            var lastProcessedUid = source.LastProcessedUid;
            if (source.LastProcessedUidValidity.HasValue &&
                source.LastProcessedUidValidity.Value != uidValidity)
            {
                logger.LogInformation(
                    "UIDVALIDITY for report source {ReportSourceId} changed from {Old} to {New}; " +
                    "the checkpoint no longer names a message and the mailbox will be re-read",
                    source.Id, source.LastProcessedUidValidity.Value, uidValidity);
                lastProcessedUid = null;
            }

            SearchQuery query = SearchQuery.All;
            if (lastProcessedUid is > 0 and < uint.MaxValue)
            {
                var startUid = new UniqueId((uint)lastProcessedUid.Value + 1);
                query = SearchQuery.Uids(new UniqueIdRange(startUid, UniqueId.MaxValue));
            }

            // Filtered rather than taken as given. IMAP resolves * to the highest UID that
            // exists, so {checkpoint+1}:* does not return nothing once a mailbox is caught
            // up — the range is normalised and the newest message comes back again. See
            // SelectUidsPastCheckpoint.
            var uids = MailboxSyncService.SelectUidsPastCheckpoint(
                await inbox.SearchAsync(query, ct), lastProcessedUid);

            var pending = uids
                .Select(uid => new MailboxMessageRef(
                    uid.Id,
                    uid.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ReportMailIdentity.ForImap(uid.Id, uidValidity)))
                .ToArray();

            return new ImapReadSession(client, inbox, uidValidity, pending);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<IMailboxPruneSession> OpenForPruneAsync(
        ReportSource source, string password, DateTime cutoffUtc, bool dryRun, CancellationToken ct)
    {
        var client = new ImapClient();

        try
        {
            await ConnectAsync(client, source, password, ct);

            // Read-write, unlike the sync pass. This is the only place in the application
            // that opens a customer's mailbox for writing, and a dry run does not.
            var inbox = client.Inbox;
            await inbox.OpenAsync(dryRun ? FolderAccess.ReadOnly : FolderAccess.ReadWrite, ct);

            var uidValidity = (long)inbox.UidValidity;

            // Delivered-before, not "processed": a message that never parsed must age out
            // too, or the mailbox accumulates permanent failures for ever.
            var eligible = await inbox.SearchAsync(SearchQuery.DeliveredBefore(cutoffUtc), ct);

            var candidates = Array.Empty<MailboxPruneCandidate>();
            if (eligible.Count > 0)
            {
                // One FETCH for the whole eligible set rather than one per message. The
                // envelope is here because the archive keys on the Date header — which is
                // what the sync pass archived under — while INTERNALDATE is only the
                // fallback for a message that has no Date at all.
                var summaries = await inbox.FetchAsync(
                    eligible, MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate |
                              MessageSummaryItems.Envelope, ct);

                candidates = [.. summaries.Select(summary => new MailboxPruneCandidate(
                    summary.UniqueId.Id,
                    summary.Envelope?.Date?.UtcDateTime
                        ?? summary.InternalDate?.UtcDateTime
                        ?? cutoffUtc,
                    ReportMailIdentity.ForImap(summary.UniqueId.Id, uidValidity)))];
            }

            return new ImapPruneSession(client, inbox, candidates, dryRun);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task ConnectAsync(
        ImapClient client, ReportSource source, string password, CancellationToken ct)
    {
        var socketOptions = source.UseTls
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        await client.ConnectAsync(source.Host, source.Port, socketOptions, ct);
        await client.AuthenticateAsync(source.Username, password, ct);
    }

    private sealed class ImapReadSession(
        ImapClient client,
        IMailFolder inbox,
        long uidValidity,
        IReadOnlyList<MailboxMessageRef> pending) : IMailboxReadSession
    {
        public IReadOnlyList<MailboxMessageRef> Pending => pending;

        /// <summary>
        /// Not answered here. The sync pass reads only what is past the checkpoint, so the
        /// oldest message is usually not among them; the retention pass, which opens the
        /// whole folder anyway, is what keeps this current.
        /// </summary>
        public DateTime? OldestMessageAtUtc => null;

        public async Task<MimeMessage> FetchAsync(MailboxMessageRef message, CancellationToken ct)
            => await inbox.GetMessageAsync(new UniqueId((uint)message.Token), ct);

        public void ApplyGeneration(ReportSource source)
            => source.LastProcessedUidValidity = uidValidity;

        public void ApplyCheckpoint(ReportSource source, MailboxMessageRef handled)
        {
            source.LastProcessedUid = handled.Token;
            source.LastProcessedUidValidity = uidValidity;
        }

        public async Task CloseAsync(CancellationToken ct)
            => await client.DisconnectAsync(true, ct);

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ImapPruneSession(
        ImapClient client,
        IMailFolder inbox,
        IReadOnlyList<MailboxPruneCandidate> eligible,
        bool dryRun) : IMailboxPruneSession
    {
        private int _marked;

        public IReadOnlyList<MailboxPruneCandidate> Eligible => eligible;

        public async Task DeleteAsync(MailboxPruneCandidate candidate, CancellationToken ct)
        {
            if (dryRun)
            {
                return;
            }

            await inbox.AddFlagsAsync(
                new UniqueId((uint)candidate.Token), MessageFlags.Deleted, silent: true, ct);
            _marked++;
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            if (dryRun || _marked == 0)
            {
                return;
            }

            await inbox.ExpungeAsync(ct);
        }

        public async Task<DateTime?> GetOldestMessageAtUtcAsync(CancellationToken ct)
        {
            var all = await inbox.SearchAsync(SearchQuery.All, ct);
            if (all.Count == 0)
            {
                return null;
            }

            var summaries = await inbox.FetchAsync(
                new[] { all[0] }, MessageSummaryItems.InternalDate, ct);

            return summaries.FirstOrDefault()?.InternalDate?.UtcDateTime;
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
