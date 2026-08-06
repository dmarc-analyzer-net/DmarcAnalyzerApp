using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Retention;

/// <summary>One client's share of a purge run.</summary>
public sealed record ClientPurgeResult(
    Guid ClientId,
    string ClientName,
    int RetentionMonths,
    DateTime CutoffUtc,
    int ReportsDeleted,
    int IngestRowsDeleted,
    int TlsPolicyRowsDeleted,
    int TlsIngestRowsDeleted,
    bool SkippedForLegalHold);

public sealed record PurgeRunResult(
    bool DryRun,
    DateTime StartedAtUtc,
    int ClientsConsidered,
    int ClientsOnLegalHold,
    int ReportsDeleted,
    int IngestRowsDeleted,
    int TlsPolicyRowsDeleted,
    int TlsIngestRowsDeleted,
    int TlsReportsDeleted,
    int AuditEventsDeleted,
    IReadOnlyList<ClientPurgeResult> PerClient);

public interface IRetentionPurgeService
{
    /// <summary>
    /// Deletes DMARC data whose reporting window ended before each client's
    /// retention cutoff. Clients on legal hold are skipped entirely.
    /// </summary>
    /// <param name="dryRun">Count what would be deleted without deleting it.</param>
    Task<PurgeRunResult> PurgeAsync(bool dryRun, int batchSize, CancellationToken ct);
}

/// <summary>
/// Enforces <c>client.RetentionMonths</c>. Retention is measured against the
/// report's <em>reporting window end</em> (<c>RangeEndUtc</c>), not the date we
/// happened to ingest it — a backfilled mailbox can deliver year-old reports, and
/// those should age out on their own schedule rather than getting a fresh lease.
///
/// Deleting a <c>dmarc_report</c> cascades to its records and their auth results
/// at the database level (configured in <see cref="DmarcAnalyzerDbContext"/>), so
/// this only needs to delete report rows.
/// </summary>
public sealed class RetentionPurgeService(
    DmarcAnalyzerDbContext db,
    IOptions<RetentionOptions> options,
    ILogger<RetentionPurgeService> logger) : IRetentionPurgeService
{
    public const int DefaultBatchSize = 500;

    public async Task<PurgeRunResult> PurgeAsync(bool dryRun, int batchSize, CancellationToken ct)
    {
        batchSize = batchSize <= 0 ? DefaultBatchSize : Math.Min(batchSize, 5000);
        var startedAt = DateTime.UtcNow;

        var clients = await db.Clients
            .AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.RetentionMonths, c.LegalHold })
            .ToListAsync(ct);

        var perClient = new List<ClientPurgeResult>();
        var totalReports = 0;
        var totalIngest = 0;
        var totalTlsPolicies = 0;
        var totalTlsIngest = 0;
        var held = 0;

        foreach (var client in clients)
        {
            ct.ThrowIfCancellationRequested();

            // A retention window of 0 or less would mean "delete everything";
            // treat it as misconfiguration and fall back to the documented default
            // rather than destroying data.
            var months = client.RetentionMonths > 0 ? client.RetentionMonths : 27;
            var cutoff = startedAt.AddMonths(-months);

            if (client.LegalHold)
            {
                held++;
                perClient.Add(new ClientPurgeResult(client.Id, client.Name, months, cutoff, 0, 0, 0, 0, true));
                logger.LogInformation(
                    "Retention: skipping client {ClientId} ({ClientName}) — legal hold",
                    client.Id, client.Name);
                continue;
            }

            var reports = await PurgeReportsAsync(client.Id, cutoff, dryRun, batchSize, ct);
            var ingest = await PurgeIngestLedgerAsync(client.Id, cutoff, dryRun, batchSize, ct);
            var tlsPolicies = await PurgeTlsPoliciesAsync(client.Id, cutoff, dryRun, batchSize, ct);
            var tlsIngest = await PurgeTlsIngestLedgerAsync(client.Id, cutoff, dryRun, batchSize, ct);

            totalReports += reports;
            totalIngest += ingest;
            totalTlsPolicies += tlsPolicies;
            totalTlsIngest += tlsIngest;
            perClient.Add(new ClientPurgeResult(
                client.Id, client.Name, months, cutoff, reports, ingest, tlsPolicies, tlsIngest, false));

            if (reports > 0 || ingest > 0)
            {
                logger.LogInformation(
                    "Retention: {Verb} {Reports} reports and {Ingest} ingest rows for client {ClientId} " +
                    "({ClientName}) older than {Cutoff:yyyy-MM-dd} ({Months} month retention)",
                    dryRun ? "would delete" : "deleted", reports, ingest, client.Id, client.Name, cutoff, months);
            }
        }

        // TLS reports have no client of their own — a single report can span
        // domains of several clients — so per-client retention deletes the policy
        // rows, and this sweep removes reports left with no policies at all. The
        // maximum retention across clients guards freshly ingested zero-policy
        // reports, and a held client's policy rows never delete, so its reports
        // never orphan: legal hold is safe by construction.
        var maxMonths = clients.Count == 0
            ? 27
            : clients.Max(c => c.RetentionMonths > 0 ? c.RetentionMonths : 27);
        var tlsReportsDeleted = await PurgeOrphanedTlsReportsAsync(
            startedAt.AddMonths(-maxMonths), dryRun, batchSize, ct);

        var auditDeleted = await PurgeAuditTrailAsync(startedAt, dryRun, batchSize, ct);

        var result = new PurgeRunResult(
            dryRun, startedAt, clients.Count, held, totalReports, totalIngest,
            totalTlsPolicies, totalTlsIngest, tlsReportsDeleted, auditDeleted, perClient);

        logger.LogInformation(
            "Retention run complete: {Verb} {Reports} reports, {Ingest} ingest rows across {Clients} clients " +
            "({Held} on legal hold)",
            dryRun ? "would delete" : "deleted", totalReports, totalIngest, clients.Count, held);

        return result;
    }

    /// <summary>
    /// Ages out the audit trail on its own, much longer window. Deliberately not
    /// subject to a client's retention setting or legal hold: the trail records who
    /// did what across the whole install, including to clients that no longer exist.
    /// </summary>
    private async Task<int> PurgeAuditTrailAsync(
        DateTime now, bool dryRun, int batchSize, CancellationToken ct)
    {
        var days = options.Value.AuditRetentionDays;
        if (days <= 0)
        {
            return 0;   // 0 or less means keep the trail forever
        }

        var cutoff = now.AddDays(-days);
        var expired = db.AuditEvents.Where(e => e.OccurredAtUtc < cutoff);

        if (dryRun)
        {
            return await expired.CountAsync(ct);
        }

        var deleted = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await expired.OrderBy(e => e.OccurredAtUtc).Take(batchSize).ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            db.AuditEvents.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            deleted += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        return deleted;
    }

    private async Task<int> PurgeReportsAsync(
        Guid clientId, DateTime cutoff, bool dryRun, int batchSize, CancellationToken ct)
    {
        // Reports reach a client through their domain.
        var expired = db.DmarcReports
            .Where(r => r.Domain!.ClientId == clientId && r.RangeEndUtc < cutoff);

        if (dryRun)
        {
            return await expired.CountAsync(ct);
        }

        var deleted = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Batched so a large backlog doesn't run one enormous transaction and
            // hold locks across the whole table.
            var batch = await expired.OrderBy(r => r.RangeEndUtc).Take(batchSize).ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            db.DmarcReports.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            deleted += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        return deleted;
    }

    private async Task<int> PurgeIngestLedgerAsync(
        Guid clientId, DateTime cutoff, bool dryRun, int batchSize, CancellationToken ct)
    {
        var expired = db.DmarcReportIngests
            .Where(i => i.ClientId == clientId && i.ReportRangeEndUtc < cutoff);

        if (dryRun)
        {
            return await expired.CountAsync(ct);
        }

        var deleted = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await expired.OrderBy(i => i.ReportRangeEndUtc).Take(batchSize).ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            db.DmarcReportIngests.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            deleted += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        return deleted;
    }

    /// <summary>
    /// TLS retention operates on the policy rows, which reach a client through
    /// their domain — the report row itself has none. Failure details cascade at
    /// the database level. Keyed on the reporting window end, same doctrine as
    /// the DMARC purge.
    /// </summary>
    private async Task<int> PurgeTlsPoliciesAsync(
        Guid clientId, DateTime cutoff, bool dryRun, int batchSize, CancellationToken ct)
    {
        var expired = db.SmtpTlsReportPolicies
            .Where(p => p.Domain!.ClientId == clientId && p.ReportRangeEndUtc < cutoff);

        if (dryRun)
        {
            return await expired.CountAsync(ct);
        }

        var deleted = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await expired.OrderBy(p => p.ReportRangeEndUtc).Take(batchSize).ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            db.SmtpTlsReportPolicies.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            deleted += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        return deleted;
    }

    private async Task<int> PurgeTlsIngestLedgerAsync(
        Guid clientId, DateTime cutoff, bool dryRun, int batchSize, CancellationToken ct)
    {
        var expired = db.TlsReportIngests
            .Where(i => i.ClientId == clientId && i.ReportRangeEndUtc < cutoff);

        if (dryRun)
        {
            return await expired.CountAsync(ct);
        }

        var deleted = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await expired.OrderBy(i => i.ReportRangeEndUtc).Take(batchSize).ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            db.TlsReportIngests.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            deleted += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        return deleted;
    }

    /// <summary>Report rows whose every policy has been purged, older than the oldest cutoff any client gets.</summary>
    private async Task<int> PurgeOrphanedTlsReportsAsync(
        DateTime oldestCutoff, bool dryRun, int batchSize, CancellationToken ct)
    {
        var expired = db.SmtpTlsReports
            .Where(r => r.RangeEndUtc < oldestCutoff && !r.Policies.Any());

        if (dryRun)
        {
            return await expired.CountAsync(ct);
        }

        var deleted = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var batch = await expired.OrderBy(r => r.RangeEndUtc).Take(batchSize).ToListAsync(ct);
            if (batch.Count == 0)
            {
                break;
            }

            db.SmtpTlsReports.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            deleted += batch.Count;

            if (batch.Count < batchSize)
            {
                break;
            }
        }

        return deleted;
    }
}
