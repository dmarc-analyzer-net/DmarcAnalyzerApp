using DmarcAnalyzer.Api.Application.Common;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// One full sync of one polled source: open the transport, walk messages past
/// the checkpoint, extract and ingest payloads, advance the checkpoint, and
/// record the sync run.
/// </summary>
public interface IMailboxSyncService
{
    /// <summary>Sync recorded with the default (scheduled) trigger.</summary>
    Task<ServiceResult<MailboxSyncResult>> SyncReportSourceAsync(Guid reportSourceId, CancellationToken ct);

    /// <summary>Same, with the trigger recorded on the run row — "manual" for the console button.</summary>
    Task<ServiceResult<MailboxSyncResult>> SyncReportSourceAsync(Guid reportSourceId, string trigger, CancellationToken ct);
}
