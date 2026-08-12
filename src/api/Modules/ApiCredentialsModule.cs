using Carter;
using DmarcAnalyzer.Api.Application.ApiCredentials;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;

namespace DmarcAnalyzer.Api.Modules;

/// <summary>
/// Issuing and revoking machine credentials. Admin only — a credential is a way into the
/// tenant's data, so handing one out is the same class of act as creating a user.
/// </summary>
public sealed class ApiCredentialsModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/api-credentials", async (
            Guid? reportSourceId, IApiCredentialService service, CancellationToken ct) =>
        {
            return Results.Ok(await service.ListAsync(reportSourceId, ct));
        }).RequireAgencyAdmin();

        app.MapPost("/api/v1/api-credentials", async (
            IssueApiCredentialRequest request,
            IApiCredentialService service,
            IAuditLog audit,
            ICurrentUserContext currentUser,
            CancellationToken ct) =>
        {
            var result = await service.IssueAsync(
                request.ReportSourceId, request.Name, request.ExpiresAtUtc, currentUser.UserId, ct);

            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var issued = result.Value!;
            await audit.RecordAsync(
                AuditEvents.ApiCredentialIssued,
                $"Issued machine credential {issued.Credential.Name} for report source " +
                $"{issued.Credential.ReportSourceName}",
                "api_credential", issued.Credential.Id, ct: ct);

            // 201 with the token in the body, once. There is no GET that can return it
            // again, which is the whole point of reveal-once.
            return Results.Created($"/api/v1/api-credentials/{issued.Credential.Id}", issued);
        }).RequireAgencyAdmin();

        app.MapPost("/api/v1/api-credentials/{id:guid}/revoke", async (
            Guid id, IApiCredentialService service, IAuditLog audit, CancellationToken ct) =>
        {
            var result = await service.RevokeAsync(id, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            await audit.RecordAsync(
                AuditEvents.ApiCredentialRevoked,
                $"Revoked machine credential {result.Value!.Name}",
                "api_credential", id, ct: ct);

            return Results.Ok(result.Value);
        }).RequireAgencyAdmin();
    }
}

public sealed class IssueApiCredentialRequest
{
    public Guid ReportSourceId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional. Null means no expiry, which is the default — see ADR 0010.</summary>
    public DateTime? ExpiresAtUtc { get; set; }
}
