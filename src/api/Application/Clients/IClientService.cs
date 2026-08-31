using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Clients;

namespace DmarcAnalyzer.Api.Application.Clients;

/// <summary>Client (tenant) CRUD. Reads are scoped to the caller; there is deliberately no delete.</summary>
public interface IClientService
{
    /// <summary>Clients visible to the caller — all for staff, granted ones for viewers.</summary>
    Task<IReadOnlyList<ClientDto>> ListAsync(CancellationToken ct);

    /// <summary>One client, or null for unknown and cross-tenant ids alike.</summary>
    Task<ClientDto?> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Creates a client; the slug must be unique.</summary>
    Task<ServiceResult<ClientDto>> CreateAsync(CreateClientRequest request, CancellationToken ct);

    /// <summary>Partial update — only the request's non-null fields change.</summary>
    Task<ServiceResult<ClientDto>> UpdateAsync(Guid id, UpdateClientRequest request, CancellationToken ct);
}
