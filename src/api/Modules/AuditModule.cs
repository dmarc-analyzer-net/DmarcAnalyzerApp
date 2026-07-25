using Carter;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

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
            DmarcAnalyzerDbContext db,
            CancellationToken ct) =>
        {
            var since = DateTime.UtcNow.AddDays(-Math.Clamp(days ?? 30, 1, 730));
            var query = db.AuditEvents.AsNoTracking().Where(e => e.OccurredAtUtc >= since);

            if (!string.IsNullOrWhiteSpace(eventType))
            {
                var prefix = eventType.Trim().ToLowerInvariant();
                // Prefix match so `client` finds client.created and client.updated.
                query = query.Where(e => e.EventType == prefix || e.EventType.StartsWith(prefix + "."));
            }

            if (!string.IsNullOrWhiteSpace(actor))
            {
                var needle = actor.Trim().ToLowerInvariant();
                query = query.Where(e => e.ActorEmail.ToLower().Contains(needle));
            }

            if (clientId is { } cid)
            {
                query = query.Where(e => e.ClientId == cid);
            }

            var items = await query
                .OrderByDescending(e => e.OccurredAtUtc)
                .Take(Math.Clamp(limit ?? 200, 1, 1000))
                .Select(e => new
                {
                    e.Id, e.OccurredAtUtc, e.ActorType, e.ActorUserId, e.ActorEmail, e.EventType,
                    e.TargetType, e.TargetId, e.ClientId, e.Summary, e.Details, e.IpAddress,
                })
                .ToListAsync(ct);

            return Results.Ok(items);
        }).RequireAgencyAdmin();
    }
}
