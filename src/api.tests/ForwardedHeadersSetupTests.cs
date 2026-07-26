using DmarcAnalyzer.Api.Application.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Behind a proxy the audit trail records the proxy, not the caller — on the
/// default Compose stack that is Docker's bridge gateway for every entry.
/// Believing X-Forwarded-For fixes that, but only from a trusted hop: anything
/// else lets a caller write whatever address it likes into its own audit record,
/// which is worse than recording the gateway honestly.
/// </summary>
public sealed class ForwardedHeadersSetupTests
{
    private static (bool Enabled, ForwardedHeadersOptions Options) Configure(NetworkOptions network)
    {
        var options = new ForwardedHeadersOptions();
        var enabled = ForwardedHeadersSetup.TryConfigure(network, options, NullLogger.Instance);
        return (enabled, options);
    }

    [Fact]
    public void OffByDefault()
    {
        var (enabled, _) = Configure(new NetworkOptions());
        Assert.False(enabled);
    }

    [Fact]
    public void EnabledWithNoTrustListIsRefused()
    {
        // The dangerous configuration: on, but trusting everyone. Must not apply.
        var (enabled, options) = Configure(new NetworkOptions { UseForwardedHeaders = true });

        Assert.False(enabled);
        Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders);
    }

    [Fact]
    public void ATrustedProxyIsApplied()
    {
        var (enabled, options) = Configure(new NetworkOptions
        {
            UseForwardedHeaders = true,
            TrustedProxies = ["10.0.1.5"],
        });

        Assert.True(enabled);
        Assert.Contains(options.KnownProxies, p => p.ToString() == "10.0.1.5");
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto));
    }

    [Fact]
    public void ATrustedNetworkIsApplied()
    {
        var (enabled, options) = Configure(new NetworkOptions
        {
            UseForwardedHeaders = true,
            TrustedNetworks = ["172.16.0.0/12"],
        });

        Assert.True(enabled);
        Assert.Single(options.KnownIPNetworks);
    }

    [Fact]
    public void TheFrameworkDefaultsAreReplaced_NotAddedTo()
    {
        // Defaults trust loopback; an operator naming their proxies is stating the
        // whole trust set, so stale defaults must not survive alongside it.
        var options = new ForwardedHeadersOptions();
        options.KnownProxies.Add(System.Net.IPAddress.Parse("203.0.113.9"));

        ForwardedHeadersSetup.TryConfigure(
            new NetworkOptions { UseForwardedHeaders = true, TrustedProxies = ["10.0.1.5"] },
            options, NullLogger.Instance);

        Assert.Equal("10.0.1.5", Assert.Single(options.KnownProxies).ToString());
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/notanumber")]
    [InlineData("")]
    public void GarbageEntriesAreDroppedRatherThanTrusted(string bad)
    {
        var (enabled, options) = Configure(new NetworkOptions
        {
            UseForwardedHeaders = true,
            TrustedProxies = [bad],
            TrustedNetworks = [bad],
        });

        // Nothing parseable was supplied, so nothing is trusted and the middleware
        // stays off rather than running with an empty allow-list.
        Assert.False(enabled);
        Assert.Empty(options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);
    }

    [Fact]
    public void ForwardLimitIsAtLeastOne()
    {
        var (_, options) = Configure(new NetworkOptions
        {
            UseForwardedHeaders = true, TrustedProxies = ["10.0.1.5"], ForwardLimit = 0,
        });

        Assert.Equal(1, options.ForwardLimit);
    }
}
