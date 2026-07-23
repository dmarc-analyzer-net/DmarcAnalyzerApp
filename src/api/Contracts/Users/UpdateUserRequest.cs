namespace DmarcAnalyzer.Api.Contracts.Users;

public sealed class UpdateUserRequest
{
    public string? DisplayName { get; set; }
    public string? Role { get; set; }
    public bool? IsActive { get; set; }
    public string? Password { get; set; }
}
