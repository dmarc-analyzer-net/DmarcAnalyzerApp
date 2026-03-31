using Carter;
using DmarcAnalyzer.Api.Application.Clients;
using DmarcAnalyzer.Api.Contracts.Clients;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

public sealed class ClientsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/clients/{id:guid}", async (Guid id, IClientService service, CancellationToken ct) =>
        {
            var client = await service.GetAsync(id, ct);

            return client is null ? Results.NotFound() : Results.Ok(client);
        });

        app.MapGet("/api/v1/clients", async (IClientService service, CancellationToken ct) =>
        {
            var clients = await service.ListAsync(ct);

            return Results.Ok(clients);
        });

        app.MapPost("/api/v1/clients", async (CreateClientRequest request, IClientService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var client = result.Value!;
            return Results.Created($"/api/v1/clients/{client.Id}", client);
        });

        app.MapPatch("/api/v1/clients/{id:guid}", async (Guid id, UpdateClientRequest request, IClientService service, CancellationToken ct) =>
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
