using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Clients;

namespace DmarcAnalyzer.Api.Application.Clients;

public interface IClientService
{
    Task<IReadOnlyList<ClientDto>> ListAsync(CancellationToken ct);
    Task<ClientDto?> GetAsync(Guid id, CancellationToken ct);
    Task<ServiceResult<ClientDto>> CreateAsync(CreateClientRequest request, CancellationToken ct);

    /// <summary>
    /// Creates the catch-all default client, or returns null if this install already has
    /// clients and therefore needs no such thing.
    /// </summary>
    Task<ClientDto?> EnsureDefaultAsync(CancellationToken ct);
    Task<ServiceResult<ClientDto>> UpdateAsync(Guid id, UpdateClientRequest request, CancellationToken ct);
}
