using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
// Both namespaces define IPNetwork; the HttpOverrides one is obsolete.
using IPNetwork = System.Net.IPNetwork;

namespace DmarcAnalyzer.Api.Application.Security;

/// <summary>
/// Whether to believe <c>X-Forwarded-For</c> (<c>Network:*</c>).
/// <para>
/// Behind a reverse proxy the connection's remote address is the proxy, so the
/// audit trail records the proxy — in the default Compose stack that means
/// Docker's bridge gateway for every entry. The forwarded headers carry the real
/// client, but only a trusted hop may set them: anything else lets a caller put
/// whatever address it likes into an audit record, which is worse than recording
/// the gateway honestly.
/// </para>
/// <para>
/// Off by default, so a deployment that has not thought about its proxy keeps
/// the current, truthful-if-unhelpful behaviour.
/// </para>
/// </summary>
public sealed class NetworkOptions
{
    /// <summary>Trust <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c> from the proxies below.</summary>
    public bool UseForwardedHeaders { get; set; }

    /// <summary>
    /// Proxy addresses whose forwarded headers are believed, e.g.
    /// <c>["10.0.1.5"]</c>. Loopback is always trusted by the framework.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>
    /// Proxy networks in CIDR form, e.g. <c>["172.16.0.0/12"]</c>. Use this for a
    /// Docker or Kubernetes network where the proxy's address is not fixed.
    /// </summary>
    public string[] TrustedNetworks { get; set; } = [];

    /// <summary>
    /// How many proxy hops to walk back through. Defaults to 1 — raise it only if
    /// you genuinely run chained proxies, since each extra hop is another address
    /// you are choosing to believe.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;
}

public static class ForwardedHeadersSetup
{
    /// <summary>
    /// Applies <see cref="NetworkOptions"/> to the framework's forwarded-headers
    /// middleware. Returns false when the feature is off or misconfigured, so the
    /// caller can skip registering the middleware entirely.
    /// </summary>
    public static bool TryConfigure(NetworkOptions network, ForwardedHeadersOptions options, ILogger logger)
    {
        if (!network.UseForwardedHeaders)
        {
            return false;
        }

        if (network.TrustedProxies.Length == 0 && network.TrustedNetworks.Length == 0)
        {
            // Without a trust list the middleware would accept forwarded headers
            // from anyone, letting any caller forge the address recorded against
            // their own audit entries. Refuse rather than silently do that.
            logger.LogError(
                "Network:UseForwardedHeaders is on but neither TrustedProxies nor TrustedNetworks is set. " +
                "Forwarded headers are being ignored — an empty trust list would let any caller spoof its own address.");
            return false;
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = Math.Max(1, network.ForwardLimit);

        // The defaults trust loopback only; adding to these lists is the whole point.
        // KnownIPNetworks, not the deprecated KnownNetworks — the framework marks
        // the latter and its Microsoft.AspNetCore.HttpOverrides.IPNetwork obsolete
        // in favour of System.Net.IPNetwork.
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var proxy in network.TrustedProxies)
        {
            if (IPAddress.TryParse(proxy.Trim(), out var address))
            {
                options.KnownProxies.Add(address);
            }
            else
            {
                logger.LogWarning("Ignoring unparseable Network:TrustedProxies entry {Value}", proxy);
            }
        }

        foreach (var cidr in network.TrustedNetworks)
        {
            if (IPNetwork.TryParse(cidr.Trim(), out var parsed))
            {
                options.KnownIPNetworks.Add(parsed);
            }
            else
            {
                logger.LogWarning("Ignoring unparseable Network:TrustedNetworks entry {Value}", cidr);
            }
        }

        return options.KnownProxies.Count > 0 || options.KnownIPNetworks.Count > 0;
    }
}
