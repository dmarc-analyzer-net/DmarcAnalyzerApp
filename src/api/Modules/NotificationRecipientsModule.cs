using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Modules;

public sealed record UpsertRecipientRequest(Guid? ClientId, string Email, string? Kind, bool? IsActive);

public sealed class NotificationRecipientsModule : ICarterModule
{
    private static readonly string[] Kinds = ["alert", "digest", "both"];

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
                    r.Email, r.Kind, r.IsActive, r.CreatedAtUtc, r.UpdatedAtUtc,
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

            // A null ClientId is the agency-wide scope; unique per (scope, email).
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
            db.NotificationRecipients.Add(recipient);
            await db.SaveChangesAsync(ct);

            await audit.RecordAsync(AuditEvents.NotificationRecipientAdded,
                $"Added notification recipient {recipient.Email} ({recipient.Kind})",
                "notification_recipient", recipient.Id, recipient.ClientId, ct: ct);

            return Results.Created($"/api/v1/notification-recipients/{recipient.Id}", new
            {
                recipient.Id, recipient.ClientId, recipient.Email, recipient.Kind, recipient.IsActive,
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
}
