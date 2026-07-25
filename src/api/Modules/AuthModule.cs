using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Contracts.Auth;
using Microsoft.AspNetCore.Routing;

namespace DmarcAnalyzer.Api.Modules;

public sealed class AuthModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/register", async (RegisterRequest request, IAuthService service, IAuditLog audit, CancellationToken ct) =>
        {
            var result = await service.RegisterAsync(request, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            await audit.RecordAsync(AuditEvents.UserRegistered,
                $"Registered {request.Email} during first-run bootstrap",
                "user", null, actorEmailOverride: request.Email, ct: ct);
            return Results.Created($"/api/v1/auth/me", result.Value);
        });

        app.MapGet("/api/v1/auth/setup", async (IAuthService service, CancellationToken ct) =>
        {
            var requiresBootstrap = await service.RequiresBootstrapAsync(ct);
            return Results.Ok(new { requiresBootstrap });
        });

        app.MapPost("/api/v1/auth/login", async (LoginRequest request, IAuthService service, IAuditLog audit, HttpContext http, CancellationToken ct) =>
        {
            var ipAddress = http.Connection.RemoteIpAddress?.ToString();
            var userAgent = http.Request.Headers.UserAgent.ToString();

            var result = await service.LoginAsync(request, ipAddress, userAgent, ct);
            if (!result.IsSuccess)
            {
                // Failed sign-ins are the point of an audit trail — record the
                // attempted address, never the password or the reason detail.
                await audit.RecordAsync(AuditEvents.LoginFailed,
                    $"Failed sign-in for {request.Email}",
                    actorEmailOverride: request.Email, ct: ct);
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var login = result.Value!;
            http.Response.Cookies.Append(SessionCookie.Name, login.CookieId, SessionCookie.Options());

            await audit.RecordAsync(AuditEvents.LoginSucceeded, $"Signed in as {login.User.Email}",
                "user", login.User.Id,
                actorEmailOverride: login.User.Email, actorUserIdOverride: login.User.Id, ct: ct);
            return Results.Ok(new { user = login.User });
        });

        app.MapPost("/api/v1/auth/logout", async (IAuthService service, IAuditLog audit, HttpContext http, CancellationToken ct) =>
        {
            var cookieId = http.Request.Cookies[SessionCookie.Name];
            if (cookieId is not null)
            {
                await service.LogoutAsync(cookieId, ct);
            }

            await audit.RecordAsync(AuditEvents.Logout, "Signed out", ct: ct);
            http.Response.Cookies.Delete(SessionCookie.Name);
            return Results.NoContent();
        });

        app.MapGet("/api/v1/auth/me", async (IAuthService service, HttpContext http, CancellationToken ct) =>
        {
            var cookieId = http.Request.Cookies[SessionCookie.Name];
            if (cookieId is null)
            {
                return Results.Json(new { error = "not authenticated" }, statusCode: 401);
            }

            var user = await service.GetCurrentUserAsync(cookieId, ct);
            if (user is null)
            {
                http.Response.Cookies.Delete(SessionCookie.Name);
                return Results.Json(new { error = "session expired or invalid" }, statusCode: 401);
            }

            return Results.Ok(new { user });
        }).AllowClientViewer();
    }

}
