namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>The app session cookie, shared by password login and OIDC completion.</summary>
public static class SessionCookie
{
    public const string Name = "dmarc_session";

    public static CookieOptions Options() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(7),
        Path = "/",
    };
}
