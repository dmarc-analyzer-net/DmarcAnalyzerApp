namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>Configuration for the optional OIDC login front door ("Auth:Oidc" section).</summary>
public sealed class OidcOptions
{
    public const string SectionName = "Auth:Oidc";

    public bool Enabled { get; set; }
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];
    public string DisplayName { get; set; } = "SSO";

    /// <summary>Role given to auto-provisioned users. Least privilege by default.</summary>
    public string DefaultRole { get; set; } = Roles.ClientViewer;

    /// <summary>Create a local user on first login when no identity or verified-email match exists.</summary>
    public bool AutoProvision { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Turns off password sign-in (<c>/api/v1/auth/login</c>) and has the login
    /// page redirect straight to this provider. Registration is untouched — it
    /// already refuses itself once the first account exists, so the very first
    /// admin can still bootstrap locally before this is turned on. Requires
    /// <see cref="Enabled"/>; otherwise there would be no way to sign in at all.
    /// </summary>
    public bool DisableLocalLogin { get; set; }
}
