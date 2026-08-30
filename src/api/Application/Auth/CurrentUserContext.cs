namespace DmarcAnalyzer.Api.Application.Auth;

/// <summary>
/// The request-scoped <see cref="ICurrentUserContext"/>. Starts unauthenticated;
/// SessionAuthMiddleware calls <c>Set</c> once the session cookie checks out.
/// </summary>
public sealed class CurrentUserContext : ICurrentUserContext
{
    private HashSet<Guid> _allowedClientIds = [];

    /// <inheritdoc />
    public bool IsAuthenticated { get; private set; }

    /// <inheritdoc />
    public Guid UserId { get; private set; }

    /// <inheritdoc />
    public string Email { get; private set; } = string.Empty;

    /// <inheritdoc />
    public string Role { get; private set; } = string.Empty;

    /// <inheritdoc />
    public bool IsAdmin => Role == Roles.AgencyAdmin;

    /// <inheritdoc />
    public bool IsAgencyStaff => Roles.IsAgencyStaff(Role);

    /// <inheritdoc />
    public IReadOnlyCollection<Guid> AllowedClientIds => _allowedClientIds;

    /// <inheritdoc />
    public bool CanAccessClient(Guid clientId)
        => IsAgencyStaff || _allowedClientIds.Contains(clientId);

    internal void Set(UserDto user, IReadOnlyList<Guid> grantedClientIds)
    {
        IsAuthenticated = true;
        UserId = user.Id;
        Email = user.Email;
        Role = user.Role;
        _allowedClientIds = [.. grantedClientIds];
    }
}
