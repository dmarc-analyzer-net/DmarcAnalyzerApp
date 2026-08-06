using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.MtaSts;

public interface IMtaStsPolicyHostService
{
    /// <summary>
    /// The policy body to serve for a request Host header, or null (→ 404) when
    /// the host is not <c>mta-sts.&lt;known domain&gt;</c>, the domain is
    /// inactive, or no enabled policy exists.
    /// </summary>
    Task<string?> GetPolicyBodyForHostAsync(string? host, CancellationToken ct);

    /// <summary>
    /// Whether a hostname is one this instance serves a policy for — the answer
    /// to Caddy's on_demand_tls <c>ask</c>, gating certificate issuance so a
    /// stranger pointing DNS here cannot mint certificates.
    /// </summary>
    Task<bool> IsKnownPolicyHostAsync(string? host, CancellationToken ct);
}

/// <summary>
/// Maps a request's Host header to a hosted policy. Anonymous internet traffic
/// lands here, so the shape is: one pure string mapping, one indexed query, and
/// a short positive-only cache — negative results are keyed by attacker-chosen
/// Host values and deliberately never cached, while a miss costs one indexed
/// SELECT.
/// </summary>
public sealed class MtaStsPolicyHostService(
    DmarcAnalyzerDbContext db,
    IMemoryCache cache,
    IOptions<MtaStsOptions> options) : IMtaStsPolicyHostService
{
    /// <summary>Shared with the admin service, whose saves evict it.</summary>
    public static string CacheKey(string domainName) => $"mta-sts:policy:{domainName}";

    public async Task<string?> GetPolicyBodyForHostAsync(string? host, CancellationToken ct)
    {
        var domainName = TryMapHostToDomain(host);
        if (domainName is null)
        {
            return null;
        }

        var key = CacheKey(domainName);
        if (cache.TryGetValue<string>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var policy = await db.MtaStsPolicies
            .AsNoTracking()
            .Where(p => p.Enabled && p.Domain!.IsActive && p.Domain.Name == domainName)
            .Select(p => new { p.Mode, p.MaxAgeSeconds, p.MxPatterns })
            .SingleOrDefaultAsync(ct);

        if (policy is null)
        {
            return null;
        }

        var body = MtaStsCheckService.RenderPolicyFile(
            policy.Mode, policy.MaxAgeSeconds, SplitPatterns(policy.MxPatterns));

        var ttl = TimeSpan.FromSeconds(Math.Clamp(options.Value.ServeCacheSeconds, 1, 3600));
        cache.Set(key, body, ttl);
        return body;
    }

    public async Task<bool> IsKnownPolicyHostAsync(string? host, CancellationToken ct)
        => await GetPolicyBodyForHostAsync(host, ct) is not null;

    /// <summary>
    /// <c>mta-sts.example.com</c> → <c>example.com</c>, or null for anything
    /// else. Lowercases, strips one trailing dot and a defensive :port suffix
    /// (Request.Host.Host already excludes it; raw proxies may not), refuses
    /// IPv6 literals, and requires the exact <c>mta-sts.</c> prefix — the label
    /// RFC 8461 fixes, not a substring anywhere in the name.
    /// </summary>
    public static string? TryMapHostToDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var value = host.Trim();
        if (value.Contains('[') || value.Contains('/'))
        {
            return null;
        }

        var colon = value.IndexOf(':');
        if (colon >= 0)
        {
            value = value[..colon];
        }

        value = value.TrimEnd('.').ToLowerInvariant();

        const string prefix = "mta-sts.";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var domain = value[prefix.Length..];
        return domain.Length == 0 || domain.StartsWith('.') ? null : domain;
    }

    /// <summary>The storage format is newline-joined; empty string means no patterns.</summary>
    public static IReadOnlyList<string> SplitPatterns(string joined)
        => joined.Length == 0
            ? []
            : joined.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
