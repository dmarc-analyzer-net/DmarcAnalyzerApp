using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Modules;

public sealed class DatabaseModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/admin/database/migrate", async (DmarcAnalyzerDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            await db.Database.MigrateAsync(ct);
            await audit.RecordAsync(AuditEvents.DatabaseMigrated, "Applied pending database migrations", ct: ct);
            return Results.Ok(new { status = "ok" });
        }).RequireAgencyAdmin();
    }
}
