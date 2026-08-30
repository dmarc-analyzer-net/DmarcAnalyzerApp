using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>EF-backed <see cref="IMailboxSyncRunQueryService"/>; the limit is clamped to 1–200.</summary>
public sealed class MailboxSyncRunQueryService(DmarcAnalyzerDbContext db) : IMailboxSyncRunQueryService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<MailboxSyncRunDto>> ListAsync(Guid? reportSourceId, int limit, CancellationToken ct)
    {
        var boundedLimit = Math.Clamp(limit, 1, 200);

        var query = db.MailboxSyncRuns
            .AsNoTracking()
            .OrderByDescending(x => x.StartedAtUtc)
            .AsQueryable();

        if (reportSourceId.HasValue)
        {
            query = query.Where(x => x.ReportSourceId == reportSourceId.Value);
        }

        return await query
            .Take(boundedLimit)
            .Select(x => new MailboxSyncRunDto(
                x.Id,
                x.ReportSourceId,
                x.Trigger,
                x.Status,
                x.StartedAtUtc,
                x.FinishedAtUtc,
                x.MessagesScanned,
                x.AttachmentsProcessed,
                x.ReportsInserted,
                x.ReportsSkippedAsDuplicate,
                x.ParseFailures,
                x.TlsReportsInserted,
                x.TlsReportsSkippedAsDuplicate,
                x.Error,
                x.CreatedAtUtc))
            .ToListAsync(ct);
    }
}
