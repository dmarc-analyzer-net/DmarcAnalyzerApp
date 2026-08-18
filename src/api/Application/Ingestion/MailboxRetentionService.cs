using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data;
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
    IPolledSourceTransportFactory transportFactory,
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
                $"{results.Count(x => x.Deleted > 0)} report source(s)",
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
        var source = await db.ReportSources.SingleAsync(x => x.Id == plan.ReportSourceId, ct);
        var secret = string.IsNullOrEmpty(source.PasswordEncrypted)
            ? string.Empty
            : credentialProtector.Unprotect(source.PasswordEncrypted);

        // The planner already excluded anything without a mailbox, so a missing transport
        // here is a source whose protocol was removed from under it rather than an ordinary
        // outcome — and deleting mail is not a thing to attempt on a guess.
        var transport = transportFactory.For(source.Protocol)
            ?? throw new InvalidOperationException(
                $"no mailbox transport for protocol '{source.Protocol}'");

        await using var session = await transport.OpenForPruneAsync(source, secret, cutoff, dryRun, ct);

        var eligible = session.Eligible;
        var deleted = 0;
        var skippedUnarchived = 0;

        foreach (var candidate in eligible)
        {
            ct.ThrowIfCancellationRequested();

            if (reportMailArchive.IsEnabled)
            {
                // No delete without a confirmed write. Checked against the bucket rather
                // than inferred from configuration, because "archiving is on" and "this
                // message is archived" are different claims.
                if (!await reportMailArchive.ExistsAsync(
                        source.Id, candidate.ArchiveIdentity, candidate.ReceivedAtUtc, ct))
                {
                    skippedUnarchived++;
                    continue;
                }
            }

            if (!dryRun)
            {
                await session.DeleteAsync(candidate, ct);
                deleted++;
            }
        }

        if (!dryRun && deleted > 0)
        {
            await session.CommitAsync(ct);
        }

        // Asked while the mailbox is open, since this pass has just changed the answer.
        var oldest = await session.GetOldestMessageAtUtcAsync(ct);

        // Last, and it matters that it is last: on POP3 the deletions do not take effect
        // until the session ends cleanly, so this call is where the mail actually goes.
        await session.CloseAsync(ct);

        await RecordOldestMessageAsync(source.Id, oldest, ct);

        logger.LogInformation(
            "Mailbox retention pass for {ReportSourceName} ({Protocol}): {Eligible} eligible before " +
            "{Cutoff:yyyy-MM-dd}, {Deleted} deleted, {Skipped} skipped as unarchived{DryRun}",
            plan.ReportSourceName, source.Protocol, eligible.Count, cutoff, deleted, skippedUnarchived,
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
    private async Task RecordOldestMessageAsync(Guid sourceId, DateTime? oldest, CancellationToken ct)
    {
        var tracked = await db.ReportSources.SingleAsync(x => x.Id == sourceId, ct);
        tracked.OldestMessageAtUtc = oldest;
        tracked.UpdatedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }
}
