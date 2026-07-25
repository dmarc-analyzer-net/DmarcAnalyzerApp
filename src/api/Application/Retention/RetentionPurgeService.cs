using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Retention;

/// <summary>One client's share of a purge run.</summary>
public sealed record ClientPurgeResult(
    Guid ClientId,
    string ClientName,
    int RetentionMonths,
    DateTime CutoffUtc,
    int ReportsDeleted,
    int IngestRowsDeleted,
    bool SkippedForLegalHold);

public sealed record PurgeRunResult(
    bool DryRun,
    DateTime StartedAtUtc,
    int ClientsConsidered,
    int ClientsOnLegalHold,
    int ReportsDeleted,
    int IngestRowsDeleted,
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
                perClient.Add(new ClientPurgeResult(client.Id, client.Name, months, cutoff, 0, 0, true));
                logger.LogInformation(
                    "Retention: skipping client {ClientId} ({ClientName}) — legal hold",
                    client.Id, client.Name);
                continue;
            }

            var reports = await PurgeReportsAsync(client.Id, cutoff, dryRun, batchSize, ct);
            var ingest = await PurgeIngestLedgerAsync(client.Id, cutoff, dryRun, batchSize, ct);

            totalReports += reports;
            totalIngest += ingest;
            perClient.Add(new ClientPurgeResult(client.Id, client.Name, months, cutoff, reports, ingest, false));

            if (reports > 0 || ingest > 0)
            {
                logger.LogInformation(
                    "Retention: {Verb} {Reports} reports and {Ingest} ingest rows for client {ClientId} " +
                    "({ClientName}) older than {Cutoff:yyyy-MM-dd} ({Months} month retention)",
                    dryRun ? "would delete" : "deleted", reports, ingest, client.Id, client.Name, cutoff, months);
            }
        }

        var result = new PurgeRunResult(
            dryRun, startedAt, clients.Count, held, totalReports, totalIngest, perClient);

        logger.LogInformation(
            "Retention run complete: {Verb} {Reports} reports, {Ingest} ingest rows across {Clients} clients " +
            "({Held} on legal hold)",
            dryRun ? "would delete" : "deleted", totalReports, totalIngest, clients.Count, held);

        return result;
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
}
