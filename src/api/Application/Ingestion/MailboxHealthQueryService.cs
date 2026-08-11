using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed class MailboxHealthQueryService(DmarcAnalyzerDbContext db) : IMailboxHealthQueryService
{
    public async Task<IReadOnlyList<ReportSourceHealthDto>> ListAsync(Guid? reportSourceId, CancellationToken ct)
    {
        var reportSources = db.ReportSources
            .AsNoTracking()
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
