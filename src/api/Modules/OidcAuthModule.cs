using Carter;
using DmarcAnalyzer.Api.Application.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Modules;

public sealed class OidcAuthModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/auth/providers", (IOptions<OidcOptions> oidc) =>
        {
            var options = oidc.Value;
            return Results.Ok(new
            {
                local = true,
                oidc = options.Enabled
                    ? new { enabled = true, displayName = options.DisplayName, loginUrl = "/api/v1/auth/oidc/login" }
                    : null,
            });
        });

        app.MapGet("/api/v1/auth/oidc/login", (string? returnUrl, IOptions<OidcOptions> oidc) =>
        {
            if (!oidc.Value.Enabled)
            {
                return Results.NotFound();
            }

            var target = SafeReturnUrl(returnUrl);
            var completeUrl = $"/api/v1/auth/oidc/complete?returnUrl={Uri.EscapeDataString(target)}";
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = completeUrl },
                [OidcAuthenticationExtensions.OidcScheme]);
        });

        app.MapGet("/api/v1/auth/oidc/complete", async (
            string? returnUrl,
            HttpContext http,
            IOidcSignInService signInService,
            IOptions<OidcOptions> oidc,
            CancellationToken ct) =>
        {
            if (!oidc.Value.Enabled)
            {
                return Results.NotFound();
            }

            var auth = await http.AuthenticateAsync(OidcAuthenticationExtensions.TempScheme);
            if (!auth.Succeeded || auth.Principal is null)
            {
                return Results.Redirect("/?loginError=oidc_failed");
            }

            var ipAddress = http.Connection.RemoteIpAddress?.ToString();
            var userAgent = http.Request.Headers.UserAgent.ToString();

            var result = await signInService.SignInAsync(auth.Principal, ipAddress, userAgent, ct);

            // The temp cookie is single-use; drop it regardless of outcome.
            await http.SignOutAsync(OidcAuthenticationExtensions.TempScheme);

            if (!result.IsSuccess)
            {
                return Results.Redirect($"/?loginError={result.ErrorCode}");
            }

            http.Response.Cookies.Append(SessionCookie.Name, result.CookieId!, SessionCookie.Options());
            return Results.Redirect(SafeReturnUrl(returnUrl));
        });
    }

    /// <summary>Only local absolute paths survive; anything else falls back to "/".</summary>
    private static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        var candidate = returnUrl.Trim();
        if (!candidate.StartsWith('/') || candidate.StartsWith("//") || candidate.Contains('\\'))
        {
            return "/";
        }

        return candidate;
    }
}
