namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>The mailbox-health card's read side — polled sources only, since pushed sources have no sync runs.</summary>
public interface IMailboxHealthQueryService
{
    /// <summary>Checkpoint and latest-run state per polled source, optionally just one.</summary>
    Task<IReadOnlyList<ReportSourceHealthDto>> ListAsync(Guid? reportSourceId, CancellationToken ct);
}
