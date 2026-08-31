using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Users;

namespace DmarcAnalyzer.Api.Application.Users;

/// <summary>User administration (admin-only endpoints). The service also guards against locking out the last admin.</summary>
public interface IUserAdminService
{
    /// <summary>Every user with their grants, by email.</summary>
    Task<IReadOnlyList<UserAdminDto>> ListAsync(CancellationToken ct);

    /// <summary>Creates a user with a role and, for client viewers, initial grants.</summary>
    Task<ServiceResult<UserAdminDto>> CreateAsync(CreateUserRequest request, CancellationToken ct);

    /// <summary>Partial update: profile, role, active flag, or password reset.</summary>
    Task<ServiceResult<UserAdminDto>> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct);

    /// <summary>Replaces the user's client grants with exactly this set.</summary>
    Task<ServiceResult<UserAdminDto>> ReplaceGrantsAsync(Guid id, ReplaceUserGrantsRequest request, CancellationToken ct);

    /// <summary>Deletes a user; their sessions, identities, and grants go with them.</summary>
    Task<ServiceResult<UserAdminDto>> DeleteAsync(Guid id, CancellationToken ct);
}
