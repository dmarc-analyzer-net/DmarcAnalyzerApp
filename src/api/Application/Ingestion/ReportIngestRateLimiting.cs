using System.Globalization;
using System.Threading.RateLimiting;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Workers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// Rate limiting for the machine ingestion endpoint.
/// <para>
/// The size ceiling bounds one request; this bounds how many. Without it a leaked
/// credential can post the maximum payload as fast as the network allows, and the size
/// limit only decides how much damage each one does. ADR 0010 lists this as required
/// before the endpoint is reachable from the internet.
/// </para>
/// <para>
/// Partitioned by credential rather than by address. A pipeline behind NAT would otherwise
/// share a bucket with everything else on that address, so one noisy neighbour could
/// starve an unrelated tenant's ingestion — and the thing actually worth limiting is the
/// credential, because that is what a leak hands over.
/// </para>
/// </summary>
public static class ReportIngestRateLimiting
{
    public const string PolicyName = "report-ingest";

    public static IServiceCollection AddReportIngestRateLimiter(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(PolicyName, context =>
            {
                var options = context.RequestServices
                    .GetRequiredService<IOptions<WorkerOptions>>().Value;
                var caller = context.RequestServices.GetRequiredService<IMachineCallerContext>();

                // Authenticated is the normal case — the endpoint refuses anything else, so
                // an unauthenticated request has already been rejected upstream. The address
                // fallback exists so the partition key is never empty rather than as a
                // second control.
                var partition = caller.IsAuthenticated
                    ? $"credential:{caller.CredentialId}"
                    : $"address:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

                return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.ReportIngestRateLimitPermits),
                    Window = TimeSpan.FromSeconds(Math.Max(1, options.ReportIngestRateLimitWindowSeconds)),

                    // No queue. A report is not a request worth holding open — a caller
                    // told to come back in a moment retries cheaply, whereas a queued
                    // request occupies a connection and makes the pile-up worse.
                    QueueLimit = 0,
                });
            });

            limiter.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                // Retry-After turns "slow down" into something a client can act on without
                // guessing. Without it a well-behaved caller has no choice but to poll.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "too many requests for this credential" }, ct);
            };
        });

        return services;
    }
}
