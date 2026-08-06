using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.MtaSts;

/// <summary>
/// The two anonymous MTA-STS routes, mapped by the api/all pipeline and by the
/// dedicated APP_MODE=mta-sts host. Deliberately not a Carter module:
/// MapCarter() assembly-scans every ICarterModule, so anything the dedicated
/// public host serves must live outside Carter or it would drag the whole
/// console API onto the internet-facing container.
///
/// No auth opt-outs needed anywhere — SessionAuthMiddleware guards only
/// /api/v1/* paths, the same mechanism that makes /health/* public.
/// </summary>
public static class MtaStsPublicEndpoints
{
    public static IEndpointRouteBuilder MapMtaStsPublicEndpoints(this IEndpointRouteBuilder app)
    {
        // MapGet alone answers HEAD with 405, and uptime checkers HEAD this.
        app.MapMethods(
            "/.well-known/mta-sts.txt",
            [HttpMethods.Get, HttpMethods.Head],
            async (HttpContext context, IMtaStsPolicyHostService host, IOptions<MtaStsOptions> options, CancellationToken ct) =>
            {
                var body = await host.GetPolicyBodyForHostAsync(context.Request.Host.Host, ct);
                if (body is null)
                {
                    // Plain text, never the SPA fallback and never a redirect —
                    // senders treat any redirect as a broken policy host.
                    return Results.Text("not found", "text/plain", statusCode: 404);
                }

                context.Response.Headers.CacheControl =
                    $"public, max-age={Math.Clamp(options.Value.ServeCacheSeconds, 0, 3600)}";
                return Results.Text(body, "text/plain");
            });

        // Caddy's on_demand_tls ask endpoint: 200 = issue a certificate for this
        // hostname, anything else = refuse. 403 rather than 404 so a wrong-path
        // probe stays distinguishable from a deny while debugging.
        app.MapGet("/mta-sts/ask", async (string? domain, IMtaStsPolicyHostService host, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                return Results.Text("domain query parameter is required", "text/plain", statusCode: 400);
            }

            return await host.IsKnownPolicyHostAsync(domain, ct)
                ? Results.Text(string.Empty, "text/plain")
                : Results.Text("unknown policy host", "text/plain", statusCode: 403);
        });

        return app;
    }
}
