using Carter;
using DmarcAnalyzer.Api.Application.Ingestion;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

public sealed class MailboxSyncRunsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mailbox-sync-runs", async (
            Guid? mailboxSourceId,
            int? limit,
            IMailboxSyncRunQueryService service,
            CancellationToken ct) =>
        {
            var items = await service.ListAsync(mailboxSourceId, limit ?? 50, ct);
            return Results.Ok(items);
        });
    }
}
