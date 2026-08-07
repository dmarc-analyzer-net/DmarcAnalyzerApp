using System.Net;
using DnsClient;
using DnsClient.Protocol;

namespace DmarcAnalyzer.Api.Application.Analytics;

public interface IAuthoritativeDnsClientLocator
{
    /// <summary>
    /// A non-recursive client pointed directly at one of <paramref name="name"/>'s
    /// authoritative nameservers — bypassing every caching resolver in between
    /// (ours, the host's, anything upstream), the same trick as <c>dig
    /// @ns1.example.com</c>. Null when no authoritative server could be found
    /// or reached, so the caller should fall back to its normal resolver path;
    /// that fallback is the point — a self-hosted install with egress locked
    /// to its configured resolver must keep working exactly as before.
    /// </summary>
    Task<LookupClient?> LocateAsync(string name, CancellationToken ct);
}

/// <summary>
/// Finds and connects to the authoritative server for a name: walk up from the
/// query name asking each ancestor for its NS records until one answers, then
/// resolve one of those nameservers to an address and hand back a client
/// pointed straight at it with recursion turned off.
/// <para>
/// The two discovery steps (NS, then A/AAAA for the NS host) go through the
/// normal, cached, recursive resolver — NS records for a domain change on a
/// timescale of years, not the seconds-old edit this exists to see past, so
/// there is nothing to gain and a full round of latency to lose by bypassing
/// cache for them too. Only the actual answer — the record whose freshness
/// matters — comes from the direct, non-recursive query.
/// </para>
/// </summary>
public sealed class AuthoritativeDnsClientLocator(ILogger<AuthoritativeDnsClientLocator> logger)
    : IAuthoritativeDnsClientLocator
{
    /// <summary>Stops before trying a bare TLD, which would return the registry's own servers, not the domain's.</summary>
    public const int MaxAncestors = 4;

    private static readonly LookupClient DiscoveryClient = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(2),
        Retries = 0,
        UseCache = false,
    });

    public async Task<LookupClient?> LocateAsync(string name, CancellationToken ct)
    {
        IReadOnlyList<NsRecord>? ns = null;
        foreach (var candidate in SearchNames(name))
        {
            ct.ThrowIfCancellationRequested();
            ns = await TryGetNsAsync(candidate, ct);
            if (ns is { Count: > 0 })
            {
                break;
            }
        }

        if (ns is null or { Count: 0 })
        {
            return null;
        }

        // Up to two candidates for resilience — one dead nameserver should not
        // sink the whole attempt when the domain publishes several.
        foreach (var host in ns.Take(2))
        {
            ct.ThrowIfCancellationRequested();
            var address = await TryResolveAddressAsync(host.NSDName.Value, ct);
            if (address is null)
            {
                continue;
            }

            return new LookupClient(new LookupClientOptions(new NameServer(address, 53))
            {
                Timeout = TimeSpan.FromSeconds(3),
                Retries = 0,
                UseCache = false,
                Recursion = false, // the whole point: an authoritative answer, not whatever it has cached
            });
        }

        return null;
    }

    /// <summary>
    /// The query name, then each parent label group, stopping short of a bare
    /// single-label TLD. Public static and pure so the walk order is directly
    /// testable without DNS — <c>_mta-sts.mail.example.co.uk</c> tries itself,
    /// then <c>mail.example.co.uk</c>, <c>example.co.uk</c>, <c>co.uk</c>
    /// (capped at <see cref="MaxAncestors"/>), correctly finding the real zone
    /// cut without needing a public-suffix list: NS records only exist at an
    /// actual zone boundary, so the first non-empty answer is definitionally
    /// the right one, however many labels a `co.uk`-shaped suffix costs.
    /// </summary>
    public static IReadOnlyList<string> SearchNames(string name)
    {
        var trimmed = name.Trim().TrimEnd('.');
        var labels = trimmed.Split('.');
        var names = new List<string> { trimmed };

        for (var take = labels.Length - 1; take >= 2 && names.Count < MaxAncestors; take--)
        {
            names.Add(string.Join('.', labels.Skip(labels.Length - take)));
        }

        return names;
    }

    private async Task<IReadOnlyList<NsRecord>?> TryGetNsAsync(string name, CancellationToken ct)
    {
        try
        {
            var response = await DiscoveryClient.QueryAsync(name, QueryType.NS, cancellationToken: ct);
            return response.HasError ? null : response.Answers.NsRecords().ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "NS lookup failed for {Name} while locating an authoritative server", name);
            return null;
        }
    }

    private async Task<IPAddress?> TryResolveAddressAsync(string host, CancellationToken ct)
    {
        try
        {
            var response = await DiscoveryClient.QueryAsync(host, QueryType.A, cancellationToken: ct);
            var address = response.Answers.ARecords().FirstOrDefault()?.Address;
            if (address is not null)
            {
                return address;
            }

            var v6 = await DiscoveryClient.QueryAsync(host, QueryType.AAAA, cancellationToken: ct);
            return v6.Answers.AaaaRecords().FirstOrDefault()?.Address;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve an address for nameserver {Host}", host);
            return null;
        }
    }
}
