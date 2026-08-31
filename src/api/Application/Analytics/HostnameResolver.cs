using System.Net;
using Microsoft.Extensions.Caching.Memory;

namespace DmarcAnalyzer.Api.Application.Analytics;

/// <summary>Reverse-DNS (PTR) lookups for source IPs — see <see cref="HostnameResolver"/>.</summary>
public interface IHostnameResolver
{
    /// <summary>Reverse-DNS lookups for a set of IPs. Unresolvable IPs map to null.</summary>
    Task<IReadOnlyDictionary<string, string?>> ResolveAsync(IReadOnlyCollection<string> ips, CancellationToken ct);
}

/// <summary>
/// Batched, concurrency-capped reverse lookups with a long success cache and a
/// shorter failure cache — most source IPs repeat across pages, and PTR records
/// rarely change. Best-effort by design: an unresolvable IP is a null hostname,
/// never an error the page has to handle.
/// </summary>
public sealed class HostnameResolver(IMemoryCache cache, ILogger<HostnameResolver> logger) : IHostnameResolver
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim Concurrency = new(8);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string?>> ResolveAsync(IReadOnlyCollection<string> ips, CancellationToken ct)
    {
        // The address as the caller spelled it, mapped to its canonical form. Report
        // records store the source IP exactly as the reporter wrote it and nothing
        // normalises it on the way in, so an IPv6 address can arrive uppercase or
        // uncompressed while IPAddress.ToString() only ever answers in lowercase
        // compressed form. Answering under the canonical spelling handed the caller a
        // key it never asked for and could not match against its own rows, which is
        // why IPv6 sources stayed hostname-less however often they were requested.
        // The cache still keys on the canonical form, so two spellings of one address
        // share a single lookup.
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in ips)
        {
            var asked = raw.Trim();
            if (canonical.ContainsKey(asked) || !IPAddress.TryParse(asked, out var parsed))
            {
                continue;
            }

            canonical[asked] = parsed.ToString();
        }

        var known = new Dictionary<string, string?>(StringComparer.Ordinal);
        var pending = new List<string>();

        foreach (var address in canonical.Values.Distinct(StringComparer.Ordinal))
        {
            if (cache.TryGetValue<string?>(CacheKey(address), out var cached))
            {
                known[address] = cached;
            }
            else
            {
                pending.Add(address);
            }
        }

        var lookups = pending.Select(async ip =>
        {
            await Concurrency.WaitAsync(ct);
            try
            {
                var hostname = await LookupAsync(ip, ct);
                cache.Set(CacheKey(ip), hostname, hostname is null ? FailureTtl : SuccessTtl);
                return (ip, hostname);
            }
            finally
            {
                Concurrency.Release();
            }
        });

        foreach (var (ip, hostname) in await Task.WhenAll(lookups))
        {
            known[ip] = hostname;
        }

        return canonical.ToDictionary(
            entry => entry.Key,
            entry => known.GetValueOrDefault(entry.Value),
            StringComparer.Ordinal);
    }

    private async Task<string?> LookupAsync(string ip, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(LookupTimeout);

        try
        {
            var entry = await Dns.GetHostEntryAsync(ip, timeoutCts.Token);
            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug("PTR lookup failed for {Ip}: {Reason}", ip, ex.Message);
            return null;
        }
    }

    private static string CacheKey(string ip) => $"ptr:{ip}";
}
