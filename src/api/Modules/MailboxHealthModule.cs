using Carter;
using DmarcAnalyzer.Api.Application.Ingestion;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

public sealed class MailboxHealthModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/mailbox-health", async (
            Guid? reportSourceId,
            IMailboxHealthQueryService service,
            CancellationToken ct) =>
        {
            var items = await service.ListAsync(reportSourceId, ct);
            return Results.Ok(items);
        });
    }
}
