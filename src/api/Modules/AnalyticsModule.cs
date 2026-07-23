using Carter;
using DmarcAnalyzer.Api.Application.Auth;
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
        }).AllowClientViewer();

        app.MapGet("/api/v1/analytics/domains", async (
            int? days,
            IAnalyticsQueryService service,
            CancellationToken ct) =>
        {
            var items = await service.ListDomainAnalyticsAsync(days ?? 30, ct);
            return Results.Ok(items);
        }).AllowClientViewer();

        app.MapGet("/api/v1/analytics/domains/{domainId:guid}/drilldown", async (
            Guid domainId,
            int? days,
            IAnalyticsQueryService service,
            CancellationToken ct) =>
        {
            var drilldown = await service.GetDomainDrilldownAsync(domainId, days ?? 30, ct);
            return drilldown is null ? Results.NotFound() : Results.Ok(drilldown);
        }).AllowClientViewer();

        app.MapGet("/api/v1/analytics/domains/{domainId:guid}/sources", async (
            Guid domainId,
            int? days,
            IAnalyticsQueryService service,
            CancellationToken ct) =>
        {
            var sources = await service.ListDomainSourcesAsync(domainId, days ?? 30, ct);
            return sources is null ? Results.NotFound() : Results.Ok(sources);
        }).AllowClientViewer();

        app.MapGet("/api/v1/analytics/domains/{domainId:guid}/source-detail", async (
            Guid domainId,
            string? ip,
            int? days,
            IAnalyticsQueryService service,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                return Results.Json(new { error = "ip query parameter is required" }, statusCode: 400);
            }

            var detail = await service.GetSourceDetailAsync(domainId, ip.Trim(), days ?? 30, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        }).AllowClientViewer();

        app.MapGet("/api/v1/analytics/hostnames", async (
            string? ips,
            IHostnameResolver resolver,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(ips))
            {
                return Results.Json(new { error = "ips query parameter is required" }, statusCode: 400);
            }

            var list = ips.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (list.Length > 100)
            {
                return Results.Json(new { error = "at most 100 ips per request" }, statusCode: 400);
            }

            var resolved = await resolver.ResolveAsync(list, ct);
            return Results.Ok(resolved);
        }).AllowClientViewer();
    }
}
