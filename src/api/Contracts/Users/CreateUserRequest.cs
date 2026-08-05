namespace DmarcAnalyzer.Api.Contracts.Users;

public sealed class CreateUserRequest
{
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Omit (or send empty) to create an account with no password — one that can
    /// only sign in through OIDC, the same shape auto-provisioning produces.
    /// </summary>
    public string? Password { get; set; }

    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}
