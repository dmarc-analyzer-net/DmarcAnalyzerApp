using DmarcAnalyzer.Api.Application.Common;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public interface IMailboxSyncService
{
    Task<ServiceResult<MailboxSyncResult>> SyncMailboxSourceAsync(Guid mailboxSourceId, CancellationToken ct);
    Task<ServiceResult<MailboxSyncResult>> SyncMailboxSourceAsync(Guid mailboxSourceId, string trigger, CancellationToken ct);
}
