using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Domains;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Domains;

public sealed class DomainService(DmarcAnalyzerDbContext db, ICurrentUserContext currentUser) : IDomainService
{
    public async Task<IReadOnlyList<DomainDto>> ListAsync(Guid? clientId, CancellationToken ct)
    {
        var query = db.Domains
            .AsNoTracking()
            .Include(x => x.Client)
            .AsQueryable();

        if (!currentUser.IsAgencyStaff)
        {
            var allowed = currentUser.AllowedClientIds;
            query = query.Where(x => allowed.Contains(x.ClientId));
        }

        if (clientId.HasValue)
        {
            query = query.Where(x => x.ClientId == clientId.Value);
        }

        return await query
            .OrderBy(x => x.Name)
            .Select(x => ToDto(x, x.Client != null ? x.Client.Name : null))
            .ToListAsync(ct);
    }

    public async Task<DomainDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var query = db.Domains
            .AsNoTracking()
            .Include(x => x.Client)
            .Where(x => x.Id == id);

        if (!currentUser.IsAgencyStaff)
        {
            var allowed = currentUser.AllowedClientIds;
            query = query.Where(x => allowed.Contains(x.ClientId));
        }

        return await query
            .Select(x => ToDto(x, x.Client != null ? x.Client.Name : null))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<ServiceResult<DomainDto>> CreateAsync(CreateDomainRequest request, CancellationToken ct)
    {
        if (request.ClientId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name))
        {
            return ServiceResult<DomainDto>.Failure("clientId and name are required", 400);
        }

        var clientExists = await db.Clients.AnyAsync(x => x.Id == request.ClientId, ct);
        if (!clientExists)
        {
            return ServiceResult<DomainDto>.Failure("client not found", 400);
        }

        var normalizedName = request.Name.Trim().ToLowerInvariant();
        var domainExists = await db.Domains.AnyAsync(x => x.Name == normalizedName, ct);
        if (domainExists)
        {
            return ServiceResult<DomainDto>.Failure("domain already exists", 409);
        }

        var now = DateTime.UtcNow;
        var domain = new Domain
        {
            ClientId = request.ClientId,
            Name = normalizedName,
            IsActive = request.IsActive,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.Domains.Add(domain);
        await db.SaveChangesAsync(ct);

        return ServiceResult<DomainDto>.Success(ToDto(domain, null));
    }

    public async Task<ServiceResult<DomainDto>> UpdateAsync(Guid id, UpdateDomainRequest request, CancellationToken ct)
    {
        var domain = await db.Domains.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (domain is null)
        {
            return ServiceResult<DomainDto>.Failure("not found", 404);
        }

        if (request.ClientId.HasValue)
        {
            if (request.ClientId.Value == Guid.Empty)
            {
                return ServiceResult<DomainDto>.Failure("clientId cannot be empty", 400);
            }

            var clientExists = await db.Clients.AnyAsync(x => x.Id == request.ClientId.Value, ct);
            if (!clientExists)
            {
                return ServiceResult<DomainDto>.Failure("client not found", 400);
            }

            domain.ClientId = request.ClientId.Value;
        }

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ServiceResult<DomainDto>.Failure("name cannot be empty", 400);
            }

            var normalizedName = request.Name.Trim().ToLowerInvariant();
            var domainExists = await db.Domains.AnyAsync(x => x.Id != id && x.Name == normalizedName, ct);
            if (domainExists)
            {
                return ServiceResult<DomainDto>.Failure("domain already exists", 409);
            }

            domain.Name = normalizedName;
        }

        if (request.IsActive.HasValue)
        {
            domain.IsActive = request.IsActive.Value;
        }

        domain.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ServiceResult<DomainDto>.Success(ToDto(domain, null));
    }

    private static DomainDto ToDto(Domain x, string? clientName) =>
        new(
            x.Id,
            x.Name,
            x.IsActive,
            x.ClientId,
            clientName,
            x.CreatedAtUtc,
            x.UpdatedAtUtc);
}
