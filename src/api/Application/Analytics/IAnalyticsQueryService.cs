namespace DmarcAnalyzer.Api.Application.Analytics;

/// <summary>
/// The DMARC analytics read side. Every method is tenant-scoped through the
/// caller's <c>ICurrentUserContext</c>, windows are data-anchored (see
/// <see cref="AnalyticsWindowDto"/>), and per-domain lookups return null for
/// unknown *and* cross-tenant ids so the API answers 404 either way.
/// </summary>
public interface IAnalyticsQueryService
{
    /// <summary>The dashboard: totals, trend, top failing domains/reporters, dispositions, mailbox health.</summary>
    Task<AnalyticsSummaryDto> GetSummaryAsync(int days, CancellationToken ct);

    /// <summary>One row per visible domain, including domains with no reports in the window.</summary>
    Task<IReadOnlyList<DomainAnalyticsDto>> ListDomainAnalyticsAsync(int days, CancellationToken ct);

    /// <summary>The domain detail header and totals. Performs a live DNS policy lookup.</summary>
    Task<DomainDrilldownDto?> GetDomainDrilldownAsync(Guid domainId, int days, CancellationToken ct);

    /// <summary>Sending sources of one domain, highest volume first.</summary>
    Task<IReadOnlyList<DomainSourceDto>?> ListDomainSourcesAsync(Guid domainId, int days, CancellationToken ct);

    /// <summary>Everything reported about one (domain, source IP) pair.</summary>
    Task<SourceDetailDto?> GetSourceDetailAsync(Guid domainId, string sourceIp, int days, CancellationToken ct);

    /// <summary>The path-to-enforcement recommendation: what to publish next and what blocks it.</summary>
    Task<EnforcementGuidanceDto?> GetEnforcementGuidanceAsync(Guid domainId, int days, CancellationToken ct);

    /// <summary>Failing sources across every visible domain, optionally filtered to one client.</summary>
    Task<ThreatFeedDto> GetThreatFeedAsync(int days, int limit, Guid? clientId, CancellationToken ct);
}
