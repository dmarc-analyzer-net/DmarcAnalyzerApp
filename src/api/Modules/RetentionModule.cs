using Carter;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Retention;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

/// <summary>
/// Operator control over retention. The worker enforces retention on its own
/// schedule; these endpoints exist so an admin can preview the effect before it
/// happens, and run it on demand rather than waiting for the next daily pass.
/// </summary>
public sealed class RetentionModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        // Non-destructive: reports what the next purge would remove, per client.
        app.MapGet("/api/v1/admin/retention/preview", async (
            IRetentionPurgeService service,
            CancellationToken ct) =>
        {
            var result = await service.PurgeAsync(dryRun: true, RetentionPurgeService.DefaultBatchSize, ct);
            return Results.Ok(result);
        }).RequireAgencyAdmin();

        app.MapPost("/api/v1/admin/retention/purge", async (
            int? batchSize,
            IRetentionPurgeService service,
            CancellationToken ct) =>
        {
            var result = await service.PurgeAsync(
                dryRun: false, batchSize ?? RetentionPurgeService.DefaultBatchSize, ct);
            return Results.Ok(result);
        }).RequireAgencyAdmin();
    }
}
