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

    public async Task<bool> RequiresBootstrapAsync(CancellationToken ct)
        => !await db.AgencyUsers.AnyAsync(ct);

    public async Task<ServiceResult<UserDto>> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        // Registration only bootstraps the very first account (forced admin);
        // afterwards users are created by admins via the users endpoints.
        if (!await RequiresBootstrapAsync(ct))
        {
            return ServiceResult<UserDto>.Failure("registration is disabled; ask an administrator to create your account", 403);
        }

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

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();

        var now = DateTime.UtcNow;
        var user = new AgencyUser
        {
            Email = normalizedEmail,
            PasswordHash = PasswordHasher.Hash(request.Password),
            DisplayName = request.DisplayName.Trim(),
            Role = Roles.AgencyAdmin,
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

        if (user is null || !PasswordHasher.Verify(request.Password, user.PasswordHash))
        {
            return ServiceResult<LoginResultDto>.Failure("invalid credentials", 401);
        }

        return await LoginWithExternalIdentityAsync(user.Id, ipAddress, userAgent, ct);
    }

    public async Task<ServiceResult<LoginResultDto>> LoginWithExternalIdentityAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        var user = await db.AgencyUsers.SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null)
        {
            return ServiceResult<LoginResultDto>.Failure("user not found", 404);
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
        => (await GetSessionUserAsync(cookieId, ct))?.User;

    public async Task<SessionUserDto?> GetSessionUserAsync(string cookieId, CancellationToken ct)
    {
        var session = await db.UserSessions
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.CookieId == cookieId, ct);

        if (session is null) return null;
        if (!IsSessionValid(session)) return null;

        session.LastSeenAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Grants only constrain client_viewer; staff are unrestricted.
        IReadOnlyList<Guid> grantedClientIds = [];
        if (!Roles.IsAgencyStaff(session.User.Role))
        {
            grantedClientIds = await db.UserClientGrants
                .AsNoTracking()
                .Where(x => x.UserId == session.UserId)
                .Select(x => x.ClientId)
                .ToListAsync(ct);
        }

        return new SessionUserDto(ToDto(session.User), grantedClientIds);
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
