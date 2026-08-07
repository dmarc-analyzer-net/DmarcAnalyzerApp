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

    /// <summary>
    /// Let a login link to an existing local account when the provider says
    /// <em>nothing</em> about the address — no <c>email_verified</c>, no
    /// <c>xms_edov</c>. Microsoft Entra ID is the case this exists for: it never
    /// issues <c>email_verified</c>, so silence is its only answer and every
    /// Entra user is otherwise refused forever (#140).
    /// <para>
    /// This does not override a provider that answers. An explicit
    /// <c>email_verified=false</c> (or <c>xms_edov=false</c>) is still refused
    /// with this on — only silence becomes trustable.
    /// </para>
    /// <para>
    /// Off by default, and it should stay off unless the provider's addresses are
    /// administered by someone you trust. Against a multi-tenant authority
    /// (<c>/common</c>, <c>/organizations</c>) any tenant can assert any address,
    /// which is an account takeover of every local user by email. Prefer the
    /// <c>xms_edov</c> optional claim, which is a real assertion and needs no flag
    /// — see <c>docs/ops/oidc-entra.md</c>.
    /// </para>
    /// </summary>
    public bool TrustUnverifiedEmail { get; set; }

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
