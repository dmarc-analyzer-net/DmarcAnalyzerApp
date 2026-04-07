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
        "/health/live",
        "/health/ready",
    ];

    public async Task InvokeAsync(HttpContext context, IAuthService authService)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;

        if (!path.StartsWith("/api/v1/") || PublicPaths.Contains(path))
        {
            await next(context);
            return;
        }

        // Admin migrate endpoint is protected by auth too
        var cookieId = context.Request.Cookies[CookieName];
        if (cookieId is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "not authenticated" });
            return;
        }

        var user = await authService.GetCurrentUserAsync(cookieId, context.RequestAborted);
        if (user is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "session expired or invalid" });
            return;
        }

        context.Items["CurrentUser"] = user;
        await next(context);
    }
}
