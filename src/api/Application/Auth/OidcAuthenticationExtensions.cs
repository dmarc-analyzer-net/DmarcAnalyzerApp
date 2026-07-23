using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>
/// Optional OIDC login front door. The Microsoft OIDC handler performs the
/// challenge/callback dance (state, nonce, PKCE, token validation) and signs
/// the result into a short-lived external-temp cookie; the /auth/oidc/complete
/// endpoint consumes that once, mints the app's own dmarc_session, and signs
/// the temp scheme out. SessionAuthMiddleware remains the only downstream
/// authentication authority.
/// </summary>
public static class OidcAuthenticationExtensions
{
    public const string TempScheme = "external-temp";
    public const string OidcScheme = "oidc";
    public const string CallbackPath = "/api/v1/auth/oidc/callback";

    public static IServiceCollection AddOidcAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OidcOptions>(configuration.GetSection(OidcOptions.SectionName));

        var options = configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();
        if (!options.Enabled)
        {
            return services;
        }

        services.AddAuthentication(TempScheme)
            .AddCookie(TempScheme, cookie =>
            {
                cookie.Cookie.Name = "dmarc_ext";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                cookie.SlidingExpiration = false;
                // API consumers should see status codes, not login redirects.
                cookie.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = ctx =>
                    {
                        ctx.Response.StatusCode = 401;
                        return Task.CompletedTask;
                    },
                };
            })
            .AddOpenIdConnect(OidcScheme, oidc =>
            {
                oidc.SignInScheme = TempScheme;
                oidc.Authority = options.Authority;
                oidc.ClientId = options.ClientId;
                if (!string.IsNullOrWhiteSpace(options.ClientSecret))
                {
                    oidc.ClientSecret = options.ClientSecret;
                }

                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                oidc.CallbackPath = CallbackPath;
                oidc.RequireHttpsMetadata = options.RequireHttpsMetadata;
                oidc.SaveTokens = false;
                oidc.GetClaimsFromUserInfoEndpoint = true;

                // Keep raw OIDC claim names (iss/sub/email/email_verified/name).
                oidc.MapInboundClaims = false;
                oidc.TokenValidationParameters.NameClaimType = "name";
                oidc.TokenValidationParameters.RoleClaimType = "role";
                oidc.ClaimActions.MapJsonKey("email", "email");
                oidc.ClaimActions.MapJsonKey("email_verified", "email_verified");
                oidc.ClaimActions.MapJsonKey("name", "name");

                oidc.Scope.Clear();
                foreach (var scope in options.Scopes)
                {
                    oidc.Scope.Add(scope);
                }

                // The callback is a top-level GET navigation, so Lax cookies are
                // sent — and unlike the None default they work on plain-http dev.
                oidc.CorrelationCookie.SameSite = SameSiteMode.Lax;
                oidc.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                oidc.NonceCookie.SameSite = SameSiteMode.Lax;
                oidc.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });

        return services;
    }
}
