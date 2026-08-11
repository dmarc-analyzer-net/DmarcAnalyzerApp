using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <param name="Deleted">Messages expunged.</param>
/// <param name="SkippedUnarchived">
/// Messages past the cutoff that were left alone because the archive is on and has no copy
/// of them. Non-zero here is the safety rule working, not a fault.
/// </param>
public sealed record MailboxRetentionSourceResult(
    Guid ReportSourceId,
    string ReportSourceName,
    bool Suspended,
    string? Reason,
    DateTime? CutoffUtc,
    int Eligible,
    int Deleted,
    int SkippedUnarchived,
    string? Error);

public sealed record MailboxRetentionRunResult(
    bool DryRun,
    IReadOnlyList<MailboxRetentionSourceResult> Sources);

public interface IMailboxRetentionService
{
    Task<MailboxRetentionRunResult> RunAsync(bool dryRun, CancellationToken ct);
}

/// <summary>
/// Deletes report mail that has aged past the retention window the app enforces on itself,
/// so the system has one retention window instead of two.
/// <para>
/// Without this the daily purge removes report data from the database while the same
/// personal data — sending IPs, authentication outcomes — sits in the mailbox forever. That
/// makes an erasure request impossible to satisfy: lower a client's window, purge, and the
/// reports return on the next sync.
/// </para>
/// <para>
/// It is the only thing in this application that deletes data it does not own, so every
/// decision it makes is taken by <see cref="IMailboxRetentionPlanner"/> first, it is opt-in
/// per source, it carries a grace margin, it has a dry run, and it audits every pass.
/// </para>
/// </summary>
public sealed class MailboxRetentionService(
    DmarcAnalyzerDbContext db,
    IMailboxRetentionPlanner planner,
    ICredentialProtector credentialProtector,
    IReportMailArchive reportMailArchive,
    IAuditLog audit,
    ILogger<MailboxRetentionService> logger) : IMailboxRetentionService
{
    public async Task<MailboxRetentionRunResult> RunAsync(bool dryRun, CancellationToken ct)
    {
        var plans = await planner.PlanAsync(ct);
        var results = new List<MailboxRetentionSourceResult>(plans.Count);

        foreach (var plan in plans)
        {
            if (plan.Suspended || plan.CutoffUtc is not { } cutoff)
            {
                results.Add(new MailboxRetentionSourceResult(
                    plan.ReportSourceId, plan.ReportSourceName, true, plan.Reason,
                    null, 0, 0, 0, null));
                continue;
            }

            try
            {
                results.Add(await RunForSourceAsync(plan, cutoff, dryRun, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "Mailbox retention pass failed for source {ReportSourceId}", plan.ReportSourceId);

                results.Add(new MailboxRetentionSourceResult(
                    plan.ReportSourceId, plan.ReportSourceName, false, plan.Reason,
                    cutoff, 0, 0, 0, ex.Message));
            }
        }

        var deleted = results.Sum(x => x.Deleted);
        if (!dryRun && deleted > 0)
        {
            // Deleting upstream data belongs in the trail, and the trail is the only place
            // it is recorded at all — the mail is gone from the mailbox by then.
            await audit.RecordSystemAsync(
                AuditEvents.MailboxRetentionDeleted,
                $"Deleted {deleted} report message(s) past retention from " +
                $"{results.Count(x => x.Deleted > 0)} mailbox source(s)",
                details: string.Join("; ", results
                    .Where(x => x.Deleted > 0)
                    .Select(x => $"{x.ReportSourceName}: {x.Deleted} before {x.CutoffUtc:yyyy-MM-dd}")),
                ct: ct);
        }

        return new MailboxRetentionRunResult(dryRun, results);
    }

    private async Task<MailboxRetentionSourceResult> RunForSourceAsync(
        MailboxRetentionPlan plan,
        DateTime cutoff,
        bool dryRun,
        CancellationToken ct)
    {
        var source = await db.MailboxSources.SingleAsync(x => x.Id == plan.ReportSourceId, ct);
        var password = credentialProtector.Unprotect(source.PasswordEncrypted);

        using var client = new ImapClient();
        var socketOptions = source.UseTls
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTlsWhenAvailable;

        await client.ConnectAsync(source.Host, source.Port, socketOptions, ct);
        await client.AuthenticateAsync(source.Username, password, ct);

        // Read-write, unlike the sync pass. This is the only place in the application that
        // opens a customer's mailbox for writing.
        var inbox = client.Inbox;
        await inbox.OpenAsync(dryRun ? FolderAccess.ReadOnly : FolderAccess.ReadWrite, ct);

        var uidValidity = (long)inbox.UidValidity;

        // Delivered-before, not "processed": a message that never parsed must age out too,
        // or the mailbox accumulates permanent failures for ever.
        var eligible = await inbox.SearchAsync(SearchQuery.DeliveredBefore(cutoff), ct);

        var deleted = 0;
        var skippedUnarchived = 0;

        foreach (var uid in eligible)
        {
            ct.ThrowIfCancellationRequested();

            if (reportMailArchive.IsEnabled)
            {
                // No delete without a confirmed write. Checked against the bucket rather
                // than inferred from configuration, because "archiving is on" and "this
                // message is archived" are different claims.
                var summaries = await inbox.FetchAsync(
                    new[] { uid }, MessageSummaryItems.InternalDate, ct);
                var receivedAtUtc = summaries.FirstOrDefault()?.InternalDate?.UtcDateTime ?? cutoff;

                if (!await reportMailArchive.ExistsAsync(
                        source.Id, uid.Id, uidValidity, receivedAtUtc, ct))
                {
                    skippedUnarchived++;
                    continue;
                }
            }

            if (!dryRun)
            {
                await inbox.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true, ct);
                deleted++;
            }
        }

        if (!dryRun && deleted > 0)
        {
            await inbox.ExpungeAsync(ct);
        }

        // Refreshed while the folder is open, since this pass has just changed the answer.
        await RefreshOldestMessageAsync(source.Id, inbox, ct);

        await client.DisconnectAsync(true, ct);

        logger.LogInformation(
            "Mailbox retention pass for {ReportSourceName}: {Eligible} eligible before {Cutoff:yyyy-MM-dd}, " +
            "{Deleted} deleted, {Skipped} skipped as unarchived{DryRun}",
            plan.ReportSourceName, eligible.Count, cutoff, deleted, skippedUnarchived,
            dryRun ? " (dry run)" : string.Empty);

        return new MailboxRetentionSourceResult(
            plan.ReportSourceId, plan.ReportSourceName, false, null,
            cutoff, eligible.Count, deleted, skippedUnarchived, null);
    }

    /// <summary>
    /// Records how far back the mailbox still reaches. This is the evidence for the claim
    /// that the mailbox is a usable archive, and after a deletion pass it is how an operator
    /// confirms the cut landed where it was meant to.
    /// </summary>
    private async Task RefreshOldestMessageAsync(Guid sourceId, IMailFolder inbox, CancellationToken ct)
    {
        var all = await inbox.SearchAsync(SearchQuery.All, ct);
        DateTime? oldest = null;

        if (all.Count > 0)
        {
            var summaries = await inbox.FetchAsync(
                new[] { all[0] }, MessageSummaryItems.InternalDate, ct);
            oldest = summaries.FirstOrDefault()?.InternalDate?.UtcDateTime;
        }

        var tracked = await db.MailboxSources.SingleAsync(x => x.Id == sourceId, ct);
        tracked.OldestMessageAtUtc = oldest;
        tracked.UpdatedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }
}
