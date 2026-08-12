namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>Endpoint role requirements enforced by RoleAuthorizationMiddleware.</summary>
public sealed record RoleRequirementMetadata(RoleRequirement Requirement);

/// <summary>
/// Marks an endpoint as reachable only by a machine credential of this kind. Not an
/// additional way in for a session: a cookie-authenticated admin is refused too, because
/// these endpoints resolve their tenant from the credential and there is nothing sensible
/// for them to do without one.
/// </summary>
public sealed record MachineCredentialRequirementMetadata(string Kind);

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

    /// <summary>
    /// Requires a machine credential of <paramref name="kind"/>. Deny-by-default in the same
    /// way <c>client_viewer</c> is: an endpoint that has not opted in is unreachable with a
    /// bearer token, whatever the credential is scoped to.
    /// </summary>
    public static TBuilder RequireMachineCredential<TBuilder>(this TBuilder builder, string kind)
        where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new MachineCredentialRequirementMetadata(kind));
}
