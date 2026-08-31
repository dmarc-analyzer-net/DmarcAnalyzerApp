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

/// <summary>What an endpoint demands of the session role — enforced centrally, deny-by-default.</summary>
public enum RoleRequirement
{
    /// <summary>Admin and analyst only. This is also the default for endpoints without metadata.</summary>
    AgencyStaff,

    /// <summary>agency_admin only.</summary>
    AgencyAdmin,

    /// <summary>Any authenticated role, including client_viewer (data must be scoped in the service).</summary>
    AnyAuthenticated,
}

/// <summary>How endpoints declare their role requirement to RoleAuthorizationMiddleware.</summary>
public static class EndpointAuthExtensions
{
    /// <summary>Restricts the endpoint to agency_admin.</summary>
    public static TBuilder RequireAgencyAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new RoleRequirementMetadata(RoleRequirement.AgencyAdmin));

    /// <summary>States the default (admin + analyst) explicitly, for endpoints worth reading that on.</summary>
    public static TBuilder RequireAgencyStaff<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new RoleRequirementMetadata(RoleRequirement.AgencyStaff));

    /// <summary>
    /// Opts the endpoint in for client_viewer sessions — the service behind it
    /// must scope its data through <see cref="ICurrentUserContext.CanAccessClient"/>.
    /// </summary>
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
