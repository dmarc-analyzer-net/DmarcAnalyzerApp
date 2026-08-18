using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using MailKit;
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
    IPolledSourceTransportFactory transportFactory,
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

        // Resolved rather than tested against a list of protocol names: a source is
        // syncable exactly when a transport exists for it, so the two can never disagree.
        var transport = transportFactory.For(reportSource.Protocol);
        if (transport is null)
        {
            return ServiceResult<MailboxSyncResult>.Failure(
                $"sync applies to polled mailboxes only; this source's protocol is '{reportSource.Protocol}'", 400);
        }

        var messagesScanned = 0;
        var attachmentsProcessed = 0;
        var reportsInserted = 0;
        var reportsSkippedAsDuplicate = 0;
        var parseFailures = 0;
        var tlsReportsInserted = 0;
        var tlsReportsSkippedAsDuplicate = 0;

        // Legacy rows store the password in plaintext; re-protect them on first use. An empty
        // secret is skipped rather than protected: an S3 source using the ambient credential
        // chain legitimately has none, and encrypting the empty string would turn "no
        // credential" into a stored blob that reads as one.
        if (!string.IsNullOrEmpty(reportSource.PasswordEncrypted) &&
            !credentialProtector.IsProtected(reportSource.PasswordEncrypted))
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

        var secret = string.IsNullOrEmpty(reportSource.PasswordEncrypted)
            ? string.Empty
            : credentialProtector.Unprotect(reportSource.PasswordEncrypted);

        // Declared out here so the failure path can persist it. A run that times out
        // mid-drain has still read everything up to this message, and throwing that away
        // means the next pass re-fetches all of it — safe, because of dedup, but a straight
        // repeat of work that can take hours on a large backlog.
        PolledItemRef? highestProcessed = null;
        IPolledReadSession? session = null;

        try
        {
            // The run timeout covers connecting and authenticating too, not just the drain:
            // a mailbox host that accepts the TCP connection and then never answers is one
            // of the ways a sync hangs, and it is the same incident as any other overrun.
            session = await transport.OpenForReadAsync(reportSource, secret, operationToken);

            var pending = session.Pending;
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
                if (highestProcessed is not { } handled)
                {
                    return;
                }

                session.ApplyCheckpoint(reportSource, handled);
                reportSource.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(operationToken);
            }

            foreach (var message in pending)
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

                var mail = await session.FetchAsync(message, operationToken);

                // Archived before it is parsed, and independently of whether it parses. A
                // message that fails to parse is exactly the one worth keeping a copy of,
                // and archiving after a parse failure would skip it.
                if (reportMailArchive.IsEnabled)
                {
                    await reportMailArchive.TryArchiveAsync(
                        mail, reportSource.Id, message.ArchiveIdentity,
                        mail.Date.UtcDateTime, operationToken);
                }

                if (!mail.Attachments.Any())
                {
                    // Nothing to extract, but the message has been dealt with — see the
                    // note at the end of the loop body for why that matters.
                    highestProcessed = message;
                    continue;
                }

                foreach (var attachment in mail.Attachments)
                {
                    operationToken.ThrowIfCancellationRequested();

                    IReadOnlyList<ExtractedReportPayload> payloads;
                    try
                    {
                        // ArchiveTruncated is deliberately ignored here. Mail cannot be
                        // re-delivered on request, so taking what the cap allowed and
                        // logging the rest is the best available outcome; the endpoint,
                        // whose caller can retry in smaller pieces, refuses instead.
                        payloads = (await ReportPayloadExtractor.ExtractAsync(
                            attachment, PayloadLimits(), logger, operationToken)).Payloads;
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

                                if (outcome.Outcome == ReportPayloadOutcome.ForeignDomainRefused)
                                {
                                    // Counted with the failures rather than the duplicates:
                                    // a report that arrived and was not stored is the same
                                    // operator signal, and calling it a duplicate would
                                    // suggest the data is already held when it is not.
                                    parseFailures++;
                                    logger.LogWarning(
                                        "Refused {AttachmentName} for report source {ReportSourceId}: it is for a " +
                                        "domain another client owns and this source may not ingest for foreign domains",
                                        payload.SourceName, reportSource.Id);
                                }
                                else if (outcome.Format == ReportPayloadIngestResult.Tls)
                                {
                                    if (outcome.Outcome == ReportPayloadOutcome.Inserted) tlsReportsInserted++;
                                    else tlsReportsSkippedAsDuplicate++;
                                }
                                else
                                {
                                    if (outcome.Outcome == ReportPayloadOutcome.Inserted) reportsInserted++;
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
                // message recorded before its own fetch completed would be skipped for good
                // on the next pass.
                highestProcessed = message;
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
                    pending.Count - messagesScanned);
            }

            reportSource.LastSuccessSyncAtUtc = DateTime.UtcNow;
            session.ApplyGeneration(reportSource);
            if (highestProcessed is { } lastHandled)
            {
                session.ApplyCheckpoint(reportSource, lastHandled);
            }

            // Only where the protocol could answer cheaply. IMAP declines — see the note on
            // its session — and leaves this to the retention pass, which opens the whole
            // folder anyway.
            if (session.OldestMessageAtUtc is { } oldest)
            {
                reportSource.OldestMessageAtUtc = oldest;
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

            await session.CloseAsync(operationToken);

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
            // deliberately. Only the checkpoint columns are marked modified:
            // LastSuccessSyncAtUtc is deliberately left alone, because this was not a
            // success even when it made progress.
            if (highestProcessed is { } handled && session is not null)
            {
                session.ApplyCheckpoint(reportSource, handled);
                reportSource.UpdatedAtUtc = DateTime.UtcNow;

                db.ReportSources.Attach(reportSource);
                var checkpoint = db.Entry(reportSource);

                // Every protocol's checkpoint columns rather than the one this source
                // actually writes. Marking only the protocol's own would mean naming it
                // here, which is the branch this service exists without; the others are
                // re-written with the values they were loaded with, so the row does not
                // move for them.
                checkpoint.Property(x => x.LastProcessedUid).IsModified = true;
                checkpoint.Property(x => x.LastProcessedUidValidity).IsModified = true;
                checkpoint.Property(x => x.LastProcessedUidl).IsModified = true;
                checkpoint.Property(x => x.LastProcessedObjectAtUtc).IsModified = true;
                checkpoint.Property(x => x.LastProcessedObjectKey).IsModified = true;
                checkpoint.Property(x => x.S3ReadListingCursorKey).IsModified = true;
                checkpoint.Property(x => x.UpdatedAtUtc).IsModified = true;
            }

            var timedOut = IsTimeout(ex);
            var status = ResolveUnsuccessfulRunStatus(ex, highestProcessed);

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
                      $"checkpointed at {highestProcessed?.Identity ?? "none"}"
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
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
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
    public static string ResolveUnsuccessfulRunStatus(Exception ex, PolledItemRef? highestProcessed)
        => IsTimeout(ex) && highestProcessed is not null ? "partial" : "failed";

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
    /// The POP3 equivalent: the messages after the checkpointed UIDL, in listing order.
    /// <para>
    /// The shape of the problem is different from IMAP's, because a UIDL is opaque. There is
    /// no ordering to compare against and no range to ask the server for, so "what is new" is
    /// only answerable as "what comes after this one in the listing" — POP3 numbers messages
    /// by arrival and keeps that order stable within the mailbox's lifetime.
    /// </para>
    /// <para>
    /// Two cases are worth naming because they look alike and are not. A checkpoint at the
    /// <em>last</em> entry selects nothing, which is a caught-up mailbox and the POP3 analogue
    /// of the bug documented above. A checkpoint that is <em>absent</em> from the listing
    /// selects everything, because the message it named has been deleted and no position can
    /// be recovered from a string that is not there; the caller logs that, since a silent full
    /// re-read is indistinguishable from a loop.
    /// </para>
    /// <para>
    /// Positions, not the UIDLs themselves, are what comes back: the returned
    /// <see cref="PolledItemRef.Token"/> is an index into <paramref name="uidls"/>, which
    /// is only meaningful for as long as the session that produced the listing stays open.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PolledItemRef> SelectUidlsPastCheckpoint(
        IReadOnlyList<string> uidls, string? lastProcessedUidl)
    {
        var start = 0;
        if (!string.IsNullOrEmpty(lastProcessedUidl))
        {
            for (var index = 0; index < uidls.Count; index++)
            {
                if (string.Equals(uidls[index], lastProcessedUidl, StringComparison.Ordinal))
                {
                    start = index + 1;
                    break;
                }
            }
        }

        var pending = new List<PolledItemRef>(uidls.Count - start);
        for (var index = start; index < uidls.Count; index++)
        {
            pending.Add(new PolledItemRef(
                index, uidls[index], Backup.ReportMailIdentity.ForPop3(uidls[index])));
        }

        return pending;
    }

    /// <summary>
    /// The decompression budget for one attachment, read fresh from options each time so
    /// a limit raised in response to an incident applies on the next pass.
    /// </summary>
    private ReportPayloadLimits PayloadLimits() => new(
        _options.MaxReportEntryBytes,
        _options.MaxReportAttachmentBytes,
        _options.MaxReportArchiveEntries);

}
