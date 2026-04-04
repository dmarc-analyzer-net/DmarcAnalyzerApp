namespace DmarcAnalyzer.Api.Application.Ingestion;

public interface IMailboxHealthQueryService
{
    Task<IReadOnlyList<MailboxSourceHealthDto>> ListAsync(Guid? mailboxSourceId, CancellationToken ct);
}
