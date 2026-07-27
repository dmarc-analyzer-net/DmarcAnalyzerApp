using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// One append-only table, and how to page through it by time.
/// </summary>
/// <param name="Name">Also the object-key segment and the <c>backup_stream_state</c> key.</param>
/// <param name="ReadAsync">Rows at or after <paramref name="since"/>, oldest first. Null means everything.</param>
/// <param name="TimestampOf">The row's own time, used to advance the watermark.</param>
public sealed record BackupHistoryStream(
    string Name,
    Func<DmarcAnalyzerDbContext, DateTime?, CancellationToken, Task<IReadOnlyList<object>>> ReadAsync,
    Func<object, DateTime> TimestampOf);

/// <summary>
/// The tables no report replay can reconstruct.
/// <para>
/// Re-ingesting a mailbox rebuilds every report, but it cannot rebuild these: the audit
/// trail is a compliance record of the install itself, and alerts and digests were
/// computed from report data <em>at evaluation time</em>, so replaying the reports does not
/// replay the events they produced. That is the whole reason this tier exists — and the
/// reason it is cheap is that every one of these tables is append-only, so an object once
/// written never needs rewriting.
/// </para>
/// <para>
/// The ingest ledger is included even though it is derivable, because it is small and it
/// is what an operator reads to answer "did we ever receive that report?" after a
/// retention purge has removed the report itself.
/// </para>
/// </summary>
public static class BackupHistoryStreams
{
    /// <summary>
    /// A page bound, so one pass cannot try to serialize years of audit history into a
    /// single object after a long outage. The watermark advances to whatever was shipped,
    /// so the next pass simply continues.
    /// </summary>
    public const int MaxRowsPerPass = 5000;

    public static readonly BackupHistoryStream AuditEvents = new(
        "audit_event",
        async (db, since, ct) => await Page(
            db.AuditEvents.AsNoTracking()
                .Where(x => since == null || x.OccurredAtUtc >= since)
                .OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id),
            ct),
        row => ((Data.Entities.AuditEvent)row).OccurredAtUtc);

    public static readonly BackupHistoryStream AlertEvents = new(
        "alert_event",
        async (db, since, ct) => await Page(
            db.AlertEvents.AsNoTracking()
                .Where(x => since == null || x.DetectedAtUtc >= since)
                .OrderBy(x => x.DetectedAtUtc).ThenBy(x => x.Id),
            ct),
        row => ((Data.Entities.AlertEvent)row).DetectedAtUtc);

    public static readonly BackupHistoryStream DigestDeliveries = new(
        "digest_delivery",
        async (db, since, ct) => await Page(
            db.DigestDeliveries.AsNoTracking()
                .Where(x => since == null || x.SentAtUtc >= since)
                .OrderBy(x => x.SentAtUtc).ThenBy(x => x.Id),
            ct),
        row => ((Data.Entities.DigestDelivery)row).SentAtUtc);

    public static readonly BackupHistoryStream MailboxSyncRuns = new(
        "mailbox_sync_run",
        async (db, since, ct) => await Page(
            db.MailboxSyncRuns.AsNoTracking()
                .Where(x => since == null || x.CreatedAtUtc >= since)
                .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
            ct),
        row => ((Data.Entities.MailboxSyncRun)row).CreatedAtUtc);

    public static readonly BackupHistoryStream ReportIngests = new(
        "dmarc_report_ingest",
        async (db, since, ct) => await Page(
            db.DmarcReportIngests.AsNoTracking()
                .Where(x => since == null || x.IngestedAtUtc >= since)
                .OrderBy(x => x.IngestedAtUtc).ThenBy(x => x.Id),
            ct),
        row => ((Data.Entities.DmarcReportIngest)row).IngestedAtUtc);

    public static readonly IReadOnlyList<BackupHistoryStream> All =
    [
        AuditEvents, AlertEvents, DigestDeliveries, MailboxSyncRuns, ReportIngests,
    ];

    private static async Task<IReadOnlyList<object>> Page<T>(IQueryable<T> query, CancellationToken ct)
        where T : class
        => [.. await query.Take(MaxRowsPerPass).ToListAsync(ct)];
}
