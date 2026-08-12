using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// What a mailbox retention pass would do to one source, and why.
/// </summary>
/// <param name="Suspended">
/// True when nothing may be deleted from this source. The reason says which rule stopped
/// it.
/// </param>
/// <param name="CutoffUtc">
/// Messages older than this are eligible. Null when suspended — there is no cutoff to
/// report, and returning one anyway invites a caller to use it.
/// </param>
/// <param name="RetentionMonths">
/// The widest window among the clients this source serves. One mailbox can receive reports
/// for many clients, so the narrowest window must never be the one that decides.
/// </param>
public sealed record MailboxRetentionPlan(
    Guid ReportSourceId,
    string ReportSourceName,
    bool Enabled,
    bool Suspended,
    string? Reason,
    DateTime? CutoffUtc,
    int RetentionMonths,
    int GraceDays,
    IReadOnlyList<string> ClientSlugs,
    IReadOnlyList<string> LegalHoldClientSlugs,
    DateTime? OldestMessageAtUtc);

public interface IMailboxRetentionPlanner
{
    /// <summary>
    /// Plans every source, including the ones that will do nothing — a preview that hides
    /// the suspended sources cannot answer "why is that mailbox still growing?".
    /// </summary>
    Task<IReadOnlyList<MailboxRetentionPlan>> PlanAsync(CancellationToken ct);
}

/// <summary>
/// Decides where the cut falls, separately from making it.
/// <para>
/// The split is deliberate. Every rule that makes deleting a customer's mail safe lives
/// here — the widest-window rule, the legal-hold suspension, the grace margin — and none of
/// them needs an IMAP connection, so all of them are testable. What is left in the
/// executor is a loop over a plan this class already vetted.
/// </para>
/// </summary>
public sealed class MailboxRetentionPlanner(
    DmarcAnalyzerDbContext db,
    IOptions<WorkerOptions> options) : IMailboxRetentionPlanner
{
    private readonly WorkerOptions _options = options.Value;

    public async Task<IReadOnlyList<MailboxRetentionPlan>> PlanAsync(CancellationToken ct)
    {
        var graceDays = Math.Max(0, _options.MailboxRetentionGraceDays);
        // Only a polled source has a mailbox to expunge. This is not merely tidy: the
        // retention service connects over IMAP per plan, so a plan for a source that is
        // not IMAP is an attempt to connect to a mailbox that does not exist — which a
        // legacy pop3 row with deletion enabled would already have triggered today.
        var sources = await db.ReportSources
            .AsNoTracking()
            .Where(x => x.Protocol == ReportSourceProtocols.Imap)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        var plans = new List<MailboxRetentionPlan>(sources.Count);

        foreach (var source in sources)
        {
            // Every client this source has actually delivered reports for, plus its default
            // client — which matters when a source is new and has ingested nothing yet, so
            // its cutoff would otherwise be computed from an empty set.
            var servedClientIds = await db.DmarcReports
                .AsNoTracking()
                .Where(r => r.ReportSourceId == source.Id)
                .Join(db.Domains.AsNoTracking(), r => r.DomainId, d => d.Id, (r, d) => d.ClientId)
                .Distinct()
                .ToListAsync(ct);

            if (!servedClientIds.Contains(source.DefaultClientId))
            {
                servedClientIds.Add(source.DefaultClientId);
            }

            var clients = await db.Clients
                .AsNoTracking()
                .Where(c => servedClientIds.Contains(c.Id))
                .Select(c => new { c.Slug, c.RetentionMonths, c.LegalHold })
                .ToListAsync(ct);

            var slugs = clients.Select(c => c.Slug).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var legalHold = clients.Where(c => c.LegalHold).Select(c => c.Slug)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();

            // The widest window, never the narrowest: mail deleted on a 6-month client's
            // schedule would take a 27-month client's reports with it.
            var retentionMonths = clients.Count == 0 ? 0 : clients.Max(c => c.RetentionMonths);

            var (suspended, reason) = Suspension(source.DeleteAfterRetention, clients.Count, legalHold);

            plans.Add(new MailboxRetentionPlan(
                ReportSourceId: source.Id,
                ReportSourceName: source.Name,
                Enabled: source.DeleteAfterRetention,
                Suspended: suspended,
                Reason: reason,
                CutoffUtc: suspended
                    ? null
                    : DateTime.UtcNow.AddMonths(-retentionMonths).AddDays(-graceDays),
                RetentionMonths: retentionMonths,
                GraceDays: graceDays,
                ClientSlugs: slugs,
                LegalHoldClientSlugs: legalHold,
                OldestMessageAtUtc: source.OldestMessageAtUtc));
        }

        return plans;
    }

    /// <summary>
    /// The three reasons nothing gets deleted. Exposed as a static so the rules can be
    /// read — and tested — without a database.
    /// </summary>
    public static (bool Suspended, string? Reason) Suspension(
        bool enabled,
        int clientCount,
        IReadOnlyCollection<string> legalHoldSlugs)
    {
        if (!enabled)
        {
            return (true, "mailbox retention deletion is not enabled for this source");
        }

        if (legalHoldSlugs.Count > 0)
        {
            // The database exemption is worthless if the upstream copy is being deleted:
            // legal hold preserves data for a dispute, and the mailbox is where that data
            // can be re-read from.
            return (true,
                $"suspended: client(s) under legal hold ({string.Join(", ", legalHoldSlugs)})");
        }

        if (clientCount == 0)
        {
            // No client means no retention window, and no window means no defensible cutoff.
            return (true, "suspended: no client could be resolved for this source");
        }

        return (false, null);
    }
}
