using Carter;
using DmarcAnalyzer.Api.Application.Analytics;

namespace DmarcAnalyzer.Api.Modules;

public sealed class AnalyticsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/analytics/summary", async (
            int? days,
            IAnalyticsQueryService service,
            CancellationToken ct) =>
        {
            var summary = await service.GetSummaryAsync(days ?? 30, ct);
            return Results.Ok(summary);
        });

        app.MapGet("/api/v1/analytics/domains", async (
            int? days,
            IAnalyticsQueryService service,
            CancellationToken ct) =>
        {
            var items = await service.ListDomainAnalyticsAsync(days ?? 30, ct);
            return Results.Ok(items);
        });
    }
}
