using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Application.MtaSts;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The composed check: TXT gate, parallel fetch + MX lookup, cross-check. The
/// stubs serve raw TXT strings and policy bodies so the production parsers run.
/// </summary>
public sealed class MtaStsCheckServiceTests
{
    private const string Domain = "acme.example";

    private static readonly string ValidPolicy =
        "version: STSv1\nmode: enforce\nmx: mx1.acme.example\nmx: *.mail.acme.example\nmax_age: 604800\n";

    private static MtaStsCheckService Service(
        TestDnsTxtResolver txt, TestDnsMxResolver mx, TestMtaStsPolicyFetcher fetcher)
        => new(txt, mx, fetcher);

    [Fact]
    public async Task MissingRecord_ShortCircuits_NoFetchNoMxLookup()
    {
        var mx = new TestDnsMxResolver();
        var fetcher = new TestMtaStsPolicyFetcher();
        var result = await Service(TestDnsTxtResolver.Empty(), mx, fetcher)
            .CheckAsync(Domain, CancellationToken.None);

        Assert.Equal(MtaStsRecordStatus.Missing, result.Record.Status);
        Assert.Null(result.Fetch);
        Assert.Null(result.MxHosts);
        // The common no-MTA-STS domain must cost exactly one DNS query.
        Assert.Empty(fetcher.Fetched);
        Assert.Empty(mx.Queried);
    }

    [Fact]
    public async Task HappyPath_AllMxCovered_NoIssues()
    {
        var txt = new TestDnsTxtResolver().Publish($"_mta-sts.{Domain}", "v=STSv1; id=a1");
        var mx = new TestDnsMxResolver().Publish(Domain,
            new MxHost(10, "mx1.acme.example"),
            new MxHost(20, "backup.mail.acme.example"));
        var fetcher = new TestMtaStsPolicyFetcher().Serve(Domain, ValidPolicy);

        var result = await Service(txt, mx, fetcher).CheckAsync(Domain, CancellationToken.None);

        Assert.Equal(MtaStsRecordStatus.Found, result.Record.Status);
        Assert.Equal("a1", result.Record.Id);
        Assert.Equal(MtaStsFetchStatus.Ok, result.Fetch!.Status);
        Assert.True(result.Policy!.Valid);
        Assert.Equal(MtaStsMxStatus.Found, result.MxLookupStatus);
        Assert.Equal([], result.UnmatchedMxHosts);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task UnmatchedMx_UnderEnforce_IsAMailBreakingFinding()
    {
        var txt = new TestDnsTxtResolver().Publish($"_mta-sts.{Domain}", "v=STSv1; id=a1");
        var mx = new TestDnsMxResolver().Publish(Domain,
            new MxHost(10, "mx1.acme.example"),
            new MxHost(20, "mx.new-provider.net")); // migrated, policy not updated
        var fetcher = new TestMtaStsPolicyFetcher().Serve(Domain, ValidPolicy);

        var result = await Service(txt, mx, fetcher).CheckAsync(Domain, CancellationToken.None);

        Assert.Equal(["mx.new-provider.net"], result.UnmatchedMxHosts);
        Assert.Contains(result.Issues, i => i.Contains("mx.new-provider.net") && i.Contains("refuse"));
    }

    [Fact]
    public async Task UnmatchedMx_UnderTesting_PointsAtTlsRpt()
    {
        var txt = new TestDnsTxtResolver().Publish($"_mta-sts.{Domain}", "v=STSv1; id=a1");
        var mx = new TestDnsMxResolver().Publish(Domain, new MxHost(10, "elsewhere.example.net"));
        var fetcher = new TestMtaStsPolicyFetcher()
            .Serve(Domain, ValidPolicy.Replace("mode: enforce", "mode: testing"));

        var result = await Service(txt, mx, fetcher).CheckAsync(Domain, CancellationToken.None);

        Assert.Contains(result.Issues, i => i.Contains("elsewhere.example.net") && i.Contains("TLS-RPT"));
    }

    [Fact]
    public async Task FetchFailure_PropagatesTheDetail_AndSkipsTheCrossCheck()
    {
        var txt = new TestDnsTxtResolver().Publish($"_mta-sts.{Domain}", "v=STSv1; id=a1");
        var mx = new TestDnsMxResolver().Publish(Domain, new MxHost(10, "mx1.acme.example"));
        var fetcher = new TestMtaStsPolicyFetcher()
            .Fail(Domain, MtaStsFetchStatus.TlsFailed, "Certificate rejected (RemoteCertificateNameMismatch)");

        var result = await Service(txt, mx, fetcher).CheckAsync(Domain, CancellationToken.None);

        Assert.Equal(MtaStsFetchStatus.TlsFailed, result.Fetch!.Status);
        Assert.Null(result.Policy);
        Assert.Null(result.UnmatchedMxHosts);
        Assert.Contains(result.Issues, i => i.Contains("RemoteCertificateNameMismatch"));
    }

    [Fact]
    public async Task MxLookupFailure_IsNotAMismatch()
    {
        var txt = new TestDnsTxtResolver().Publish($"_mta-sts.{Domain}", "v=STSv1; id=a1");
        var mx = new TestDnsMxResolver().FailFor(Domain);
        var fetcher = new TestMtaStsPolicyFetcher().Serve(Domain, ValidPolicy);

        var result = await Service(txt, mx, fetcher).CheckAsync(Domain, CancellationToken.None);

        Assert.Equal(MtaStsMxStatus.LookupFailed, result.MxLookupStatus);
        // Couldn't check is not the same as mismatched.
        Assert.Null(result.UnmatchedMxHosts);
        Assert.Contains(result.Issues, i => i.Contains("could not cross-check"));
    }

    [Fact]
    public async Task ModeNone_SuppressesTheMxCrossCheck()
    {
        var txt = new TestDnsTxtResolver().Publish($"_mta-sts.{Domain}", "v=STSv1; id=a1");
        var mx = new TestDnsMxResolver().Publish(Domain, new MxHost(10, "uncovered.example.net"));
        var fetcher = new TestMtaStsPolicyFetcher()
            .Serve(Domain, "version: STSv1\nmode: none\nmax_age: 86400\n");

        var result = await Service(txt, mx, fetcher).CheckAsync(Domain, CancellationToken.None);

        Assert.True(result.Policy!.Valid);
        Assert.Null(result.UnmatchedMxHosts);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task NullMx_WithAPolicy_IsCalledOut()
    {
        var txt = new TestDnsTxtResolver().Publish($"_mta-sts.{Domain}", "v=STSv1; id=a1");
        // The resolver strips trailing dots, so RFC 7505's "." arrives empty.
        var mx = new TestDnsMxResolver().Publish(Domain, new MxHost(0, ""));
        var fetcher = new TestMtaStsPolicyFetcher().Serve(Domain, ValidPolicy);

        var result = await Service(txt, mx, fetcher).CheckAsync(Domain, CancellationToken.None);

        Assert.Contains(result.Issues, i => i.Contains("null MX"));
        Assert.Equal([], result.UnmatchedMxHosts ?? []);
    }

    [Fact]
    public async Task WrongContentType_IsAWarningNotAFailure()
    {
        var txt = new TestDnsTxtResolver().Publish($"_mta-sts.{Domain}", "v=STSv1; id=a1");
        var mx = new TestDnsMxResolver().Publish(Domain, new MxHost(10, "mx1.acme.example"));
        var fetcher = new TestMtaStsPolicyFetcher().Serve(Domain, ValidPolicy, contentType: "application/json");

        var result = await Service(txt, mx, fetcher).CheckAsync(Domain, CancellationToken.None);

        Assert.Equal(MtaStsFetchStatus.Ok, result.Fetch!.Status);
        Assert.True(result.Policy!.Valid);
        Assert.Contains(result.Issues, i => i.Contains("application/json"));
    }

    [Fact]
    public async Task InvalidRecord_ShortCircuitsLikeMissing()
    {
        var txt = new TestDnsTxtResolver()
            .Publish($"_mta-sts.{Domain}", "v=STSv1; id=a1", "v=STSv1; id=b2");
        var mx = new TestDnsMxResolver();
        var fetcher = new TestMtaStsPolicyFetcher();

        var result = await Service(txt, mx, fetcher).CheckAsync(Domain, CancellationToken.None);

        Assert.Equal(MtaStsRecordStatus.Invalid, result.Record.Status);
        Assert.Empty(fetcher.Fetched);
        Assert.Empty(mx.Queried);
    }
}
