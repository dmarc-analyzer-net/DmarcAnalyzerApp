using Carter;
using DmarcAnalyzer.Api.Contracts.Clients;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Modules;

public sealed class ClientsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/clients", async (DmarcAnalyzerDbContext db, CancellationToken ct) =>
        {
            var clients = await db.Clients
                .OrderBy(x => x.Name)
                .Select(x => new
                {
                    x.Id,
                    x.Name,
                    x.Slug,
                    x.IsActive,
                    x.RetentionMonths,
                    x.Timezone,
                    x.CreatedAtUtc,
                    x.UpdatedAtUtc,
                })
                .ToListAsync(ct);

            return Results.Ok(clients);
        });

        app.MapPost("/api/v1/clients", async (CreateClientRequest request, DmarcAnalyzerDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Slug))
            {
                return Results.BadRequest(new { error = "name and slug are required" });
            }

            var normalizedSlug = request.Slug.Trim().ToLowerInvariant();
            var slugInUse = await db.Clients.AnyAsync(x => x.Slug == normalizedSlug, ct);
            if (slugInUse)
            {
                return Results.Conflict(new { error = "client slug already exists" });
            }

            var now = DateTime.UtcNow;
            var client = new Client
            {
                Name = request.Name.Trim(),
                Slug = normalizedSlug,
                IsActive = request.IsActive,
                RetentionMonths = request.RetentionMonths <= 0 ? 27 : request.RetentionMonths,
                Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "UTC" : request.Timezone.Trim(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            db.Clients.Add(client);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/clients/{client.Id}", client);
        });
    }
}
