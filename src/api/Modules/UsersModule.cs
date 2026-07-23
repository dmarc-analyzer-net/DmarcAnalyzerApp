using Carter;
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

        app.MapPost("/api/v1/users", async (CreateUserRequest request, IUserAdminService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var user = result.Value!;
            return Results.Created($"/api/v1/users/{user.Id}", user);
        }).RequireAgencyAdmin();

        app.MapPatch("/api/v1/users/{id:guid}", async (Guid id, UpdateUserRequest request, IUserAdminService service, CancellationToken ct) =>
        {
            var result = await service.UpdateAsync(id, request, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
        }).RequireAgencyAdmin();

        app.MapPut("/api/v1/users/{id:guid}/grants", async (Guid id, ReplaceUserGrantsRequest request, IUserAdminService service, CancellationToken ct) =>
        {
            var result = await service.ReplaceGrantsAsync(id, request, ct);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
        }).RequireAgencyAdmin();
    }
}
