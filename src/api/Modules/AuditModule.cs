using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

/// <summary>
/// The audit trail is read-only over HTTP by design — there is no endpoint to
/// edit or delete an entry, because a trail that can be rewritten isn't evidence.
/// Ageing entries out is the retention job's business, not an operator's.
/// </summary>
public sealed class AuditModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/admin/audit-events", async (
            int? days,
            string? eventType,
            string? actor,
            Guid? clientId,
            int? limit,
            int? offset,
            AuditQueryService audit,
            CancellationToken ct) =>
        {
            var page = await audit.QueryAsync(
                new AuditQuery(days, eventType, actor, clientId, limit, offset), ct);
            return Results.Ok(page);
        }).RequireAgencyAdmin();
    }
}
