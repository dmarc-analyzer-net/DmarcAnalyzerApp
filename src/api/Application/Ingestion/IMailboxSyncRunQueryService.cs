namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>Read side of the sync-run history table on the report-source pages.</summary>
public interface IMailboxSyncRunQueryService
{
    /// <summary>Most recent runs, newest first, optionally for one source.</summary>
    Task<IReadOnlyList<MailboxSyncRunDto>> ListAsync(Guid? reportSourceId, int limit, CancellationToken ct);
}
