using System.Net;
using Microsoft.Extensions.Caching.Memory;

namespace DmarcAnalyzer.Api.Application.Analytics;

public interface IHostnameResolver
{
    /// <summary>Reverse-DNS lookups for a set of IPs. Unresolvable IPs map to null.</summary>
    Task<IReadOnlyDictionary<string, string?>> ResolveAsync(IReadOnlyCollection<string> ips, CancellationToken ct);
}

public sealed class HostnameResolver(IMemoryCache cache, ILogger<HostnameResolver> logger) : IHostnameResolver
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim Concurrency = new(8);

    public async Task<IReadOnlyDictionary<string, string?>> ResolveAsync(IReadOnlyCollection<string> ips, CancellationToken ct)
    {
        var results = new Dictionary<string, string?>();
        var pending = new List<string>();

        foreach (var raw in ips.Distinct())
        {
            if (!IPAddress.TryParse(raw.Trim(), out var parsed))
            {
                continue;
            }

            var normalized = parsed.ToString();
            if (cache.TryGetValue<string?>(CacheKey(normalized), out var cached))
            {
                results[normalized] = cached;
            }
            else
            {
                pending.Add(normalized);
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
            results[ip] = hostname;
        }

        return results;
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
