using DmarcAnalyzer.Api.Application.Analytics;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Stubs live MX lookups. Same contract as the production resolver: null means
/// the lookup failed, an empty list means NXDOMAIN or no MX records.
/// </summary>
public sealed class TestDnsMxResolver : IDnsMxResolver
{
    private readonly Dictionary<string, IReadOnlyList<MxHost>?> _byDomain = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Domains queried, in order — lets tests assert a lookup never happened.</summary>
    public List<string> Queried { get; } = [];

    public TestDnsMxResolver Publish(string domain, params MxHost[] hosts)
    {
        _byDomain[domain] = hosts;
        return this;
    }

    /// <summary>Simulates a timeout/servfail — the check reports "couldn't cross-check".</summary>
    public TestDnsMxResolver FailFor(string domain)
    {
        _byDomain[domain] = null;
        return this;
    }

    public Task<IReadOnlyList<MxHost>?> ResolveAsync(string domain, CancellationToken ct)
    {
        Queried.Add(domain);
        return Task.FromResult(_byDomain.TryGetValue(domain, out var hosts) ? hosts : Array.Empty<MxHost>());
    }
}
