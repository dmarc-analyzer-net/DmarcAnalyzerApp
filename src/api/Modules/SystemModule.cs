using Carter;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

public sealed class SystemModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/system/status", () =>
        {
            return Results.Ok(new
            {
                service = "dmarc-analyzer-api",
                mode = "api",
                timestampUtc = DateTime.UtcNow,
            });
        });
    }
}
