namespace DmarcAnalyzer.Api.Application.Analytics;

public interface IAnalyticsQueryService
{
    Task<AnalyticsSummaryDto> GetSummaryAsync(int days, CancellationToken ct);
    Task<IReadOnlyList<DomainAnalyticsDto>> ListDomainAnalyticsAsync(int days, CancellationToken ct);
    Task<DomainDrilldownDto?> GetDomainDrilldownAsync(Guid domainId, int days, CancellationToken ct);
    Task<IReadOnlyList<DomainSourceDto>?> ListDomainSourcesAsync(Guid domainId, int days, CancellationToken ct);
    Task<SourceDetailDto?> GetSourceDetailAsync(Guid domainId, string sourceIp, int days, CancellationToken ct);
}
