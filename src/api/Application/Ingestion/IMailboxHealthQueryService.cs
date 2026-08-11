namespace DmarcAnalyzer.Api.Application.Ingestion;

public interface IMailboxHealthQueryService
{
    Task<IReadOnlyList<ReportSourceHealthDto>> ListAsync(Guid? reportSourceId, CancellationToken ct);
}
