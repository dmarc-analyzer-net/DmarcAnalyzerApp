using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Notifications;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Modules;

/// <summary>
/// Body of POST /api/v1/notification-recipients (create only — a duplicate is a 409).
/// A set ClientId covers that one client; a null one covers several, by
/// <see cref="DigestDefaultMode"/> plus any <see cref="ClientModes"/>. Kind is alert,
/// digest or both.
/// </summary>
public sealed record UpsertRecipientRequest(
    Guid? ClientId,
    string Email,
    string? Kind,
    bool? IsActive,
    string? DigestDefaultMode = null,
    IReadOnlyList<RecipientClientModeRequest>? ClientModes = null);

/// <summary>One client's mode for a several-clients recipient: rollup, separate or off.</summary>
public sealed record RecipientClientModeRequest(Guid ClientId, string Mode);

/// <summary>
/// Body of PUT /api/v1/notification-recipients/{id}/routing. Replaces the default mode
/// and every per-client mode at once; clients left out take the default.
/// </summary>
public sealed record RecipientRoutingRequest(string DigestDefaultMode, IReadOnlyList<RecipientClientModeRequest>? ClientModes);

/// <summary>Endpoints under /api/v1/notification-recipients — who gets alert and digest mail.</summary>
public sealed class NotificationRecipientsModule : ICarterModule
{
    private static readonly string[] Kinds = ["alert", "digest", "both"];

    /// <inheritdoc />
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/notification-recipients", async (
            DmarcAnalyzerDbContext db, CancellationToken ct) =>
        {
            var items = await db.NotificationRecipients
                .AsNoTracking()
                .OrderBy(r => r.Email)
                .Select(r => new
                {
                    r.Id, r.ClientId, ClientName = r.Client != null ? r.Client.Name : null,
                    r.Email, r.Kind, r.IsActive, r.DigestDefaultMode,
                    ClientModes = r.ClientModes
                        .OrderBy(m => m.Client!.Name)
                        .Select(m => new { m.ClientId, Mode = m.DigestMode })
                        .ToList(),
                    r.CreatedAtUtc, r.UpdatedAtUtc,
                })
                .ToListAsync(ct);
            return Results.Ok(items);
        }).RequireAgencyStaff();

        app.MapPost("/api/v1/notification-recipients", async (
            UpsertRecipientRequest request, DmarcAnalyzerDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var email = (request.Email ?? string.Empty).Trim();
            if (email.Length == 0 || !email.Contains('@'))
            {
                return Results.Json(new { error = "a valid email is required" }, statusCode: 400);
            }

            var kind = (request.Kind ?? "both").Trim().ToLowerInvariant();
            if (!Kinds.Contains(kind))
            {
                return Results.Json(new { error = "kind must be alert, digest, or both" }, statusCode: 400);
            }

            if (request.ClientId is { } clientId &&
                !await db.Clients.AnyAsync(c => c.Id == clientId, ct))
            {
                return Results.NotFound();
            }

            // A null ClientId is the several-clients scope; unique per (scope, email).
            if (await db.NotificationRecipients.AnyAsync(
                    r => r.ClientId == request.ClientId && r.Email == email, ct))
            {
                return Results.Json(new { error = "that address already exists for this scope" }, statusCode: 409);
            }

            var recipient = new NotificationRecipient
            {
                ClientId = request.ClientId,
                Email = email,
                Kind = kind,
                IsActive = request.IsActive ?? true,
            };

            if (request.DigestDefaultMode is not null || request.ClientModes is { Count: > 0 })
            {
                var error = await RecipientRouting.ApplyAsync(
                    db, recipient, request.DigestDefaultMode ?? DigestModes.Rollup, Modes(request.ClientModes), ct);
                if (error is not null)
                {
                    return Results.Json(new { error }, statusCode: 400);
                }
            }

            db.NotificationRecipients.Add(recipient);
            await db.SaveChangesAsync(ct);

            await audit.RecordAsync(AuditEvents.NotificationRecipientAdded,
                $"Added notification recipient {recipient.Email} ({recipient.Kind})",
                "notification_recipient", recipient.Id, recipient.ClientId, ct: ct);

            return Results.Created($"/api/v1/notification-recipients/{recipient.Id}", new
            {
                recipient.Id, recipient.ClientId, recipient.Email, recipient.Kind, recipient.IsActive,
                recipient.DigestDefaultMode,
            });
        }).RequireAgencyAdmin();

        app.MapPut("/api/v1/notification-recipients/{id:guid}/routing", async (
            Guid id, RecipientRoutingRequest request, DmarcAnalyzerDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var recipient = await db.NotificationRecipients
                .Include(r => r.ClientModes)
                .FirstOrDefaultAsync(r => r.Id == id, ct);
            if (recipient is null)
            {
                return Results.NotFound();
            }

            var error = await RecipientRouting.ApplyAsync(
                db, recipient, request.DigestDefaultMode, Modes(request.ClientModes), ct);
            if (error is not null)
            {
                return Results.Json(new { error }, statusCode: 400);
            }

            recipient.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var counts = string.Join(", ", recipient.ClientModes
                .GroupBy(m => m.DigestMode)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} {g.Key}"));
            await audit.RecordAsync(AuditEvents.NotificationRecipientRoutingChanged,
                $"Changed clients for {recipient.Email}: default {recipient.DigestDefaultMode}" +
                (counts.Length > 0 ? $"; {counts}" : string.Empty),
                "notification_recipient", recipient.Id, recipient.ClientId, ct: ct);

            return Results.Ok(new
            {
                recipient.Id,
                recipient.DigestDefaultMode,
                ClientModes = recipient.ClientModes.Select(m => new { m.ClientId, Mode = m.DigestMode }),
            });
        }).RequireAgencyAdmin();

        app.MapDelete("/api/v1/notification-recipients/{id:guid}", async (
            Guid id, DmarcAnalyzerDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var recipient = await db.NotificationRecipients.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (recipient is null)
            {
                return Results.NotFound();
            }

            db.NotificationRecipients.Remove(recipient);
            await db.SaveChangesAsync(ct);

            await audit.RecordAsync(AuditEvents.NotificationRecipientRemoved,
                $"Removed notification recipient {recipient.Email}",
                "notification_recipient", id, recipient.ClientId, ct: ct);
            return Results.NoContent();
        }).RequireAgencyAdmin();
    }

    private static List<(Guid, string?)> Modes(IReadOnlyList<RecipientClientModeRequest>? modes)
        => (modes ?? []).Select(m => (m.ClientId, (string?)m.Mode)).ToList();
}
