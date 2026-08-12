using Carter;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Data.Entities;
using DmarcAnalyzer.Api.Workers;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Modules;

/// <summary>
/// The push half of ingestion: raw report bytes posted by a machine.
/// <para>
/// There is no source id in the path. The credential decides which source — and therefore
/// which client — the data lands under, per ADR 0010, and a path parameter would invite a
/// caller to think otherwise and create a mismatch case that only exists because the route
/// asked for it.
/// </para>
/// </summary>
public sealed class ReportIngestionModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/reports", async (
            HttpRequest http,
            IMachineCallerContext caller,
            IPushedReportIngestService service,
            IOptions<WorkerOptions> options,
            CancellationToken ct) =>
        {
            var maxBytes = options.Value.MaxPushedReportRequestBytes;

            // Checked before reading where the length is declared, so an oversized payload
            // is refused without transferring it. The read below is still bounded, because
            // a chunked request declares nothing.
            if (http.ContentLength is > 0 && http.ContentLength > maxBytes)
            {
                return Results.Json(
                    new { error = $"payload exceeds Worker:MaxPushedReportRequestBytes ({maxBytes})" },
                    statusCode: 413);
            }

            byte[] body;
            try
            {
                body = await ReadBoundedAsync(http.Body, maxBytes, ct);
            }
            catch (InvalidOperationException)
            {
                return Results.Json(
                    new { error = $"payload exceeds Worker:MaxPushedReportRequestBytes ({maxBytes})" },
                    statusCode: 413);
            }

            var result = await service.IngestAsync(
                caller.ReportSourceId,
                body,
                http.Headers.ContentDisposition.ToString() is { Length: > 0 } cd ? FileNameFrom(cd) : null,
                http.ContentType,
                http.Headers["X-Report-Provenance"].ToString() is { Length: > 0 } p ? p : null,
                ct);

            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
        })
        .RequireMachineCredential(MachineCredentialKinds.ReportIngest)
        .RequireRateLimiting(ReportIngestRateLimiting.PolicyName)
        .DisableAntiforgery();
    }

    /// <summary>
    /// Reads the request body with a ceiling, stopping while reading rather than after. A
    /// declared Content-Length is a claim, not a guarantee, and a chunked request makes no
    /// claim at all.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        using var destination = new MemoryStream();
        long total = 0;

        while (true)
        {
            var read = await body.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException("request body exceeded the configured maximum");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return destination.ToArray();
    }

    private static string? FileNameFrom(string contentDisposition)
    {
        foreach (var part in contentDisposition.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["filename=".Length..].Trim('"');
            }
        }

        return null;
    }
}
