using System.Net;
using System.Text;
using DmarcAnalyzer.Api.Application.MtaSts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The policy-file fetch, against a stubbed handler — no network. Guards the
/// RFC 8461 behaviors senders rely on: redirects are reported rather than
/// followed, oversized bodies are bounded, and every failure mode comes back as
/// a status instead of an exception.
/// </summary>
public sealed class MtaStsPolicyFetcherTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler, TimeSpan? timeout = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
    }

    private static MtaStsPolicyFetcher Fetcher(HttpMessageHandler handler, TimeSpan? timeout = null)
        => new(new StubFactory(handler, timeout), NullLogger<MtaStsPolicyFetcher>.Instance);

    private static HttpResponseMessage Response(
        HttpStatusCode status, string? body = null, string contentType = "text/plain")
    {
        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        return response;
    }

    [Fact]
    public async Task Ok_ReturnsBodyAndContentType()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("https://mta-sts.acme.example/.well-known/mta-sts.txt", request.RequestUri!.ToString());
            return Response(HttpStatusCode.OK, "version: STSv1\nmode: testing\nmx: a\nmax_age: 86400\n");
        });

        var result = await Fetcher(handler).FetchAsync("acme.example", CancellationToken.None);

        Assert.Equal(MtaStsFetchStatus.Ok, result.Status);
        Assert.Contains("mode: testing", result.Body);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal("text/plain", result.ContentType);
    }

    [Fact]
    public async Task Redirect_IsReportedAndNeverFollowed()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            response.Headers.Location = new Uri("https://elsewhere.example/policy");
            return response;
        });

        var result = await Fetcher(handler).FetchAsync("acme.example", CancellationToken.None);

        Assert.Equal(MtaStsFetchStatus.Redirected, result.Status);
        Assert.Equal(301, result.HttpStatusCode);
        Assert.Contains("elsewhere.example", result.Detail);
        Assert.Equal(1, handler.Calls); // one request, no hop
    }

    [Fact]
    public async Task HttpError_CarriesTheStatusCode()
    {
        var result = await Fetcher(new StubHandler(_ => Response(HttpStatusCode.NotFound)))
            .FetchAsync("acme.example", CancellationToken.None);

        Assert.Equal(MtaStsFetchStatus.HttpError, result.Status);
        Assert.Equal(404, result.HttpStatusCode);
    }

    [Fact]
    public async Task OversizedBody_WithContentLength_IsTooLarge()
    {
        var big = new string('x', MtaStsPolicyFetcher.MaxPolicyBytes + 1);
        var result = await Fetcher(new StubHandler(_ => Response(HttpStatusCode.OK, big)))
            .FetchAsync("acme.example", CancellationToken.None);

        Assert.Equal(MtaStsFetchStatus.TooLarge, result.Status);
        Assert.Null(result.Body);
    }

    [Fact]
    public async Task OversizedBody_WithoutContentLength_IsBoundedByTheRead()
    {
        var big = new string('x', MtaStsPolicyFetcher.MaxPolicyBytes + 1);
        var handler = new StubHandler(_ =>
        {
            var response = Response(HttpStatusCode.OK, big);
            // Chunked-style response: no Content-Length, so only the bounded
            // read can stop it.
            response.Content.Headers.ContentLength = null;
            return response;
        });

        var result = await Fetcher(handler).FetchAsync("acme.example", CancellationToken.None);

        Assert.Equal(MtaStsFetchStatus.TooLarge, result.Status);
    }

    [Fact]
    public async Task ExactlyMaxBytes_IsStillOk()
    {
        var body = new string('x', MtaStsPolicyFetcher.MaxPolicyBytes);
        var result = await Fetcher(new StubHandler(_ => Response(HttpStatusCode.OK, body)))
            .FetchAsync("acme.example", CancellationToken.None);

        Assert.Equal(MtaStsFetchStatus.Ok, result.Status);
        Assert.Equal(MtaStsPolicyFetcher.MaxPolicyBytes, result.Body!.Length);
    }

    [Fact]
    public async Task WrongContentType_IsStillOk_TheCheckDecidesWhatToSay()
    {
        var result = await Fetcher(new StubHandler(_ =>
                Response(HttpStatusCode.OK, "version: STSv1\n", contentType: "application/json")))
            .FetchAsync("acme.example", CancellationToken.None);

        Assert.Equal(MtaStsFetchStatus.Ok, result.Status);
        Assert.Equal("application/json", result.ContentType);
    }

    [Fact]
    public async Task ClientTimeout_ReadsAsTimeout_NotAsACrash()
    {
        var result = await Fetcher(new SlowHandler(), timeout: TimeSpan.FromMilliseconds(100))
            .FetchAsync("acme.example", CancellationToken.None);

        Assert.Equal(MtaStsFetchStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task CallerCancellation_StillPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Fetcher(new SlowHandler()).FetchAsync("acme.example", cts.Token));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("100.64.0.1", true)]     // CGNAT / mesh-VPN space
    [InlineData("100.63.255.255", false)]
    [InlineData("0.0.0.0", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]        // unique local
    [InlineData("fd12::1", true)]
    [InlineData("2606:4700::1111", false)]
    [InlineData("::ffff:192.168.1.1", true)] // IPv4-mapped private
    public void EgressGuard_AddressTable(string address, bool disallowed)
    {
        Assert.Equal(disallowed, MtaStsPolicyFetcher.IsDisallowedAddress(IPAddress.Parse(address)));
    }
}
