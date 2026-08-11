using DmarcAnalyzer.Api.Application.Common;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public interface IMailboxSyncService
{
    Task<ServiceResult<MailboxSyncResult>> SyncReportSourceAsync(Guid reportSourceId, CancellationToken ct);
    Task<ServiceResult<MailboxSyncResult>> SyncReportSourceAsync(Guid reportSourceId, string trigger, CancellationToken ct);
}
