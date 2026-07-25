using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.MailboxSources;
using DmarcAnalyzer.Api.Contracts.MailboxSources;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

public sealed class MailboxSourcesModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mailbox-sources", async (IMailboxSourceService service, CancellationToken ct) =>
        {
            var items = await service.ListAsync(ct);

            return Results.Ok(items);
        });

        app.MapPost("/api/v1/mailbox-sources", async (CreateMailboxSourceRequest request, IMailboxSourceService service, IAuditLog audit, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var source = result.Value!;

            await audit.RecordAsync(AuditEvents.MailboxSourceCreated,
                $"Created mailbox source {source.Name} ({source.Host})",
                "mailbox_source", source.Id, ct: ct);
            return Results.Created($"/api/v1/mailbox-sources/{source.Id}", source);
        }).RequireAgencyAdmin();

        app.MapPatch("/api/v1/mailbox-sources/{id:guid}", async (Guid id, UpdateMailboxSourceRequest request, IMailboxSourceService service, IAuditLog audit, CancellationToken ct) =>
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
            await audit.RecordAsync(AuditEvents.MailboxSourceUpdated,
                $"Updated mailbox source {updatedSource.Name}",
                "mailbox_source", updatedSource.Id, ct: ct);
            return Results.Ok(updatedSource);
        }).RequireAgencyAdmin();

        app.MapPost("/api/v1/mailbox-sources/{id:guid}/sync", async (Guid id, IMailboxSyncService service, IAuditLog audit, CancellationToken ct) =>
        {
            await audit.RecordAsync(AuditEvents.MailboxSyncTriggered,
                "Triggered a manual mailbox sync", "mailbox_source", id, ct: ct);

            var result = await service.SyncMailboxSourceAsync(id, ct);
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
