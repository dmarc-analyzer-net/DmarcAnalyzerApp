namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>Endpoint role requirements enforced by RoleAuthorizationMiddleware.</summary>
public sealed record RoleRequirementMetadata(RoleRequirement Requirement);

public enum RoleRequirement
{
    /// <summary>Admin and analyst only. This is also the default for endpoints without metadata.</summary>
    AgencyStaff,

    /// <summary>agency_admin only.</summary>
    AgencyAdmin,

    /// <summary>Any authenticated role, including client_viewer (data must be scoped in the service).</summary>
    AnyAuthenticated,
}

public static class EndpointAuthExtensions
{
    public static TBuilder RequireAgencyAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new RoleRequirementMetadata(RoleRequirement.AgencyAdmin));

    public static TBuilder RequireAgencyStaff<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new RoleRequirementMetadata(RoleRequirement.AgencyStaff));

    public static TBuilder AllowClientViewer<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new RoleRequirementMetadata(RoleRequirement.AnyAuthenticated));
}
