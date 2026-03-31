using Carter;
using DmarcAnalyzer.Api.Contracts.Domains;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Modules;

public sealed class DomainsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/domains/{id:guid}", async (Guid id, DmarcAnalyzerDbContext db, CancellationToken ct) =>
        {
            var domain = await db.Domains
                .AsNoTracking()
                .Include(x => x.Client)
                .Where(x => x.Id == id)
                .Select(x => new
                {
                    x.Id,
                    x.Name,
                    x.IsActive,
                    x.ClientId,
                    ClientName = x.Client != null ? x.Client.Name : null,
                    x.CreatedAtUtc,
                    x.UpdatedAtUtc,
                })
                .SingleOrDefaultAsync(ct);

            return domain is null ? Results.NotFound() : Results.Ok(domain);
        });

        app.MapGet("/api/v1/domains", async (Guid? clientId, DmarcAnalyzerDbContext db, CancellationToken ct) =>
        {
            var query = db.Domains
                .AsNoTracking()
                .Include(x => x.Client)
                .AsQueryable();

            if (clientId.HasValue)
            {
                query = query.Where(x => x.ClientId == clientId.Value);
            }

            var domains = await query
                .OrderBy(x => x.Name)
                .Select(x => new
                {
                    x.Id,
                    x.Name,
                    x.IsActive,
                    x.ClientId,
                    ClientName = x.Client != null ? x.Client.Name : null,
                    x.CreatedAtUtc,
                    x.UpdatedAtUtc,
                })
                .ToListAsync(ct);

            return Results.Ok(domains);
        });

        app.MapPost("/api/v1/domains", async (CreateDomainRequest request, DmarcAnalyzerDbContext db, CancellationToken ct) =>
        {
            if (request.ClientId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "clientId and name are required" });
            }

            var clientExists = await db.Clients.AnyAsync(x => x.Id == request.ClientId, ct);
            if (!clientExists)
            {
                return Results.BadRequest(new { error = "client not found" });
            }

            var normalizedName = request.Name.Trim().ToLowerInvariant();
            var domainExists = await db.Domains.AnyAsync(x => x.Name == normalizedName, ct);
            if (domainExists)
            {
                return Results.Conflict(new { error = "domain already exists" });
            }

            var now = DateTime.UtcNow;
            var domain = new Domain
            {
                ClientId = request.ClientId,
                Name = normalizedName,
                IsActive = request.IsActive,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            db.Domains.Add(domain);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/domains/{domain.Id}", domain);
        });

        app.MapPatch("/api/v1/domains/{id:guid}", async (Guid id, UpdateDomainRequest request, DmarcAnalyzerDbContext db, CancellationToken ct) =>
        {
            var domain = await db.Domains.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (domain is null)
            {
                return Results.NotFound();
            }

            if (request.ClientId.HasValue)
            {
                if (request.ClientId.Value == Guid.Empty)
                {
                    return Results.BadRequest(new { error = "clientId cannot be empty" });
                }

                var clientExists = await db.Clients.AnyAsync(x => x.Id == request.ClientId.Value, ct);
                if (!clientExists)
                {
                    return Results.BadRequest(new { error = "client not found" });
                }

                domain.ClientId = request.ClientId.Value;
            }

            if (request.Name is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest(new { error = "name cannot be empty" });
                }

                var normalizedName = request.Name.Trim().ToLowerInvariant();
                var domainExists = await db.Domains.AnyAsync(x => x.Id != id && x.Name == normalizedName, ct);
                if (domainExists)
                {
                    return Results.Conflict(new { error = "domain already exists" });
                }

                domain.Name = normalizedName;
            }

            if (request.IsActive.HasValue)
            {
                domain.IsActive = request.IsActive.Value;
            }

            domain.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                domain.Id,
                domain.ClientId,
                domain.Name,
                domain.IsActive,
                domain.CreatedAtUtc,
                domain.UpdatedAtUtc,
            });
        });
    }
}
