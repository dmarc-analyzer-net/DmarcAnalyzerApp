using DmarcAnalyzer.Api.Application.Common;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// One full sync of one polled source: open the transport, walk messages past
/// the checkpoint, extract and ingest payloads, advance the checkpoint, and
/// record the sync run.
/// </summary>
public interface IMailboxSyncService
{
    /// <summary>Sync recorded with the "manual" trigger — the console button's path.</summary>
    Task<ServiceResult<MailboxSyncResult>> SyncReportSourceAsync(Guid reportSourceId, CancellationToken ct);

    /// <summary>Same, with the caller's trigger recorded on the run row — the worker passes "scheduled".</summary>
    Task<ServiceResult<MailboxSyncResult>> SyncReportSourceAsync(Guid reportSourceId, string trigger, CancellationToken ct);
}
