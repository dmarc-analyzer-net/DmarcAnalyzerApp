using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Contracts.Clients;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Clients;

public sealed class ClientService(DmarcAnalyzerDbContext db) : IClientService
{
    public async Task<IReadOnlyList<ClientDto>> ListAsync(CancellationToken ct)
    {
        var clients = await db.Clients
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        return clients.Select(ToDto).ToList();
    }

    public async Task<ClientDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var client = await db.Clients
            .AsNoTracking()
            .Where(x => x.Id == id)
            .SingleOrDefaultAsync(ct);

        return client is null ? null : ToDto(client);
    }

    public async Task<ServiceResult<ClientDto>> CreateAsync(CreateClientRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Slug))
        {
            return ServiceResult<ClientDto>.Failure("name and slug are required", 400);
        }

        var normalizedSlug = request.Slug.Trim().ToLowerInvariant();
        var slugInUse = await db.Clients.AnyAsync(x => x.Slug == normalizedSlug, ct);
        if (slugInUse)
        {
            return ServiceResult<ClientDto>.Failure("client slug already exists", 409);
        }

        var now = DateTime.UtcNow;
        var client = new Client
        {
            Name = request.Name.Trim(),
            Slug = normalizedSlug,
            IsActive = request.IsActive,
            RetentionMonths = request.RetentionMonths <= 0 ? 27 : request.RetentionMonths,
            Timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "UTC" : request.Timezone.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.Clients.Add(client);
        await db.SaveChangesAsync(ct);

        return ServiceResult<ClientDto>.Success(ToDto(client));
    }

    public async Task<ServiceResult<ClientDto>> UpdateAsync(Guid id, UpdateClientRequest request, CancellationToken ct)
    {
        var client = await db.Clients.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (client is null)
        {
            return ServiceResult<ClientDto>.Failure("not found", 404);
        }

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ServiceResult<ClientDto>.Failure("name cannot be empty", 400);
            }

            client.Name = request.Name.Trim();
        }

        if (request.Slug is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Slug))
            {
                return ServiceResult<ClientDto>.Failure("slug cannot be empty", 400);
            }

            var normalizedSlug = request.Slug.Trim().ToLowerInvariant();
            var slugInUse = await db.Clients.AnyAsync(x => x.Id != id && x.Slug == normalizedSlug, ct);
            if (slugInUse)
            {
                return ServiceResult<ClientDto>.Failure("client slug already exists", 409);
            }

            client.Slug = normalizedSlug;
        }

        if (request.RetentionMonths.HasValue)
        {
            if (request.RetentionMonths.Value <= 0)
            {
                return ServiceResult<ClientDto>.Failure("retentionMonths must be greater than 0", 400);
            }

            client.RetentionMonths = request.RetentionMonths.Value;
        }

        if (request.Timezone is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Timezone))
            {
                return ServiceResult<ClientDto>.Failure("timezone cannot be empty", 400);
            }

            client.Timezone = request.Timezone.Trim();
        }

        if (request.IsActive.HasValue)
        {
            client.IsActive = request.IsActive.Value;
        }

        client.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ServiceResult<ClientDto>.Success(ToDto(client));
    }

    private static ClientDto ToDto(Client x) =>
        new(
            x.Id,
            x.Name,
            x.Slug,
            x.IsActive,
            x.RetentionMonths,
            x.Timezone,
            x.CreatedAtUtc,
            x.UpdatedAtUtc);
}
