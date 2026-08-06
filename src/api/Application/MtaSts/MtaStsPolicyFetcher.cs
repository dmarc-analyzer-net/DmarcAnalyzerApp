using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.MtaSts;

public interface IMtaStsPolicyFetcher
{
    /// <summary>
    /// GET https://mta-sts.{domain}/.well-known/mta-sts.txt, without following
    /// redirects (RFC 8461 §3.3 forbids senders to). Never throws for a broken
    /// policy host — every failure mode is a status the check reports.
    /// </summary>
    Task<MtaStsPolicyFetchResult> FetchAsync(string domain, CancellationToken ct);
}

/// <summary>
/// The policy-file fetch. TLS validation is the point of the exercise — senders
/// refuse an invalid chain — so a certificate failure is captured with its
/// reason and reported as <c>tls_failed</c>, never surfaced as an exception.
/// </summary>
public sealed class MtaStsPolicyFetcher(
    IHttpClientFactory httpClientFactory,
    ILogger<MtaStsPolicyFetcher> logger) : IMtaStsPolicyFetcher
{
    public const string ClientName = "mta-sts";

    /// <summary>
    /// RFC 8461 only asks senders to bound the fetch; 64 KB is orders of
    /// magnitude above any real policy and small enough to be harmless.
    /// </summary>
    public const int MaxPolicyBytes = 64 * 1024;

    /// <summary>
    /// Carries the certificate-failure description out of the TLS handshake.
    /// AsyncLocal values set inside an awaited child do not flow back to the
    /// caller, so the caller plants a mutable box before sending and the
    /// validation callback (which runs inside the request's flow, where the box
    /// is visible) writes into it.
    /// </summary>
    private static readonly AsyncLocal<StrongBox<string?>?> CertFailure = new();

    public async Task<MtaStsPolicyFetchResult> FetchAsync(string domain, CancellationToken ct)
    {
        var url = $"https://mta-sts.{domain}/.well-known/mta-sts.txt";
        var certFailure = new StrongBox<string?>(null);
        CertFailure.Value = certFailure;

        try
        {
            var client = httpClientFactory.CreateClient(ClientName);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            var statusCode = (int)response.StatusCode;
            if (statusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location?.ToString();
                return new MtaStsPolicyFetchResult(
                    MtaStsFetchStatus.Redirected, null, statusCode,
                    $"HTTP {statusCode} redirect{(location is null ? "" : $" to {location}")} — senders never follow redirects here.",
                    null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new MtaStsPolicyFetchResult(
                    MtaStsFetchStatus.HttpError, null, statusCode, $"HTTP {statusCode}.", null);
            }

            if (response.Content.Headers.ContentLength is > MaxPolicyBytes)
            {
                return new MtaStsPolicyFetchResult(
                    MtaStsFetchStatus.TooLarge, null, statusCode,
                    $"Policy file is {response.Content.Headers.ContentLength:N0} bytes — senders bound this fetch; keep it under {MaxPolicyBytes / 1024} KB.",
                    null);
            }

            var (body, overflowed) = await ReadBoundedAsync(response, ct);
            if (overflowed)
            {
                return new MtaStsPolicyFetchResult(
                    MtaStsFetchStatus.TooLarge, null, statusCode,
                    $"Policy file exceeds {MaxPolicyBytes / 1024} KB — senders bound this fetch.",
                    null);
            }

            return new MtaStsPolicyFetchResult(
                MtaStsFetchStatus.Ok, body, statusCode, null,
                response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The client's own Timeout, not our caller's token.
            return new MtaStsPolicyFetchResult(
                MtaStsFetchStatus.Timeout, null, null, "The policy host did not answer in time.", null);
        }
        catch (Exception ex)
        {
            if (certFailure.Value is { } certDetail)
            {
                return new MtaStsPolicyFetchResult(MtaStsFetchStatus.TlsFailed, null, null, certDetail, null);
            }

            if (HasInner<AuthenticationException>(ex))
            {
                // Fallback when the handshake failed before the validation
                // callback ran (protocol mismatch, closed mid-handshake).
                return new MtaStsPolicyFetchResult(
                    MtaStsFetchStatus.TlsFailed, null, null, RootMessage(ex), null);
            }

            logger.LogDebug(ex, "MTA-STS policy fetch failed for {Domain}", domain);
            return new MtaStsPolicyFetchResult(
                MtaStsFetchStatus.ConnectFailed, null, null, RootMessage(ex), null);
        }
        finally
        {
            CertFailure.Value = null;
        }
    }

    private static async Task<(string Body, bool Overflowed)> ReadBoundedAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();

        var chunk = new byte[16 * 1024];
        while (buffer.Length <= MaxPolicyBytes)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
            {
                return (System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), false);
            }

            buffer.Write(chunk, 0, read);
        }

        return (string.Empty, true);
    }

    /// <summary>
    /// The handler for the named client. Built here rather than inline in DI so
    /// the redirect/TLS/egress decisions live next to the fetch they protect.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(MtaStsOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // a redirect is a finding, not a hop
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = CaptureThenReject,
            },
        };

        if (!options.AllowPrivateNetworks)
        {
            handler.ConnectCallback = ConnectPublicOnlyAsync;
        }

        return handler;
    }

    /// <summary>
    /// Validation stays strict — any error still fails the handshake — but the
    /// reason is captured first so the check can say *why* senders would refuse
    /// this host, instead of a bare "TLS failed".
    /// </summary>
    internal static bool CaptureThenReject(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
        {
            return true;
        }

        if (CertFailure.Value is { } box)
        {
            var cert2 = certificate as X509Certificate2;
            var subject = cert2?.Subject ?? certificate?.Subject;
            var expiry = cert2 is null ? null : $", expires {cert2.NotAfter.ToUniversalTime():yyyy-MM-dd}";
            box.Value = $"Certificate rejected ({sslPolicyErrors})" +
                        (subject is null ? "" : $": {subject}{expiry}") +
                        ". Senders refuse the policy host when the chain does not validate.";
        }

        return false;
    }

    /// <summary>
    /// Egress guard: mta-sts hostnames derive from operator-entered domains, so
    /// without this the fetcher is steerable at anything the instance's network
    /// can reach. Addresses inside private/loopback/link-local space are refused
    /// before a connection is attempted.
    /// </summary>
    private static async ValueTask<System.IO.Stream> ConnectPublicOnlyAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);

        var allowed = addresses.Where(a => !IsDisallowedAddress(a)).ToArray();
        if (allowed.Length == 0)
        {
            throw new HttpRequestException(
                addresses.Length == 0
                    ? $"{host} does not resolve."
                    : $"{host} resolves only to private or local addresses; refusing to connect " +
                      "(MtaSts__AllowPrivateNetworks enables this for intranet deployments).");
        }

        Exception? lastFailure = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                socket.Dispose();
                lastFailure = ex;
            }
        }

        throw lastFailure!;
    }

    /// <summary>Public static so the egress-guard table is unit-testable without sockets.</summary>
    public static bool IsDisallowedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10                                  // 10/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)   // 172.16/12
                || (b[0] == 192 && b[1] == 168)                // 192.168/16
                || (b[0] == 169 && b[1] == 254)                // link-local
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)  // CGNAT / mesh-VPN space
                || b[0] == 0;                                  // 0/8
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || (b[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        }

        return true; // unknown family — refuse rather than guess
    }

    private static bool HasInner<T>(Exception ex) where T : Exception
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            if (current is T)
            {
                return true;
            }

            if (current.InnerException is null)
            {
                return false;
            }
        }

        return false;
    }

    private static string RootMessage(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }
}
