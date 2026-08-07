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
    /// <param name="bypassCache">
    /// Skip the cached answer and query DNS now. Cached answers include a
    /// definitive "nothing published" result, so a normal cache-first lookup
    /// keeps saying "missing" for up to the TTL after a record actually goes
    /// live — set this for an operator-triggered recheck, where a stale
    /// answer defeats the point of clicking the button. Leave it false for the
    /// worker's scheduled pass, which relies on the cache to avoid hammering
    /// DNS every interval across every domain.
    /// </param>
    Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct, bool bypassCache = false);
}

/// <summary>
/// TXT lookups against the host's configured resolver (not a hardcoded public
/// DoH endpoint — a self-hosted deployment shouldn't leak its clients' domains
/// to a third party). Cached briefly so a page refresh doesn't re-query DNS.
/// </summary>
public sealed class DnsTxtResolver(
    IMemoryCache cache,
    ILogger<DnsTxtResolver> logger,
    IAuthoritativeDnsClientLocator authoritativeLocator) : IDnsTxtResolver
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(5);
    private static readonly LookupClient Client = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(3),
        Retries = 1,
        UseCache = false, // IMemoryCache above is the cache; keep layers single-purpose
    });

    public async Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct, bool bypassCache = false)
    {
        var key = $"dns-txt:{name.ToLowerInvariant()}";
        if (!bypassCache && cache.TryGetValue<IReadOnlyList<string>>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        // bypassCache means the operator wants to see past every cache layer,
        // not just ours — the host's own resolver may hold a stale negative
        // answer for the rest of its TTL too. Try the domain's authoritative
        // server directly first; any failure (blocked port 53, no NS found,
        // timeout) falls through to the normal resolver path below.
        if (bypassCache)
        {
            var authoritative = await TryResolveAuthoritativeAsync(name, ct);
            if (authoritative is not null)
            {
                cache.Set(key, authoritative, SuccessTtl);
                return authoritative;
            }
        }

        try
        {
            var response = await Client.QueryAsync(name, QueryType.TXT, cancellationToken: ct);

            // DnsClient does not throw on DNS-level errors (ThrowDnsErrors is
            // off). A SERVFAIL/REFUSED must read as "couldn't check", not as an
            // empty answer set — otherwise it looks identical to "no record".
            // NXDOMAIN is a definitive answer (no record) and is NOT an error.
            if (response.HasError && response.Header.ResponseCode != DnsHeaderResponseCode.NotExistentDomain)
            {
                logger.LogWarning("TXT lookup for {Name} returned {Code}", name, response.Header.ResponseCode);
                return null;
            }

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

    private async Task<IReadOnlyList<string>?> TryResolveAuthoritativeAsync(string name, CancellationToken ct)
    {
        try
        {
            var client = await authoritativeLocator.LocateAsync(name, ct);
            if (client is null)
            {
                return null;
            }

            var response = await client.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
            if (response.HasError && response.Header.ResponseCode != DnsHeaderResponseCode.NotExistentDomain)
            {
                return null;
            }

            return response.Answers.TxtRecords()
                .Select(r => string.Concat(r.Text))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Authoritative TXT lookup failed for {Name}, falling back", name);
            return null;
        }
    }
}
