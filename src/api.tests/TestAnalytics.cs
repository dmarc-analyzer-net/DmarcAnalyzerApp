using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace DmarcAnalyzer.Api.Tests;

internal static class TestAnalytics
{
    /// <summary>
    /// Builds the analytics service with a stubbed resolver. The real DnsPolicyCache is
    /// used rather than a fake, so tests also cover the write-back that corrects the
    /// cached policy from a detail-page lookup.
    /// </summary>
    public static AnalyticsQueryService Service(
        DmarcAnalyzerDbContext db,
        ICurrentUserContext user,
        IDnsTxtResolver? dns = null)
    {
        var resolver = dns ?? TestDnsTxtResolver.Empty();
        var policyResolver = new DmarcPolicyResolver(resolver);
        return new AnalyticsQueryService(
            db,
            user,
            policyResolver,
            new DnsPolicyCache(db, policyResolver, NullLogger<DnsPolicyCache>.Instance),
            NullLogger<AnalyticsQueryService>.Instance);
    }
}
