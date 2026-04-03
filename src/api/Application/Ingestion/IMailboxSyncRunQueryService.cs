namespace DmarcAnalyzer.Api.Application.Ingestion;

public interface IMailboxSyncRunQueryService
{
    Task<IReadOnlyList<MailboxSyncRunDto>> ListAsync(Guid? mailboxSourceId, int limit, CancellationToken ct);
}
