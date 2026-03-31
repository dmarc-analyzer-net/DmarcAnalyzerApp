using Carter;
using DmarcAnalyzer.Api.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Modules;

public sealed class DatabaseModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/admin/database/migrate", async (DmarcAnalyzerDbContext db, CancellationToken ct) =>
        {
            await db.Database.MigrateAsync(ct);
            return Results.Ok(new { status = "ok" });
        });
    }
}
