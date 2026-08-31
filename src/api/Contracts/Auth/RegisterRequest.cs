namespace DmarcAnalyzer.Api.Contracts.Auth;

/// <summary>Body of POST /api/v1/auth/register — the one-time bootstrap of the first admin.</summary>
public sealed class RegisterRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "agency_admin";
}
