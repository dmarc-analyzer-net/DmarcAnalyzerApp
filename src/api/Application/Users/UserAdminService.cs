using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Users;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Users;

public sealed class UserAdminService(DmarcAnalyzerDbContext db, ICurrentUserContext currentUser) : IUserAdminService
{
    public async Task<IReadOnlyList<UserAdminDto>> ListAsync(CancellationToken ct)
    {
        var users = await db.AgencyUsers
            .AsNoTracking()
            .OrderBy(x => x.Email)
            .ToListAsync(ct);

        var grants = await db.UserClientGrants
            .AsNoTracking()
            .Select(x => new { x.UserId, x.ClientId })
            .ToListAsync(ct);
        var grantsByUser = grants
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(x => x.ClientId).ToList());

        return users
            .Select(u => ToDto(u, grantsByUser.TryGetValue(u.Id, out var g) ? g : []))
            .ToArray();
    }

    public async Task<ServiceResult<UserAdminDto>> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return ServiceResult<UserAdminDto>.Failure("email, password, and displayName are required", 400);
        }

        if (request.Password.Length < 10)
        {
            return ServiceResult<UserAdminDto>.Failure("password must be at least 10 characters", 400);
        }

        var role = request.Role.Trim().ToLowerInvariant();
        if (!Roles.IsValid(role))
        {
            return ServiceResult<UserAdminDto>.Failure("role must be agency_admin, agency_analyst, or client_viewer", 400);
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        if (await db.AgencyUsers.AnyAsync(x => x.Email == normalizedEmail, ct))
        {
            return ServiceResult<UserAdminDto>.Failure("email already registered", 409);
        }

        var now = DateTime.UtcNow;
        var user = new AgencyUser
        {
            Email = normalizedEmail,
            PasswordHash = PasswordHasher.Hash(request.Password),
            DisplayName = request.DisplayName.Trim(),
            Role = role,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.AgencyUsers.Add(user);
        await db.SaveChangesAsync(ct);

        return ServiceResult<UserAdminDto>.Success(ToDto(user, []));
    }

    public async Task<ServiceResult<UserAdminDto>> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        var user = await db.AgencyUsers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is null)
        {
            return ServiceResult<UserAdminDto>.Failure("user not found", 404);
        }

        var demotingAdmin = request.Role is not null &&
                            user.Role == Roles.AgencyAdmin &&
                            request.Role.Trim().ToLowerInvariant() != Roles.AgencyAdmin;
        var deactivatingAdmin = request.IsActive == false && user.IsActive && user.Role == Roles.AgencyAdmin;

        if ((demotingAdmin || deactivatingAdmin) && await IsLastActiveAdminAsync(user.Id, ct))
        {
            return ServiceResult<UserAdminDto>.Failure("cannot demote or deactivate the last active administrator", 409);
        }

        if (request.DisplayName is not null)
        {
            if (string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return ServiceResult<UserAdminDto>.Failure("displayName cannot be empty", 400);
            }

            user.DisplayName = request.DisplayName.Trim();
        }

        if (request.Role is not null)
        {
            var role = request.Role.Trim().ToLowerInvariant();
            if (!Roles.IsValid(role))
            {
                return ServiceResult<UserAdminDto>.Failure("role must be agency_admin, agency_analyst, or client_viewer", 400);
            }

            // Grants are only meaningful for client_viewer; drop them on promotion to staff.
            if (Roles.IsAgencyStaff(role) && user.Role == Roles.ClientViewer)
            {
                var grants = await db.UserClientGrants.Where(x => x.UserId == user.Id).ToListAsync(ct);
                db.UserClientGrants.RemoveRange(grants);
            }

            user.Role = role;
        }

        if (request.Password is not null)
        {
            if (request.Password.Length < 10)
            {
                return ServiceResult<UserAdminDto>.Failure("password must be at least 10 characters", 400);
            }

            user.PasswordHash = PasswordHasher.Hash(request.Password);
        }

        if (request.IsActive.HasValue && request.IsActive.Value != user.IsActive)
        {
            user.IsActive = request.IsActive.Value;

            if (!user.IsActive)
            {
                var openSessions = await db.UserSessions
                    .Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
                    .ToListAsync(ct);
                foreach (var session in openSessions)
                {
                    session.RevokedAtUtc = DateTime.UtcNow;
                }
            }
        }

        user.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ServiceResult<UserAdminDto>.Success(ToDto(user, await GrantsForAsync(user.Id, ct)));
    }

    public async Task<ServiceResult<UserAdminDto>> ReplaceGrantsAsync(Guid id, ReplaceUserGrantsRequest request, CancellationToken ct)
    {
        var user = await db.AgencyUsers.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (user is null)
        {
            return ServiceResult<UserAdminDto>.Failure("user not found", 404);
        }

        var requestedIds = request.ClientIds.Distinct().ToList();

        if (user.Role != Roles.ClientViewer && requestedIds.Count > 0)
        {
            return ServiceResult<UserAdminDto>.Failure("client grants only apply to client_viewer users", 400);
        }

        var validCount = await db.Clients.CountAsync(x => requestedIds.Contains(x.Id), ct);
        if (validCount != requestedIds.Count)
        {
            return ServiceResult<UserAdminDto>.Failure("one or more client ids do not exist", 400);
        }

        var existing = await db.UserClientGrants.Where(x => x.UserId == user.Id).ToListAsync(ct);
        db.UserClientGrants.RemoveRange(existing.Where(x => !requestedIds.Contains(x.ClientId)));

        var existingIds = existing.Select(x => x.ClientId).ToHashSet();
        var now = DateTime.UtcNow;
        foreach (var clientId in requestedIds.Where(x => !existingIds.Contains(x)))
        {
            db.UserClientGrants.Add(new UserClientGrant
            {
                UserId = user.Id,
                ClientId = clientId,
                CreatedAtUtc = now,
                CreatedByUserId = currentUser.IsAuthenticated ? currentUser.UserId : null,
            });
        }

        await db.SaveChangesAsync(ct);

        return ServiceResult<UserAdminDto>.Success(ToDto(user, requestedIds));
    }

    private async Task<bool> IsLastActiveAdminAsync(Guid userId, CancellationToken ct)
        => !await db.AgencyUsers.AnyAsync(
            x => x.Id != userId && x.Role == Roles.AgencyAdmin && x.IsActive, ct);

    private async Task<IReadOnlyList<Guid>> GrantsForAsync(Guid userId, CancellationToken ct)
        => await db.UserClientGrants
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.ClientId)
            .ToListAsync(ct);

    private static UserAdminDto ToDto(AgencyUser x, IReadOnlyList<Guid> grantedClientIds) =>
        new(
            x.Id,
            x.Email,
            x.DisplayName,
            x.Role,
            x.IsActive,
            x.LastLoginAtUtc,
            x.CreatedAtUtc,
            x.UpdatedAtUtc,
            grantedClientIds);
}
