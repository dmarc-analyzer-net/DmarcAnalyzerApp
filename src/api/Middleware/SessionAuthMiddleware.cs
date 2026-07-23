using DmarcAnalyzer.Api.Application.Auth;

namespace DmarcAnalyzer.Api.Middleware;

public sealed class SessionAuthMiddleware(RequestDelegate next)
{
    private const string CookieName = "dmarc_session";

    private static readonly HashSet<string> PublicPaths =
    [
        "/api/v1/auth/login",
        "/api/v1/auth/register",
        "/api/v1/auth/logout",
        "/api/v1/auth/setup",
        "/health/live",
        "/health/ready",
    ];

    public async Task InvokeAsync(HttpContext context, IAuthService authService, CurrentUserContext currentUserContext)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;

        if (!path.StartsWith("/api/v1/") || PublicPaths.Contains(path))
        {
            await next(context);
            return;
        }

        var cookieId = context.Request.Cookies[CookieName];
        if (cookieId is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "not authenticated" });
            return;
        }

        var sessionUser = await authService.GetSessionUserAsync(cookieId, context.RequestAborted);
        if (sessionUser is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "session expired or invalid" });
            return;
        }

        currentUserContext.Set(sessionUser.User, sessionUser.GrantedClientIds);
        await next(context);
    }
}
