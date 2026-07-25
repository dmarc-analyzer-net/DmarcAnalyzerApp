using DmarcAnalyzer.Api.Application.Analytics;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Stubs live DNS for the analytics services. Returns TXT strings rather than a
/// parsed record so the tests exercise the same parser production uses.
/// </summary>
public sealed class TestDnsTxtResolver : IDnsTxtResolver
{
    private readonly Dictionary<string, IReadOnlyList<string>?> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>No DMARC record published anywhere — every lookup is NXDOMAIN.</summary>
    public static TestDnsTxtResolver Empty() => new();

    /// <summary>Publishes `v=DMARC1; p={policy}` at `_dmarc.{domain}`.</summary>
    public static TestDnsTxtResolver WithPolicy(string domain, string policy)
        => new TestDnsTxtResolver().Publish($"_dmarc.{domain}", $"v=DMARC1; p={policy}");

    public TestDnsTxtResolver Publish(string name, params string[] txts)
    {
        _byName[name] = txts;
        return this;
    }

    /// <summary>Simulates a timeout/servfail, which the parser treats as unknown.</summary>
    public TestDnsTxtResolver FailFor(string name)
    {
        _byName[name] = null;
        return this;
    }

    public Task<IReadOnlyList<string>?> ResolveAsync(string name, CancellationToken ct)
        => Task.FromResult(_byName.TryGetValue(name, out var txts) ? txts : Array.Empty<string>());
}
