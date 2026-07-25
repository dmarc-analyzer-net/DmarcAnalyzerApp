using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Users;
using DmarcAnalyzer.Api.Contracts.Users;

namespace DmarcAnalyzer.Api.Modules;

public sealed class UsersModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/users", async (IUserAdminService service, CancellationToken ct) =>
        {
            var users = await service.ListAsync(ct);
            return Results.Ok(users);
        }).RequireAgencyAdmin();

        app.MapPost("/api/v1/users", async (CreateUserRequest request, IUserAdminService service, IAuditLog audit, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var user = result.Value!;
            await audit.RecordAsync(AuditEvents.UserCreated,
                $"Created user {user.Email} with role {user.Role}", "user", user.Id, ct: ct);
            return Results.Created($"/api/v1/users/{user.Id}", user);
        }).RequireAgencyAdmin();

        app.MapPatch("/api/v1/users/{id:guid}", async (Guid id, UpdateUserRequest request, IUserAdminService service, IAuditLog audit, CancellationToken ct) =>
        {
            var result = await service.UpdateAsync(id, request, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            await audit.RecordAsync(AuditEvents.UserUpdated,
                $"Updated user {result.Value!.Email}", "user", id, ct: ct);
            return Results.Ok(result.Value);
        }).RequireAgencyAdmin();

        app.MapPut("/api/v1/users/{id:guid}/grants", async (Guid id, ReplaceUserGrantsRequest request, IUserAdminService service, IAuditLog audit, CancellationToken ct) =>
        {
            var result = await service.ReplaceGrantsAsync(id, request, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            // Which clients a viewer can see is exactly the kind of change an
            // audit trail exists for.
            await audit.RecordAsync(AuditEvents.UserGrantsChanged,
                $"Replaced client grants for user {id}", "user", id,
                details: $"{request.ClientIds?.Count ?? 0} client grant(s)", ct: ct);
            return Results.Ok(result.Value);
        }).RequireAgencyAdmin();
    }
}
