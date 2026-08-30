using Carter;
using DmarcAnalyzer.Api.Application.Ingestion;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

/// <summary>GET /api/v1/mailbox-health — checkpoint and last-run state per polled source.</summary>
public sealed class MailboxHealthModule : ICarterModule
{
    /// <inheritdoc />
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
