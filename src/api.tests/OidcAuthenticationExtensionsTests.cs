using DmarcAnalyzer.Api.Application.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
}
