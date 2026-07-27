namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>The app session cookie, shared by password login and OIDC completion.</summary>
public static class SessionCookie
{
    public const string Name = "dmarc_session";

    /// <summary>
    /// Cookie options for the current request.
    /// <para>
    /// <c>Secure</c> follows the request scheme rather than being hard-coded on.
    /// A browser silently discards a <c>Secure</c> cookie sent over a plain-HTTP
    /// origin that is not localhost, so an install reached at
    /// <c>http://nas.local:8189</c> would accept the password, return 200, and
    /// then behave as if the user had never signed in. That is the normal way
    /// people run this on a home server or a NAS app store, and it is also the
    /// shape Umbrel and CasaOS serve apps in.
    /// </para>
    /// <para>
    /// Behind a TLS-terminating proxy the app sees HTTP, so configure
    /// <c>Network__UseForwardedHeaders</c> — <c>X-Forwarded-Proto</c> is what
    /// makes <see cref="HttpRequest.IsHttps"/> true and restores the flag. Without
    /// it the cookie stays <c>HttpOnly</c> and <c>SameSite=Lax</c>, but loses the
    /// Secure marker; see docs/ops/configuration.md.
    /// </para>
    /// </summary>
    public static CookieOptions Options(HttpRequest request) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(7),
        Path = "/",
    };
}
