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
    }
}
