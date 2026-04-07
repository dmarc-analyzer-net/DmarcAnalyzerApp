using System.Security.Cryptography;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Auth;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Auth;

public sealed class AuthService(DmarcAnalyzerDbContext db) : IAuthService
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(12);
    private static readonly TimeSpan AbsoluteMax = TimeSpan.FromDays(7);

    private static readonly HashSet<string> ValidRoles = ["agency_admin", "agency_analyst"];

    public async Task<ServiceResult<UserDto>> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return ServiceResult<UserDto>.Failure("email and password are required", 400);
        }

        if (request.Password.Length < 10)
        {
            return ServiceResult<UserDto>.Failure("password must be at least 10 characters", 400);
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return ServiceResult<UserDto>.Failure("displayName is required", 400);
        }

        var role = string.IsNullOrWhiteSpace(request.Role) ? "agency_admin" : request.Role.Trim().ToLowerInvariant();
        if (!ValidRoles.Contains(role))
        {
            return ServiceResult<UserDto>.Failure("role must be agency_admin or agency_analyst", 400);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var emailInUse = await db.AgencyUsers.AnyAsync(x => x.Email == normalizedEmail, ct);
        if (emailInUse)
        {
            return ServiceResult<UserDto>.Failure("email already registered", 409);
        }

        var now = DateTime.UtcNow;
        var user = new AgencyUser
        {
            Email = normalizedEmail,
            PasswordHash = HashPassword(request.Password),
            DisplayName = request.DisplayName.Trim(),
            Role = role,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.AgencyUsers.Add(user);
        await db.SaveChangesAsync(ct);

        return ServiceResult<UserDto>.Success(ToDto(user));
    }

    public async Task<ServiceResult<LoginResultDto>> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return ServiceResult<LoginResultDto>.Failure("email and password are required", 400);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await db.AgencyUsers.SingleOrDefaultAsync(x => x.Email == normalizedEmail, ct);

        if (user is null || !VerifyPassword(request.Password, user.PasswordHash))
        {
            return ServiceResult<LoginResultDto>.Failure("invalid credentials", 401);
        }

        if (!user.IsActive)
        {
            return ServiceResult<LoginResultDto>.Failure("account is deactivated", 403);
        }

        var now = DateTime.UtcNow;
        user.LastLoginAtUtc = now;
        user.UpdatedAtUtc = now;

        var session = new UserSession
        {
            UserId = user.Id,
            CookieId = GenerateCookieId(),
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.Add(AbsoluteMax),
            IpAddress = ipAddress,
            UserAgent = userAgent?.Length > 512 ? userAgent[..512] : userAgent,
        };

        db.UserSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return ServiceResult<LoginResultDto>.Success(new LoginResultDto(ToDto(user), session.CookieId));
    }

    public async Task LogoutAsync(string cookieId, CancellationToken ct)
    {
        var session = await db.UserSessions.SingleOrDefaultAsync(x => x.CookieId == cookieId, ct);
        if (session is not null && session.RevokedAtUtc is null)
        {
            session.RevokedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<UserDto?> GetCurrentUserAsync(string cookieId, CancellationToken ct)
    {
        var session = await db.UserSessions
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.CookieId == cookieId, ct);

        if (session is null) return null;
        if (!IsSessionValid(session)) return null;

        session.LastSeenAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ToDto(session.User);
    }

    private static bool IsSessionValid(UserSession session)
    {
        var now = DateTime.UtcNow;
        if (session.RevokedAtUtc is not null) return false;
        if (now > session.ExpiresAtUtc) return false;
        if (now - session.LastSeenAtUtc > IdleTimeout) return false;
        if (!session.User.IsActive) return false;
        return true;
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        var combined = new byte[16 + 32];
        salt.CopyTo(combined, 0);
        hash.CopyTo(combined, 16);
        return Convert.ToBase64String(combined);
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        var combined = Convert.FromBase64String(storedHash);
        if (combined.Length != 48) return false;
        var salt = combined[..16];
        var expectedHash = combined[16..];
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    private static string GenerateCookieId()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    }

    private static UserDto ToDto(AgencyUser x) =>
        new(
            x.Id,
            x.Email,
            x.DisplayName,
            x.Role,
            x.IsActive,
            x.LastLoginAtUtc,
            x.CreatedAtUtc,
            x.UpdatedAtUtc);
}
