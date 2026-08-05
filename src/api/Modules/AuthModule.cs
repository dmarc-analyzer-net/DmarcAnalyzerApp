using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Contracts.Auth;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Modules;

public sealed class AuthModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        // Deliberately not gated by Auth:Oidc:DisableLocalLogin: RegisterAsync already
        // refuses itself once the first account exists, so leaving this open is how the
        // very first admin gets in on a deployment that turns local login off — there
        // would otherwise be no way to reach that setting without already being signed in.
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

        app.MapPost("/api/v1/auth/login", async (LoginRequest request, IAuthService service, IAuditLog audit, HttpContext http, IOptions<OidcOptions> oidc, CancellationToken ct) =>
        {
            if (oidc.Value.DisableLocalLogin)
            {
                return Results.Json(new { error = "password sign-in is disabled; use single sign-on" }, statusCode: 403);
            }

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
            http.Response.Cookies.Append(SessionCookie.Name, login.CookieId, SessionCookie.Options(http.Request));

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
