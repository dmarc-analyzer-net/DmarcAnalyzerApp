using DmarcAnalyzer.Api.Application.Auth;

namespace DmarcAnalyzer.Api.Middleware;

/// <summary>
/// Enforces endpoint role requirements after SessionAuthMiddleware has
/// authenticated the request. Endpoints without RoleRequirementMetadata
/// default to agency staff, so client_viewer is deny-by-default: new
/// endpoints must opt in via AllowClientViewer() to be visible to viewers.
/// </summary>
public sealed class RoleAuthorizationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context, ICurrentUserContext currentUser, IMachineCallerContext machineCaller)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;

        if (!path.StartsWith("/api/v1/"))
        {
            await next(context);
            return;
        }

        // Machine endpoints are a closed door, not a wider one: they require a credential of
        // the named kind, and a session — even an admin's — does not substitute. These
        // endpoints resolve their tenant from the credential, so a session has nothing to
        // resolve.
        var machineRequirement = context.GetEndpoint()?.Metadata
            .GetMetadata<MachineCredentialRequirementMetadata>();

        if (machineRequirement is not null)
        {
            if (!machineCaller.IsAuthenticated || machineCaller.Kind != machineRequirement.Kind)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "forbidden" });
                return;
            }

            await next(context);
            return;
        }

        // Everything else is for people. A credential that authenticated is refused here,
        // which is what keeps a report-ingest token out of the console API.
        if (machineCaller.IsAuthenticated)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "forbidden" });
            return;
        }

        // Public paths were already passed through by SessionAuthMiddleware.
        if (!currentUser.IsAuthenticated)
        {
            await next(context);
            return;
        }

        var requirement = context.GetEndpoint()?.Metadata
            .GetMetadata<RoleRequirementMetadata>()?.Requirement
            ?? RoleRequirement.AgencyStaff;

        var allowed = requirement switch
        {
            RoleRequirement.AgencyAdmin => currentUser.IsAdmin,
            RoleRequirement.AgencyStaff => currentUser.IsAgencyStaff,
            RoleRequirement.AnyAuthenticated => true,
            _ => false,
        };

        if (!allowed)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "forbidden" });
            return;
        }

        await next(context);
    }
}
