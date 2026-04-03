using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed class MailboxSyncRunQueryService(DmarcAnalyzerDbContext db) : IMailboxSyncRunQueryService
{
    public async Task<IReadOnlyList<MailboxSyncRunDto>> ListAsync(Guid? mailboxSourceId, int limit, CancellationToken ct)
    {
        var boundedLimit = Math.Clamp(limit, 1, 200);

        var query = db.MailboxSyncRuns
            .AsNoTracking()
            .OrderByDescending(x => x.StartedAtUtc)
            .AsQueryable();

        if (mailboxSourceId.HasValue)
        {
            query = query.Where(x => x.MailboxSourceId == mailboxSourceId.Value);
        }

        return await query
            .Take(boundedLimit)
            .Select(x => new MailboxSyncRunDto(
                x.Id,
                x.MailboxSourceId,
                x.Trigger,
                x.Status,
                x.StartedAtUtc,
                x.FinishedAtUtc,
                x.MessagesScanned,
                x.AttachmentsProcessed,
                x.ReportsInserted,
                x.ReportsSkippedAsDuplicate,
                x.ParseFailures,
                x.Error,
                x.CreatedAtUtc))
            .ToListAsync(ct);
    }
}
