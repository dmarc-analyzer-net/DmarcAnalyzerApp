namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>
/// Per-request identity and tenancy scope, populated by SessionAuthMiddleware.
/// Agency staff (admin/analyst) are unrestricted; client_viewer users are
/// limited to their granted clients.
/// </summary>
public interface ICurrentUserContext
{
    /// <summary>False for anonymous requests — and for worker passes, which run as <see cref="SystemUserContext"/>.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The signed-in user's id; <see cref="Guid.Empty"/> when unauthenticated.</summary>
    Guid UserId { get; }

    /// <summary>The signed-in user's email, or empty when unauthenticated.</summary>
    string Email { get; }

    /// <summary>One of <see cref="Roles.All"/>, or empty when unauthenticated.</summary>
    string Role { get; }

    /// <summary>agency_admin only.</summary>
    bool IsAdmin { get; }

    /// <summary>agency_admin or agency_analyst — the roles that see every client.</summary>
    bool IsAgencyStaff { get; }

    /// <summary>Granted client ids; only meaningful when not agency staff.</summary>
    IReadOnlyCollection<Guid> AllowedClientIds { get; }

    /// <summary>The tenancy check every client-scoped read goes through: staff always, viewers per grant.</summary>
    bool CanAccessClient(Guid clientId);
}
