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
    public async Task InvokeAsync(HttpContext context, ICurrentUserContext currentUser)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;

        // Public and non-API paths were already passed through by SessionAuthMiddleware.
        if (!path.StartsWith("/api/v1/") || !currentUser.IsAuthenticated)
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
