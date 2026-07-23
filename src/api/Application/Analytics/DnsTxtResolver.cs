using DnsClient;
using Microsoft.Extensions.Caching.Memory;

namespace DmarcAnalyzer.Api.Application.Analytics;

public interface IDnsTxtResolver
{
    /// <summary>
    /// TXT strings published at <paramref name="name"/>, with multi-string
    /// records already joined. Returns null when the lookup itself failed
    /// (timeout/servfail) — distinct from an empty list, which means NXDOMAIN
    /// or no TXT records.
    /// </summary>
    Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct);
}

/// <summary>
/// TXT lookups against the host's configured resolver (not a hardcoded public
/// DoH endpoint — a self-hosted deployment shouldn't leak its clients' domains
/// to a third party). Cached briefly so a page refresh doesn't re-query DNS.
/// </summary>
public sealed class DnsTxtResolver(IMemoryCache cache, ILogger<DnsTxtResolver> logger) : IDnsTxtResolver
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(5);
    private static readonly LookupClient Client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(3),
        Retries = 1,
        UseCache = false, // IMemoryCache above is the cache; keep layers single-purpose
    });

    public async Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct)
    {
        var key = $"dns-txt:{name.ToLowerInvariant()}";
        if (cache.TryGetValue<IReadOnlyList<string>>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var response = await Client.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
            var records = response.Answers.TxtRecords()
                .Select(r => string.Concat(r.Text))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
            cache.Set(key, (IReadOnlyList<string>)records, SuccessTtl);
            return records;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TXT lookup failed for {Name}", name);
            return null; // lookup failure — caller reports "couldn't check", not "missing"
        }
    }
}
