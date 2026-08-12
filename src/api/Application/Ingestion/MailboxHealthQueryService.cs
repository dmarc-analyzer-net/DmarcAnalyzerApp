using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed class MailboxHealthQueryService(DmarcAnalyzerDbContext db) : IMailboxHealthQueryService
{
    public async Task<IReadOnlyList<ReportSourceHealthDto>> ListAsync(Guid? reportSourceId, CancellationToken ct)
    {
        // Mailbox health, and only a polled source has a mailbox. A pushed source has no
        // sync run, no checkpoint and no UIDVALIDITY, so including it would put a row in
        // this list that is permanently "never synced" — and the console's stale-success
        // filter treats a missing last success as a problem, so it would sit there looking
        // broken forever while working perfectly.
        var reportSources = db.ReportSources
            .AsNoTracking()
            .Where(x => x.Protocol == ReportSourceProtocols.Imap)
            .AsQueryable();

        if (reportSourceId.HasValue)
        {
            reportSources = reportSources.Where(x => x.Id == reportSourceId.Value);
        }

        return await reportSources
            .OrderBy(x => x.Name)
            .Select(source => new ReportSourceHealthDto(
                source.Id,
                source.Name,
                source.IsActive,
                source.LastSuccessSyncAtUtc,
                source.LastProcessedUid,
                source.LastProcessedUidValidity,
                db.MailboxSyncRuns
                    .Where(run => run.ReportSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => run.Status)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.ReportSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (DateTime?)run.StartedAtUtc)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.ReportSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => run.FinishedAtUtc)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.ReportSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => run.Error)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.ReportSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (int?)run.MessagesScanned)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.ReportSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (int?)run.AttachmentsProcessed)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.ReportSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (int?)run.ReportsInserted)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.ReportSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (int?)run.ReportsSkippedAsDuplicate)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.ReportSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (int?)run.ParseFailures)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.ReportSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (int?)run.TlsReportsInserted)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.ReportSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (int?)run.TlsReportsSkippedAsDuplicate)
                    .FirstOrDefault()))
            .ToListAsync(ct);
    }
}
