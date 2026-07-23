using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Users;

namespace DmarcAnalyzer.Api.Application.Users;

public interface IUserAdminService
{
    Task<IReadOnlyList<UserAdminDto>> ListAsync(CancellationToken ct);
    Task<ServiceResult<UserAdminDto>> CreateAsync(CreateUserRequest request, CancellationToken ct);
    Task<ServiceResult<UserAdminDto>> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct);
    Task<ServiceResult<UserAdminDto>> ReplaceGrantsAsync(Guid id, ReplaceUserGrantsRequest request, CancellationToken ct);
}
