using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.ReportSources;
using DmarcAnalyzer.Api.Contracts.ReportSources;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

public sealed class ReportSourcesModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/report-sources", async (IReportSourceService service, CancellationToken ct) =>
        {
            var items = await service.ListAsync(ct);

            return Results.Ok(items);
        });

        app.MapPost("/api/v1/report-sources", async (CreateReportSourceRequest request, IReportSourceService service, IAuditLog audit, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var source = result.Value!;

            await audit.RecordAsync(AuditEvents.ReportSourceCreated,
                $"Created report source {source.Name} ({source.Host})",
                "mailbox_source", source.Id, ct: ct);
            return Results.Created($"/api/v1/report-sources/{source.Id}", source);
        }).RequireAgencyAdmin();

        app.MapPatch("/api/v1/report-sources/{id:guid}", async (Guid id, UpdateReportSourceRequest request, IReportSourceService service, IAuditLog audit, CancellationToken ct) =>
        {
            var result = await service.UpdateAsync(id, request, ct);
            if (!result.IsSuccess)
            {
                if (result.StatusCode == 404)
                {
                    return Results.NotFound();
                }

                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var updatedSource = result.Value!;

            // Turning mail deletion on is the one change here that leads to data being
            // destroyed outside this application, so it is named in the summary rather than
            // hidden inside a generic "updated".
            var summary = request.DeleteAfterRetention is { } deleteAfterRetention
                ? $"Updated report source {updatedSource.Name} — mail deletion past retention " +
                  $"{(deleteAfterRetention ? "ENABLED" : "disabled")}"
                : $"Updated report source {updatedSource.Name}";

            await audit.RecordAsync(AuditEvents.ReportSourceUpdated, summary,
                "mailbox_source", updatedSource.Id, ct: ct);
            return Results.Ok(updatedSource);
        }).RequireAgencyAdmin();

        app.MapPost("/api/v1/report-sources/{id:guid}/sync", async (Guid id, IMailboxSyncService service, IAuditLog audit, CancellationToken ct) =>
        {
            await audit.RecordAsync(AuditEvents.MailboxSyncTriggered,
                "Triggered a manual mailbox sync", "mailbox_source", id, ct: ct);

            var result = await service.SyncReportSourceAsync(id, ct);
            if (!result.IsSuccess)
            {
                if (result.StatusCode == 404)
                {
                    return Results.NotFound();
                }

                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var sync = result.Value!;
            var statusCode = sync.Success ? 200 : 502;
            return Results.Json(sync, statusCode: statusCode);
        });
    }
}
