using DmarcAnalyzer.Api.Application.Auth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// <c>Auth:Oidc:DisableLocalLogin</c> without <c>Auth:Oidc:Enabled</c> would leave no way to
/// sign in at all, so it is refused at startup rather than discovered as a locked-out
/// deployment.
/// </summary>
public sealed class OidcAuthenticationExtensionsTests
{
    private static IServiceCollection AddOidc(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(x => new KeyValuePair<string, string?>(x.Key, x.Value)))
            .Build();

        return new ServiceCollection().AddOidcAuthentication(configuration);
    }

    [Fact]
    public void DisableLocalLoginWithoutEnabled_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AddOidc(("Auth:Oidc:DisableLocalLogin", "true")));

        Assert.Contains("DisableLocalLogin", exception.Message);
    }

    [Fact]
    public void DisableLocalLoginWithEnabled_DoesNotThrow()
    {
        AddOidc(
            ("Auth:Oidc:Enabled", "true"),
            ("Auth:Oidc:DisableLocalLogin", "true"),
            ("Auth:Oidc:Authority", "https://idp.example.com"),
            ("Auth:Oidc:ClientId", "client"));
    }

    [Fact]
    public void NeitherSet_DoesNotThrow()
    {
        AddOidc();
    }

    private static OpenIdConnectOptions ResolveOidcOptions()
    {
        var services = AddOidc(
            ("Auth:Oidc:Enabled", "true"),
            ("Auth:Oidc:Authority", "https://idp.example.com"),
            ("Auth:Oidc:ClientId", "client"));

        // The handler's post-configuration protects the state parameter, so it
        // needs data protection and logging present to resolve at all.
        services.AddLogging();
        services.AddDataProtection();

        return services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OidcAuthenticationExtensions.OidcScheme);
    }

    /// <summary>
    /// Regression test for #114. The handler defaults <c>ResponseMode</c> to
    /// <c>form_post</c>, and a form_post callback arrives as a cross-site POST —
    /// which never carries a <c>SameSite=Lax</c> cookie. The correlation cookie
    /// written during the challenge would be absent on return and every login
    /// would die with "Correlation failed" (Entra ID did exactly this).
    /// </summary>
    [Fact]
    public void ResponseMode_IsQuery_SoTheCallbackIsATopLevelGet()
    {
        Assert.Equal("query", ResolveOidcOptions().ResponseMode);
    }

    /// <summary>
    /// The Lax cookies and the query response mode above are a pair: Lax is only
    /// safe because the callback is a GET. Asserted together so that relaxing one
    /// without the other fails here rather than in production.
    /// </summary>
    [Fact]
    public void CorrelationAndNonceCookies_AreLax()
    {
        var options = ResolveOidcOptions();

        Assert.Equal(SameSiteMode.Lax, options.CorrelationCookie.SameSite);
        Assert.Equal(SameSiteMode.Lax, options.NonceCookie.SameSite);
    }
}
