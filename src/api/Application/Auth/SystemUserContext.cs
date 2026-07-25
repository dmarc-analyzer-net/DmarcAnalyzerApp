namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>
/// The identity a worker pass runs as. Worker mode has no HTTP request and so no
/// signed-in user, but services that take <see cref="ICurrentUserContext"/> still
/// need one. It reports agency-staff privileges because worker passes operate
/// across every tenant by design.
/// </summary>
public sealed class SystemUserContext : ICurrentUserContext
{
    public bool IsAuthenticated => false;
    public Guid UserId => Guid.Empty;
    public string Email => "system";
    public string Role => Roles.AgencyAdmin;
    public bool IsAdmin => true;
    public bool IsAgencyStaff => true;
    public IReadOnlyCollection<Guid> AllowedClientIds => [];
    public bool CanAccessClient(Guid clientId) => true;
}
