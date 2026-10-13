using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Backup;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

/// <summary>
/// Operator control over the report backfill (PR 4 of docs/planning/rfc-9990-plan.md): the
/// same preview-then-run shape as retention, except that the run is queued for the worker
/// rather than executed in the request — walking an archive takes as long as the archive is
/// big, and the cursor row is what reports progress.
/// </summary>
public sealed class ReportBackfillModule : ICarterModule
{
    /// <inheritdoc />
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        // Counts only; writes nothing.
        app.MapGet("/api/v1/admin/report-backfill/preview", async (
            IReportBackfillService service,
            CancellationToken ct) =>
        {
            return Results.Ok(await service.PreviewAsync(ct));
        }).RequireAgencyAdmin();

        // The cursor row: whether a run is requested, running, done or failed, and its counts.
        app.MapGet("/api/v1/admin/report-backfill/status", async (
            IReportBackfillService service,
            CancellationToken ct) =>
        {
            return Results.Ok(await service.GetStateAsync(ct));
        }).RequireAgencyAdmin();

        app.MapPost("/api/v1/admin/report-backfill/run", async (
            IReportBackfillService service,
            IAuditLog audit,
            CancellationToken ct) =>
        {
            var result = await service.RequestRunAsync(ct);

            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            await audit.RecordAsync(
                AuditEvents.ReportBackfillRequested,
                "Requested a backfill of report-level fields from the report-mail archive",
                "report_backfill",
                ct: ct);

            // Accepted, not OK: the worker does the work on its next pass.
            return Results.Accepted("/api/v1/admin/report-backfill/status", result.Value);
        }).RequireAgencyAdmin();
    }
}
