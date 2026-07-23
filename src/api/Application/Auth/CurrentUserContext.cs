namespace DmarcAnalyzer.Api.Application.Auth;

public sealed class CurrentUserContext : ICurrentUserContext
{
    private HashSet<Guid> _allowedClientIds = [];

    public bool IsAuthenticated { get; private set; }
    public Guid UserId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string Role { get; private set; } = string.Empty;
    public bool IsAdmin => Role == Roles.AgencyAdmin;
    public bool IsAgencyStaff => Roles.IsAgencyStaff(Role);
    public IReadOnlyCollection<Guid> AllowedClientIds => _allowedClientIds;

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
