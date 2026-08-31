using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Domains;

namespace DmarcAnalyzer.Api.Application.Domains;

/// <summary>Domain CRUD. Domain names are globally unique — creation under a second client is a conflict.</summary>
public interface IDomainService
{
    /// <summary>Domains visible to the caller, optionally only one client's.</summary>
    Task<IReadOnlyList<DomainDto>> ListAsync(Guid? clientId, CancellationToken ct);

    /// <summary>One domain, or null for unknown and cross-tenant ids alike.</summary>
    Task<DomainDto?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Creates a domain under a client; the name must not exist anywhere.</summary>
    Task<ServiceResult<DomainDto>> CreateAsync(CreateDomainRequest request, CancellationToken ct);

    /// <summary>Partial update — rename, active flag, or owning client.</summary>
    Task<ServiceResult<DomainDto>> UpdateAsync(Guid id, UpdateDomainRequest request, CancellationToken ct);
}
