using Carter;
using DmarcAnalyzer.Api.Application.Domains;
using DmarcAnalyzer.Api.Contracts.Domains;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

public sealed class DomainsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/domains/{id:guid}", async (Guid id, IDomainService service, CancellationToken ct) =>
        {
            var domain = await service.GetAsync(id, ct);

            return domain is null ? Results.NotFound() : Results.Ok(domain);
        });

        app.MapGet("/api/v1/domains", async (Guid? clientId, IDomainService service, CancellationToken ct) =>
        {
            var domains = await service.ListAsync(clientId, ct);

            return Results.Ok(domains);
        });

        app.MapPost("/api/v1/domains", async (CreateDomainRequest request, IDomainService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var domain = result.Value!;

            return Results.Created($"/api/v1/domains/{domain.Id}", domain);
        });

        app.MapPatch("/api/v1/domains/{id:guid}", async (Guid id, UpdateDomainRequest request, IDomainService service, CancellationToken ct) =>
        {
            var result = await service.UpdateAsync(id, request, ct);
            if (!result.IsSuccess)
            {
                if (result.StatusCode == 404)
                {
                    return Results.NotFound();
                }

                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            return Results.Ok(result.Value);
        });
    }
}
