using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.ApiCredentials;

/// <summary>What a credential looks like once issued. Never carries the secret.</summary>
public sealed record ApiCredentialDto(
    Guid Id,
    string Name,
    string Kind,
    Guid? ReportSourceId,
    string? ReportSourceName,
    string TokenId,
    DateTime CreatedAtUtc,
    DateTime? LastUsedAtUtc,
    DateTime? ExpiresAtUtc,
    DateTime? RevokedAtUtc,
    bool IsUsable);

/// <summary>
/// The one and only response that carries the token. <see cref="Token"/> is not stored
/// anywhere and cannot be recovered — losing it means issuing a new credential.
/// </summary>
public sealed record IssuedApiCredentialDto(ApiCredentialDto Credential, string Token);

public interface IApiCredentialService
{
    Task<IReadOnlyList<ApiCredentialDto>> ListAsync(Guid? reportSourceId, CancellationToken ct);
    Task<ServiceResult<IssuedApiCredentialDto>> IssueAsync(
        Guid reportSourceId, string name, DateTime? expiresAtUtc, Guid? createdByUserId, CancellationToken ct);
    Task<ServiceResult<ApiCredentialDto>> RevokeAsync(Guid id, CancellationToken ct);
}

public sealed class ApiCredentialService(DmarcAnalyzerDbContext db) : IApiCredentialService
{
    public async Task<IReadOnlyList<ApiCredentialDto>> ListAsync(Guid? reportSourceId, CancellationToken ct)
    {
        var query = db.ApiCredentials.AsNoTracking();
        if (reportSourceId.HasValue)
        {
            query = query.Where(x => x.ReportSourceId == reportSourceId.Value);
        }

        var rows = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new
            {
                x.Id, x.Name, x.Kind, x.ReportSourceId,
                ReportSourceName = x.ReportSource!.Name,
                x.TokenId, x.CreatedAtUtc, x.LastUsedAtUtc, x.ExpiresAtUtc, x.RevokedAtUtc,
            })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        return [.. rows.Select(x => new ApiCredentialDto(
            x.Id, x.Name, x.Kind, x.ReportSourceId, x.ReportSourceName, x.TokenId,
            x.CreatedAtUtc, x.LastUsedAtUtc, x.ExpiresAtUtc, x.RevokedAtUtc,
            x.RevokedAtUtc is null && (x.ExpiresAtUtc is null || x.ExpiresAtUtc > now)))];
    }

    public async Task<ServiceResult<IssuedApiCredentialDto>> IssueAsync(
        Guid reportSourceId, string name, DateTime? expiresAtUtc, Guid? createdByUserId, CancellationToken ct)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return ServiceResult<IssuedApiCredentialDto>.Failure("name is required", 400);
        }

        var source = await db.ReportSources
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reportSourceId, ct);

        if (source is null)
        {
            return ServiceResult<IssuedApiCredentialDto>.Failure("report source not found", 404);
        }

        if (expiresAtUtc is not null && expiresAtUtc <= DateTime.UtcNow)
        {
            return ServiceResult<IssuedApiCredentialDto>.Failure("expiry must be in the future", 400);
        }

        var issued = MachineToken.Create();
        var credential = new ApiCredential
        {
            Name = trimmed,
            Kind = MachineCredentialKinds.ReportIngest,
            ReportSourceId = source.Id,
            TokenId = issued.TokenId,
            TokenHash = issued.Hash,
            CreatedByUserId = createdByUserId,
            ExpiresAtUtc = expiresAtUtc,
        };

        db.ApiCredentials.Add(credential);
        await db.SaveChangesAsync(ct);

        var dto = new ApiCredentialDto(
            credential.Id, credential.Name, credential.Kind, source.Id, source.Name,
            credential.TokenId, credential.CreatedAtUtc, null, credential.ExpiresAtUtc, null, true);

        // The only time the token leaves this process.
        return ServiceResult<IssuedApiCredentialDto>.Success(new IssuedApiCredentialDto(dto, issued.Presented));
    }

    public async Task<ServiceResult<ApiCredentialDto>> RevokeAsync(Guid id, CancellationToken ct)
    {
        var credential = await db.ApiCredentials
            .Include(x => x.ReportSource)
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (credential is null)
        {
            return ServiceResult<ApiCredentialDto>.Failure("not found", 404);
        }

        // Idempotent on purpose. Revoking is what an operator does in a hurry, possibly
        // twice, possibly from two windows — a second attempt reporting an error would be
        // alarming and would tell them nothing useful.
        credential.RevokedAtUtc ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ServiceResult<ApiCredentialDto>.Success(new ApiCredentialDto(
            credential.Id, credential.Name, credential.Kind, credential.ReportSourceId,
            credential.ReportSource?.Name, credential.TokenId, credential.CreatedAtUtc,
            credential.LastUsedAtUtc, credential.ExpiresAtUtc, credential.RevokedAtUtc, false));
    }
}
