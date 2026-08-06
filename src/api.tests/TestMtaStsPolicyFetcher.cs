using DmarcAnalyzer.Api.Application.MtaSts;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Stubs the HTTPS policy fetch. Serves raw bodies rather than parsed policies
/// so the tests exercise the same parser production uses.
/// </summary>
public sealed class TestMtaStsPolicyFetcher : IMtaStsPolicyFetcher
{
    private readonly Dictionary<string, MtaStsPolicyFetchResult> _byDomain = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Domains fetched, in order — lets tests assert the fetch was skipped.</summary>
    public List<string> Fetched { get; } = [];

    public TestMtaStsPolicyFetcher Serve(string domain, string body, string contentType = "text/plain")
    {
        _byDomain[domain] = new MtaStsPolicyFetchResult(MtaStsFetchStatus.Ok, body, 200, null, contentType);
        return this;
    }

    public TestMtaStsPolicyFetcher Fail(string domain, string status, string? detail = null)
    {
        _byDomain[domain] = new MtaStsPolicyFetchResult(status, null, null, detail, null);
        return this;
    }

    public Task<MtaStsPolicyFetchResult> FetchAsync(string domain, CancellationToken ct)
    {
        Fetched.Add(domain);
        return Task.FromResult(_byDomain.TryGetValue(domain, out var result)
            ? result
            : new MtaStsPolicyFetchResult(MtaStsFetchStatus.ConnectFailed, null, null, "no stub configured", null));
    }
}
