namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>
/// The identity a worker pass runs as. Worker mode has no HTTP request and so no
/// signed-in user, but services that take <see cref="ICurrentUserContext"/> still
/// need one. It reports agency-staff privileges because worker passes operate
/// across every tenant by design.
/// </summary>
public sealed class SystemUserContext : ICurrentUserContext
{
    /// <inheritdoc />
    public bool IsAuthenticated => false;

    /// <inheritdoc />
    public Guid UserId => Guid.Empty;

    /// <inheritdoc />
    public string Email => "system";

    /// <inheritdoc />
    public string Role => Roles.AgencyAdmin;

    /// <inheritdoc />
    public bool IsAdmin => true;

    /// <inheritdoc />
    public bool IsAgencyStaff => true;

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> AllowedClientIds => [];

    /// <inheritdoc />
    public bool CanAccessClient(Guid clientId) => true;
}
