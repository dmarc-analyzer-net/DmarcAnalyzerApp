namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>
/// Per-request identity and tenancy scope, populated by SessionAuthMiddleware.
/// Agency staff (admin/analyst) are unrestricted; client_viewer users are
/// limited to their granted clients.
/// </summary>
public interface ICurrentUserContext
{
    bool IsAuthenticated { get; }
    Guid UserId { get; }
    string Email { get; }
    string Role { get; }
    bool IsAdmin { get; }
    bool IsAgencyStaff { get; }

    /// <summary>Granted client ids; only meaningful when not agency staff.</summary>
    IReadOnlyCollection<Guid> AllowedClientIds { get; }

    bool CanAccessClient(Guid clientId);
}
