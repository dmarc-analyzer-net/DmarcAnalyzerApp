namespace DmarcAnalyzer.Api.Contracts.Users;

/// <summary>Body of PATCH /users/{id}; null fields stay unchanged.</summary>
public sealed class UpdateUserRequest
{
    public string? DisplayName { get; set; }
    public string? Role { get; set; }
    public bool? IsActive { get; set; }
    public string? Password { get; set; }
}
