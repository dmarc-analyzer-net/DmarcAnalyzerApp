using DnsClient;
using Microsoft.Extensions.Caching.Memory;

namespace DmarcAnalyzer.Api.Application.Analytics;

/// <summary>A single MX record: preference and the exchange host, trailing dot stripped.</summary>
public sealed record MxHost(int Preference, string Host);

public interface IDnsMxResolver
{
    /// <summary>
    /// MX records published for <paramref name="domain"/>, preference order.
    /// Returns null when the lookup itself failed (timeout/servfail) — distinct
    /// from an empty list, which means NXDOMAIN or no MX records.
    /// </summary>
    Task<IReadOnlyList<MxHost>?> ResolveAsync(string domain, CancellationToken ct);
}

/// <summary>
/// MX lookups against the host's configured resolver, mirroring
/// <see cref="DnsTxtResolver"/> — same privacy rationale (no third-party DoH),
/// same short cache so a page refresh doesn't re-query DNS.
/// </summary>
public sealed class DnsMxResolver(IMemoryCache cache, ILogger<DnsMxResolver> logger) : IDnsMxResolver
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(5);
    private static readonly LookupClient Client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(3),
        Retries = 1,
        UseCache = false, // IMemoryCache above is the cache; keep layers single-purpose
    });

    public async Task<IReadOnlyList<MxHost>?> ResolveAsync(string domain, CancellationToken ct)
    {
        var key = $"dns-mx:{domain.ToLowerInvariant()}";
        if (cache.TryGetValue<IReadOnlyList<MxHost>>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var response = await Client.QueryAsync(domain, QueryType.MX, cancellationToken: ct);

            // Same contract as the TXT resolver: SERVFAIL/REFUSED reads as
            // "couldn't check" (null), NXDOMAIN is a definitive empty answer.
            if (response.HasError && response.Header.ResponseCode != DnsHeaderResponseCode.NotExistentDomain)
            {
                logger.LogWarning("MX lookup for {Domain} returned {Code}", domain, response.Header.ResponseCode);
                return null;
            }

            var records = response.Answers.MxRecords()
                .Select(r => new MxHost(r.Preference, r.Exchange.Value.TrimEnd('.').ToLowerInvariant()))
                .OrderBy(r => r.Preference)
                .ThenBy(r => r.Host, StringComparer.Ordinal)
                .ToArray();
            cache.Set(key, (IReadOnlyList<MxHost>)records, SuccessTtl);
            return records;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MX lookup failed for {Domain}", domain);
            return null; // lookup failure — caller reports "couldn't check", not "missing"
        }
    }
}
