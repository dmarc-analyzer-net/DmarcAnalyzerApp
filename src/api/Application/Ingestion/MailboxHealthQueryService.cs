using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed class MailboxHealthQueryService(DmarcAnalyzerDbContext db) : IMailboxHealthQueryService
{
    public async Task<IReadOnlyList<MailboxSourceHealthDto>> ListAsync(Guid? mailboxSourceId, CancellationToken ct)
    {
        var mailboxSources = db.MailboxSources
            .AsNoTracking()
            .AsQueryable();

        if (mailboxSourceId.HasValue)
        {
            mailboxSources = mailboxSources.Where(x => x.Id == mailboxSourceId.Value);
        }

        return await mailboxSources
            .OrderBy(x => x.Name)
            .Select(source => new MailboxSourceHealthDto(
                source.Id,
                source.Name,
                source.IsActive,
                source.LastSuccessSyncAtUtc,
                source.LastProcessedUid,
                source.LastProcessedUidValidity,
                db.MailboxSyncRuns
                    .Where(run => run.MailboxSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => run.Status)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.MailboxSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (DateTime?)run.StartedAtUtc)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.MailboxSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => run.FinishedAtUtc)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.MailboxSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => run.Error)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.MailboxSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (int?)run.MessagesScanned)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.MailboxSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (int?)run.AttachmentsProcessed)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.MailboxSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (int?)run.ReportsInserted)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.MailboxSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (int?)run.ReportsSkippedAsDuplicate)
                    .FirstOrDefault(),
                db.MailboxSyncRuns
                    .Where(run => run.MailboxSourceId == source.Id)
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (int?)run.ParseFailures)
                    .FirstOrDefault()))
            .ToListAsync(ct);
    }
}
