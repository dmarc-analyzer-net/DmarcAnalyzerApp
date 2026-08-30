using DmarcAnalyzer.Api.Application.Auth;

namespace DmarcAnalyzer.Api.Middleware;

/// <summary>
/// The cookie front door for /api/v1/*: resolves the dmarc_session cookie to a
/// user, populates <see cref="CurrentUserContext"/>, and 401s everything else.
/// Paths outside /api/v1/ (health, MTA-STS, the SPA) pass through untouched,
/// as do the listed public auth endpoints and machine-authenticated requests.
/// </summary>
public sealed class SessionAuthMiddleware(RequestDelegate next)
{
    private const string CookieName = "dmarc_session";

    private static readonly HashSet<string> PublicPaths =
    [
        "/api/v1/auth/login",
        "/api/v1/auth/register",
        "/api/v1/auth/logout",
        "/api/v1/auth/setup",
        "/api/v1/auth/providers",
        "/health/live",
        "/health/ready",
    ];

    // OIDC challenge/callback/completion endpoints authenticate via the
    // external-temp scheme, not an app session.
    private const string OidcPathPrefix = "/api/v1/auth/oidc/";

    /// <summary>Runs the gate for one request.</summary>
    public async Task InvokeAsync(
        HttpContext context,
        IAuthService authService,
        CurrentUserContext currentUserContext,
        IMachineCallerContext machineCaller)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;

        // A machine caller already authenticated upstream and has no cookie by design.
        // Passing it through here is not a second way in: RoleAuthorizationMiddleware still
        // refuses it on every endpoint that has not asked for a credential of its kind.
        if (machineCaller.IsAuthenticated)
        {
            await next(context);
            return;
        }

        if (!path.StartsWith("/api/v1/") || PublicPaths.Contains(path) || path.StartsWith(OidcPathPrefix))
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
