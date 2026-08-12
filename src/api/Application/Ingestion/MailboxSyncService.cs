using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using System.Linq;
using DmarcAnalyzer.Api.Workers;
using System.Threading.Tasks;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed class MailboxSyncService(
    DmarcAnalyzerDbContext db,
    IReportPayloadIngestor payloadIngestor,
    Security.ICredentialProtector credentialProtector,
    Backup.IReportMailArchive reportMailArchive,
    IOptions<WorkerOptions> options,
    ILogger<MailboxSyncService> logger) : IMailboxSyncService
{
    private readonly WorkerOptions _options = options.Value;

    public async Task<ServiceResult<MailboxSyncResult>> SyncReportSourceAsync(Guid reportSourceId, CancellationToken ct)
        => await SyncReportSourceAsync(reportSourceId, "manual", ct);

    public async Task<ServiceResult<MailboxSyncResult>> SyncReportSourceAsync(Guid reportSourceId, string trigger, CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        using var syncTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var syncRunTimeoutMinutes = Math.Max(1, _options.SyncRunTimeoutMinutes);
        syncTimeoutCts.CancelAfter(TimeSpan.FromMinutes(syncRunTimeoutMinutes));
        var operationToken = syncTimeoutCts.Token;

        var reportSource = await db.ReportSources
            .SingleOrDefaultAsync(x => x.Id == reportSourceId, operationToken);

        if (reportSource is null)
        {
            return ServiceResult<MailboxSyncResult>.Failure("report source not found", 404);
        }

        if (!string.Equals(reportSource.Protocol, "imap", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceResult<MailboxSyncResult>.Failure("manual sync currently supports only IMAP", 400);
        }

        var messagesScanned = 0;
        var attachmentsProcessed = 0;
        var reportsInserted = 0;
        var reportsSkippedAsDuplicate = 0;
        var parseFailures = 0;
        var tlsReportsInserted = 0;
        var tlsReportsSkippedAsDuplicate = 0;

        // Legacy rows store the password in plaintext; re-protect them on first use.
        if (!credentialProtector.IsProtected(reportSource.PasswordEncrypted))
        {
            var reprotected = credentialProtector.Protect(reportSource.PasswordEncrypted);
            if (reprotected != reportSource.PasswordEncrypted)
            {
                reportSource.PasswordEncrypted = reprotected;
                reportSource.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(operationToken);
                logger.LogInformation("Re-protected stored credential for report source {ReportSourceId}", reportSource.Id);
            }
        }

        var mailboxPassword = credentialProtector.Unprotect(reportSource.PasswordEncrypted);

        // Declared out here so the failure path can persist them. A run that times
        // out mid-drain has still read everything up to this UID, and throwing that
        // away means the next pass re-fetches all of it — safe, because of dedup,
        // but a straight repeat of work that can take hours on a large backlog.
        long? highestProcessedUid = null;
        long? currentUidValidity = null;

        try
        {
            using var client = new ImapClient();
            var secureSocketOptions = reportSource.UseTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

            await client.ConnectAsync(reportSource.Host, reportSource.Port, secureSocketOptions, ct);
            await client.AuthenticateAsync(reportSource.Username, mailboxPassword, operationToken);

            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, operationToken);

            currentUidValidity = (long)inbox.UidValidity;
            var lastProcessedUid = reportSource.LastProcessedUid;
            if (reportSource.LastProcessedUidValidity.HasValue &&
                reportSource.LastProcessedUidValidity.Value != currentUidValidity)
            {
                lastProcessedUid = null;
            }

            SearchQuery query = SearchQuery.All;
            if (lastProcessedUid.HasValue && lastProcessedUid.Value > 0 && lastProcessedUid.Value < uint.MaxValue)
            {
                var startUid = new UniqueId((uint)lastProcessedUid.Value + 1);
                query = SearchQuery.Uids(new UniqueIdRange(startUid, UniqueId.MaxValue));
            }

            // Filtered rather than taken as given. IMAP resolves * to the highest UID that
            // exists, so {checkpoint+1}:* does not return nothing once a mailbox is caught
            // up — the range is normalised and the newest message comes back again. See
            // SelectUidsPastCheckpoint.
            var uids = SelectUidsPastCheckpoint(
                await inbox.SearchAsync(query, operationToken), lastProcessedUid);
            var batchSize = Math.Max(1, _options.MaxMessagesPerSync);

            // The budget bounds how long this source may keep drawing batches. The hard
            // timeout would also stop the drain, but only by cancelling the run, so the
            // budget has to expire first to leave a clean success behind.
            var drainBudgetMinutes = Math.Max(1, _options.MailboxDrainBudgetMinutes);
            if (drainBudgetMinutes >= syncRunTimeoutMinutes)
            {
                drainBudgetMinutes = Math.Max(1, syncRunTimeoutMinutes - 1);
                logger.LogWarning(
                    "Worker:MailboxDrainBudgetMinutes ({Configured}) is not below Worker:SyncRunTimeoutMinutes " +
                    "({Timeout}); draining for {Effective} minute(s) instead so the run is not cancelled mid-drain",
                    _options.MailboxDrainBudgetMinutes, syncRunTimeoutMinutes, drainBudgetMinutes);
            }

            var drainDeadlineUtc = startedAtUtc.AddMinutes(drainBudgetMinutes);
            var processedInBatch = 0;
            var batchesDrained = 0;
            var stoppedOnBudget = false;

            // Commits the checkpoint on its own, between batches. A crash or timeout
            // later in the drain then costs one batch of re-fetching rather than every
            // message the pass had already read.
            async Task CommitCheckpointAsync()
            {
                if (!highestProcessedUid.HasValue)
                {
                    return;
                }

                reportSource.LastProcessedUid = highestProcessedUid;
                reportSource.LastProcessedUidValidity = currentUidValidity;
                reportSource.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(operationToken);
            }

            foreach (var uid in uids)
            {
                if (processedInBatch >= batchSize)
                {
                    await CommitCheckpointAsync();
                    batchesDrained++;
                    processedInBatch = 0;

                    // Checked only at a batch boundary, so a pass always drains at least
                    // one full batch however tight the budget is.
                    if (DateTime.UtcNow >= drainDeadlineUtc)
                    {
                        stoppedOnBudget = true;
                        break;
                    }
                }

                operationToken.ThrowIfCancellationRequested();
                messagesScanned++;
                processedInBatch++;

                var message = await inbox.GetMessageAsync(uid, operationToken);

                // Archived before it is parsed, and independently of whether it parses. A
                // message that fails to parse is exactly the one worth keeping a copy of,
                // and archiving after a parse failure would skip it.
                if (reportMailArchive.IsEnabled)
                {
                    await reportMailArchive.TryArchiveAsync(
                        message, reportSource.Id, uid.Id, currentUidValidity.Value,
                        message.Date.UtcDateTime, operationToken);
                }

                if (!message.Attachments.Any())
                {
                    // Nothing to extract, but the message has been dealt with — see the
                    // note at the end of the loop body for why that matters.
                    highestProcessedUid = uid.Id;
                    continue;
                }

                foreach (var attachment in message.Attachments)
                {
                    operationToken.ThrowIfCancellationRequested();

                    IReadOnlyList<ExtractedReportPayload> payloads;
                    try
                    {
                        payloads = await ReportPayloadExtractor.ExtractAsync(
                            attachment, PayloadLimits(), logger, operationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        parseFailures++;
                        logger.LogWarning(ex,
                            "Failed to extract report attachment {AttachmentName} for report source {ReportSourceId}",
                            GetAttachmentFileName(attachment), reportSource.Id);
                        continue;
                    }

                    if (payloads.Count == 0)
                    {
                        continue;
                    }

                    foreach (var payload in payloads)
                    {
                        await using (payload.Stream)
                        {
                            attachmentsProcessed++;

                            try
                            {
                                var outcome = await payloadIngestor.IngestAsync(
                                    payload, reportSource, operationToken);

                                if (outcome.Format == ReportPayloadIngestResult.Tls)
                                {
                                    if (outcome.Inserted) tlsReportsInserted++;
                                    else tlsReportsSkippedAsDuplicate++;
                                }
                                else
                                {
                                    if (outcome.Inserted) reportsInserted++;
                                    else reportsSkippedAsDuplicate++;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // One counter for both formats: a report that arrived and
                                // could not be stored is the same operator signal whichever
                                // it was, and the log line names it.
                                parseFailures++;
                                logger.LogWarning(ex,
                                    "Failed to ingest attachment {AttachmentName} for report source {ReportSourceId}",
                                    payload.SourceName, reportSource.Id);
                            }
                        }
                    }
                }

                // Advanced only now that the message is fully handled, never on the way
                // in. The checkpoint is persisted even when the run is cancelled, so a
                // UID recorded before its own fetch completed would be skipped for good
                // on the next pass.
                highestProcessedUid = uid.Id;
            }

            if (stoppedOnBudget)
            {
                // Not a failure: the checkpoint holds, so the next pass resumes here.
                // Worth saying out loud, because the alternative reading of a short run
                // on a big mailbox is that ingestion has quietly stalled.
                logger.LogInformation(
                    "Drain budget of {Budget} minute(s) reached for report source {ReportSourceId} after " +
                    "{Scanned} message(s) in {Batches} batch(es); {Remaining} still queued for the next pass",
                    drainBudgetMinutes, reportSource.Id, messagesScanned, batchesDrained + 1,
                    uids.Count - messagesScanned);
            }

            reportSource.LastSuccessSyncAtUtc = DateTime.UtcNow;
            reportSource.LastProcessedUidValidity = currentUidValidity;
            if (highestProcessedUid.HasValue)
            {
                reportSource.LastProcessedUid = highestProcessedUid;
            }
            reportSource.UpdatedAtUtc = DateTime.UtcNow;

            if (operationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"sync run exceeded configured timeout of {syncRunTimeoutMinutes} minute(s)");
            }

            db.MailboxSyncRuns.Add(new MailboxSyncRun
            {
                ReportSourceId = reportSource.Id,
                Trigger = string.IsNullOrWhiteSpace(trigger) ? "unknown" : trigger.Trim().ToLowerInvariant(),
                Status = "success",
                StartedAtUtc = startedAtUtc,
                FinishedAtUtc = DateTime.UtcNow,
                MessagesScanned = messagesScanned,
                AttachmentsProcessed = attachmentsProcessed,
                ReportsInserted = reportsInserted,
                ReportsSkippedAsDuplicate = reportsSkippedAsDuplicate,
                ParseFailures = parseFailures,
                TlsReportsInserted = tlsReportsInserted,
                TlsReportsSkippedAsDuplicate = tlsReportsSkippedAsDuplicate,
                CreatedAtUtc = startedAtUtc,
            });

            await db.SaveChangesAsync(operationToken);

            await client.DisconnectAsync(true, operationToken);

            return ServiceResult<MailboxSyncResult>.Success(new MailboxSyncResult(
                reportSource.Id,
                messagesScanned,
                attachmentsProcessed,
                reportsInserted,
                reportsSkippedAsDuplicate,
                tlsReportsInserted,
                tlsReportsSkippedAsDuplicate,
                parseFailures,
                true,
                null,
                startedAtUtc,
                DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mailbox sync failed for source {ReportSourceId}", reportSource.Id);

            db.ChangeTracker.Clear();

            // Clearing the tracker is what lets the run row below save on its own, but
            // it also drops the checkpoint assigned on the success path — so re-apply it
            // deliberately. Only the two checkpoint columns are marked modified:
            // LastSuccessSyncAtUtc is deliberately left alone, because this was not a
            // success even when it made progress.
            if (highestProcessedUid.HasValue)
            {
                reportSource.LastProcessedUid = highestProcessedUid;
                reportSource.LastProcessedUidValidity = currentUidValidity;
                reportSource.UpdatedAtUtc = DateTime.UtcNow;

                db.ReportSources.Attach(reportSource);
                var checkpoint = db.Entry(reportSource);
                checkpoint.Property(x => x.LastProcessedUid).IsModified = true;
                checkpoint.Property(x => x.LastProcessedUidValidity).IsModified = true;
                checkpoint.Property(x => x.UpdatedAtUtc).IsModified = true;
            }

            var timedOut = IsTimeout(ex);
            var status = ResolveUnsuccessfulRunStatus(ex, highestProcessedUid);

            db.MailboxSyncRuns.Add(new MailboxSyncRun
            {
                ReportSourceId = reportSource.Id,
                Trigger = string.IsNullOrWhiteSpace(trigger) ? "unknown" : trigger.Trim().ToLowerInvariant(),
                Status = status,
                StartedAtUtc = startedAtUtc,
                FinishedAtUtc = DateTime.UtcNow,
                MessagesScanned = messagesScanned,
                AttachmentsProcessed = attachmentsProcessed,
                ReportsInserted = reportsInserted,
                ReportsSkippedAsDuplicate = reportsSkippedAsDuplicate,
                ParseFailures = parseFailures,
                TlsReportsInserted = tlsReportsInserted,
                TlsReportsSkippedAsDuplicate = tlsReportsSkippedAsDuplicate,
                Error = timedOut
                    ? $"sync cancelled or timed out after {syncRunTimeoutMinutes} minute(s); " +
                      $"checkpointed at uid {highestProcessedUid?.ToString() ?? "none"}"
                    : ex.Message,
                CreatedAtUtc = startedAtUtc,
            });

            await TryPersistRunStateAsync(reportSource.Id);

            return ServiceResult<MailboxSyncResult>.Success(new MailboxSyncResult(
                reportSource.Id,
                messagesScanned,
                attachmentsProcessed,
                reportsInserted,
                reportsSkippedAsDuplicate,
                tlsReportsInserted,
                tlsReportsSkippedAsDuplicate,
                parseFailures,
                false,
                ex.Message,
                startedAtUtc,
                DateTime.UtcNow));
        }
    }

    /// <summary>
    /// Whether the run ended because it ran out of time rather than because something
    /// went wrong. The explicit budget check throws <see cref="TimeoutException"/>; the
    /// linked token throws <see cref="OperationCanceledException"/>. Both are the same
    /// event as far as an operator is concerned.
    /// </summary>
    public static bool IsTimeout(Exception ex)
        => ex is OperationCanceledException or TimeoutException;

    /// <summary>
    /// Status for a run that did not complete. A timeout that ingested part of a
    /// backlog is <c>partial</c>, not <c>failed</c>: the checkpoint is kept, so the
    /// next pass resumes where this one stopped. Calling that a failure reads as
    /// "nothing happened" and counts the source against the failing-mailbox tally on
    /// the dashboard (<c>AnalyticsQueryService</c> counts only <c>failed</c>).
    /// </summary>
    public static string ResolveUnsuccessfulRunStatus(Exception ex, long? highestProcessedUid)
        => IsTimeout(ex) && highestProcessedUid.HasValue ? "partial" : "failed";

    private async Task TryPersistRunStateAsync(Guid reportSourceId)
    {
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception persistEx)
        {
            logger.LogWarning(
                persistEx,
                "Failed to persist mailbox sync run final state for report source {ReportSourceId}",
                reportSourceId);
        }
    }

    private static string GetAttachmentFileName(MimeEntity attachment)
        => (attachment.ContentDisposition?.FileName ?? attachment.ContentType?.Name ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

    /// <summary>
    /// The UIDs a pass should actually read: those past the checkpoint, oldest first.
    /// <para>
    /// Not redundant with the UID range already in the search, and leaving it out was a real
    /// bug. IMAP resolves <c>*</c> to the highest UID that exists, so searching
    /// <c>230687:*</c> on a mailbox whose newest message is 230686 does not come back empty —
    /// the range is normalised to <c>230686:230687</c> and that message is returned. The pass
    /// then committed the checkpoint it already had and did the same thing on the next poll:
    /// one message re-fetched, re-parsed and re-checked every 16 seconds. On a real instance
    /// that was 5,162 no-op passes and a <c>mailbox_sync_run</c> row for each.
    /// </para>
    /// <para>
    /// It hides behind a backlog — while one exists <c>*</c> is above the checkpoint and the
    /// range behaves as intended — so it only appears once a mailbox has nothing left to
    /// fetch. The range is kept regardless, because it is what stops the server sending every
    /// UID in the mailbox on every poll.
    /// </para>
    /// <para>
    /// Ordering is explicit rather than assumed, because the drain loop's batch boundaries and
    /// the oldest-to-newest backfill both depend on it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<UniqueId> SelectUidsPastCheckpoint(
        IEnumerable<UniqueId> found, long? lastProcessedUid)
        => [.. found
            .Where(x => !lastProcessedUid.HasValue || x.Id > lastProcessedUid.Value)
            .OrderBy(x => x.Id)];

    /// <summary>
    /// The decompression budget for one attachment, read fresh from options each time so
    /// a limit raised in response to an incident applies on the next pass.
    /// </summary>
    private ReportPayloadLimits PayloadLimits() => new(
        _options.MaxReportEntryBytes,
        _options.MaxReportAttachmentBytes,
        _options.MaxReportArchiveEntries);

}
