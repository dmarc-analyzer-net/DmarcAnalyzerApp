namespace DmarcAnalyzer.Api.Application.Ingestion;

public interface IMailboxSyncRunQueryService
{
    Task<IReadOnlyList<MailboxSyncRunDto>> ListAsync(Guid? reportSourceId, int limit, CancellationToken ct);
}
