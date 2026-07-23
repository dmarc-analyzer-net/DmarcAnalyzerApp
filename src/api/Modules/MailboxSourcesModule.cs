using Carter;
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

        app.MapPost("/api/v1/mailbox-sources", async (CreateMailboxSourceRequest request, IMailboxSourceService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var source = result.Value!;

            return Results.Created($"/api/v1/mailbox-sources/{source.Id}", source);
        }).RequireAgencyAdmin();

        app.MapPatch("/api/v1/mailbox-sources/{id:guid}", async (Guid id, UpdateMailboxSourceRequest request, IMailboxSourceService service, CancellationToken ct) =>
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

            return Results.Ok(result.Value);
        }).RequireAgencyAdmin();

        app.MapPost("/api/v1/mailbox-sources/{id:guid}/sync", async (Guid id, IMailboxSyncService service, CancellationToken ct) =>
        {
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
