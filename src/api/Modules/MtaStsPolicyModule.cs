using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.MtaSts;
using DmarcAnalyzer.Api.Contracts.MtaSts;

namespace DmarcAnalyzer.Api.Modules;

public sealed class MtaStsPolicyModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/domains/{domainId:guid}/mta-sts-policy", async (
            Guid domainId,
            IMtaStsPolicyAdminService service,
            CancellationToken ct) =>
        {
            var response = await service.GetAsync(domainId, ct);
            return response is null ? Results.NotFound() : Results.Ok(response);
        }).AllowClientViewer();

        // Admin, like domain management itself: this config directs client DNS
        // and certificate issuance.
        app.MapPut("/api/v1/domains/{domainId:guid}/mta-sts-policy", async (
            Guid domainId,
            UpsertMtaStsPolicyRequest request,
            IMtaStsPolicyAdminService service,
            IAuditLog audit,
            CancellationToken ct) =>
        {
            var result = await service.UpsertAsync(domainId, request, ct);
            if (!result.IsSuccess)
            {
                return result.StatusCode == 404
                    ? Results.NotFound()
                    : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var upsert = result.Value!;
            if (upsert.Outcome != MtaStsPolicyOutcome.Unchanged)
            {
                var policy = upsert.Response.Policy!;
                await audit.RecordAsync(
                    upsert.Outcome == MtaStsPolicyOutcome.Created
                        ? AuditEvents.MtaStsPolicyCreated
                        : AuditEvents.MtaStsPolicyUpdated,
                    $"{(upsert.Outcome == MtaStsPolicyOutcome.Created ? "Created" : "Updated")} hosted " +
                    $"MTA-STS policy for {upsert.Response.DomainName}",
                    "domain", domainId,
                    details: Describe(policy, upsert.PreviousPolicyId), ct: ct);
            }

            return Results.Ok(upsert.Response);
        }).RequireAgencyAdmin();

        app.MapDelete("/api/v1/domains/{domainId:guid}/mta-sts-policy", async (
            Guid domainId,
            IMtaStsPolicyAdminService service,
            IAuditLog audit,
            CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(domainId, ct);
            if (!result.IsSuccess)
            {
                return result.StatusCode == 404
                    ? Results.NotFound()
                    : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            await audit.RecordAsync(AuditEvents.MtaStsPolicyDeleted,
                $"Deleted hosted MTA-STS policy for {result.Value!.DomainName} — the client's " +
                "mta-sts CNAME and _mta-sts TXT records should be removed too",
                "domain", domainId, ct: ct);
            return Results.NoContent();
        }).RequireAgencyAdmin();

        // Same-provider fleets: one policy shape across several of a client's
        // domains. Loops the same upsert core as the single PUT, so each domain
        // keeps its own id-bump-only-when-changed semantics.
        app.MapPost("/api/v1/clients/{clientId:guid}/mta-sts-policy/apply", async (
            Guid clientId,
            BulkApplyMtaStsPolicyRequest request,
            IMtaStsPolicyAdminService service,
            IAuditLog audit,
            CancellationToken ct) =>
        {
            var result = await service.BulkApplyAsync(clientId, request, ct);
            if (!result.IsSuccess)
            {
                return result.StatusCode == 404
                    ? Results.NotFound()
                    : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var response = result.Value!;
            var changed = response.Results.Where(r => r.Outcome != MtaStsPolicyOutcome.Unchanged).ToList();
            if (changed.Count > 0)
            {
                await audit.RecordAsync(AuditEvents.MtaStsPolicyBulkApplied,
                    $"Applied a hosted MTA-STS policy to {changed.Count} domain(s)",
                    "client", clientId, clientId,
                    details:
                        $"mode={request.Mode.Trim().ToLowerInvariant()} max_age={request.MaxAgeSeconds} " +
                        $"mx={request.MxPatterns.Length}; " +
                        string.Join(", ", changed.Select(r => $"{r.DomainName} ({r.Outcome}, id {r.PolicyId})")),
                    ct: ct);
            }

            return Results.Ok(response);
        }).RequireAgencyAdmin();
    }

    private static string Describe(MtaStsPolicyDto policy, string? previousPolicyId) =>
        $"enabled={policy.Enabled.ToString().ToLowerInvariant()} mode={policy.Mode} " +
        $"max_age={policy.MaxAgeSeconds} mx={policy.MxPatterns.Count}" +
        (previousPolicyId is null || previousPolicyId == policy.PolicyId
            ? $" id={policy.PolicyId}"
            : $" id {previousPolicyId} → {policy.PolicyId} — the _mta-sts TXT record needs updating");
}
