using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Audit;

/// <summary>One audit-trail entry as the console reads it.</summary>
public sealed record AuditEventDto(
    Guid Id,
    DateTime OccurredAtUtc,
    string ActorType,
    Guid? ActorUserId,
    string ActorEmail,
    string EventType,
    string? TargetType,
    Guid? TargetId,
    Guid? ClientId,
    /// <summary>
    /// The client's name as recorded when the event happened. Falls back to the
    /// current name for rows written before that was captured, and is null when
    /// the event has no client or the client is gone and nothing was recorded.
    /// </summary>
    string? ClientName,
    string Summary,
    string? Details,
    string? IpAddress,
    string? UserAgent);

/// <summary>A page of the trail plus the unpaged total, so the console can say
/// "showing 100 of 4,812" instead of leaving the reader to guess.</summary>
public sealed record AuditEventPageDto(int Total, IReadOnlyList<AuditEventDto> Items);

public sealed record AuditQuery(
    int? Days = null,
    string? EventType = null,
    string? Actor = null,
    Guid? ClientId = null,
    int? Limit = null,
    int? Offset = null);

/// <summary>
/// Reads the audit trail. Read-only on purpose — there is no write path here,
/// because a trail the console can rewrite isn't evidence.
/// </summary>
public sealed class AuditQueryService(DmarcAnalyzerDbContext db)
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;
    public const int MaxDays = 730;

    public async Task<AuditEventPageDto> QueryAsync(AuditQuery request, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(request.Days ?? 30, 1, MaxDays));
        var query = db.AuditEvents.AsNoTracking().Where(e => e.OccurredAtUtc >= since);

        if (!string.IsNullOrWhiteSpace(request.EventType))
        {
            var prefix = request.EventType.Trim().ToLowerInvariant();
            // Prefix match so `client` finds client.created and client.updated.
            query = query.Where(e => e.EventType == prefix || e.EventType.StartsWith(prefix + "."));
        }

        if (!string.IsNullOrWhiteSpace(request.Actor))
        {
            var needle = request.Actor.Trim().ToLowerInvariant();
            query = query.Where(e => e.ActorEmail.ToLower().Contains(needle));
        }

        if (request.ClientId is { } clientId)
        {
            query = query.Where(e => e.ClientId == clientId);
        }

        var total = await query.CountAsync(ct);

        // Left join rather than a navigation: audit_event deliberately has no FK to
        // client, so the trail outlives the rows it refers to. Still needed after
        // ClientName was added, because rows written before it are null and fall
        // back to the current name.
        var items = await (
            from e in query
            join c in db.Clients on e.ClientId equals c.Id into clients
            from c in clients.DefaultIfEmpty()
            orderby e.OccurredAtUtc descending, e.Id
            select new AuditEventDto(
                e.Id, e.OccurredAtUtc, e.ActorType, e.ActorUserId, e.ActorEmail, e.EventType,
                e.TargetType, e.TargetId, e.ClientId,
                // Prefer the name recorded at write time. Rows predating that
                // column fall back to the live join, which is exactly the
                // behaviour they had before — and they age out with the trail's
                // two-year retention.
                e.ClientName ?? (c != null ? c.Name : null),
                e.Summary, e.Details, e.IpAddress, e.UserAgent))
            .Skip(Math.Max(0, request.Offset ?? 0))
            .Take(Math.Clamp(request.Limit ?? DefaultLimit, 1, MaxLimit))
            .ToListAsync(ct);

        return new AuditEventPageDto(total, items);
    }
}
