namespace DmarcAnalyzer.Api.Application.Analytics;

public interface IAnalyticsQueryService
{
    Task<AnalyticsSummaryDto> GetSummaryAsync(int days, CancellationToken ct);
    Task<IReadOnlyList<DomainAnalyticsDto>> ListDomainAnalyticsAsync(int days, CancellationToken ct);
}
