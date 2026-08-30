namespace DmarcAnalyzer.Api.Contracts.Auth;

/// <summary>Body of POST /auth/login.</summary>
public sealed class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
