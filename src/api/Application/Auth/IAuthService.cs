using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Auth;

namespace DmarcAnalyzer.Api.Application.Auth;

public interface IAuthService
{
    Task<ServiceResult<UserDto>> RegisterAsync(RegisterRequest request, CancellationToken ct);
    Task<ServiceResult<LoginResultDto>> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct);
    Task LogoutAsync(string cookieId, CancellationToken ct);
    Task<UserDto?> GetCurrentUserAsync(string cookieId, CancellationToken ct);
}
