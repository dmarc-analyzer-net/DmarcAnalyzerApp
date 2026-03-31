using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Domains;

namespace DmarcAnalyzer.Api.Application.Domains;

public interface IDomainService
{
    Task<IReadOnlyList<DomainDto>> ListAsync(Guid? clientId, CancellationToken ct);
    Task<DomainDto?> GetAsync(Guid id, CancellationToken ct);
    Task<ServiceResult<DomainDto>> CreateAsync(CreateDomainRequest request, CancellationToken ct);
    Task<ServiceResult<DomainDto>> UpdateAsync(Guid id, UpdateDomainRequest request, CancellationToken ct);
}
