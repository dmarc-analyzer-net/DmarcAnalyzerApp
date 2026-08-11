using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Contracts.ReportSources;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.ReportSources;

public sealed class ReportSourceService(DmarcAnalyzerDbContext db, ICredentialProtector credentialProtector) : IReportSourceService
{
    private static readonly string[] SupportedProtocols = ["imap", "pop3"];

    public async Task<IReadOnlyList<ReportSourceDto>> ListAsync(CancellationToken ct)
    {
        return await db.ReportSources
            .AsNoTracking()
            .Include(x => x.DefaultClient)
            .OrderBy(x => x.Name)
            .Select(x => ToDto(x, x.DefaultClient != null ? x.DefaultClient.Name : null))
            .ToListAsync(ct);
    }

    public async Task<ServiceResult<ReportSourceDto>> CreateAsync(CreateReportSourceRequest request, CancellationToken ct)
    {
        var protocol = request.Protocol.Trim().ToLowerInvariant();
        if (!SupportedProtocols.Contains(protocol))
        {
            return ServiceResult<ReportSourceDto>.Failure("protocol must be imap or pop3", 400);
        }

        if (string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Host) ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            request.Port <= 0 ||
            request.DefaultClientId == Guid.Empty)
        {
            return ServiceResult<ReportSourceDto>.Failure("name, host, port, username, password, and defaultClientId are required", 400);
        }

        var clientExists = await db.Clients.AnyAsync(x => x.Id == request.DefaultClientId, ct);
        if (!clientExists)
        {
            return ServiceResult<ReportSourceDto>.Failure("default client not found", 400);
        }

        var now = DateTime.UtcNow;
        var source = new ReportSource
        {
            Name = request.Name.Trim(),
            Protocol = protocol,
            Host = request.Host.Trim().ToLowerInvariant(),
            Port = request.Port,
            UseTls = request.UseTls,
            Username = request.Username.Trim(),
            PasswordEncrypted = credentialProtector.Protect(request.Password),
            DefaultClientId = request.DefaultClientId,
            IsActive = request.IsActive,
            DeleteAfterRetention = request.DeleteAfterRetention,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.ReportSources.Add(source);
        await db.SaveChangesAsync(ct);

        return ServiceResult<ReportSourceDto>.Success(ToDto(source, null));
    }

    public async Task<ServiceResult<ReportSourceDto>> UpdateAsync(Guid id, UpdateReportSourceRequest request, CancellationToken ct)
    {
        var source = await db.ReportSources.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (source is null)
        {
            return ServiceResult<ReportSourceDto>.Failure("not found", 404);
        }

        if (request.Protocol is not null)
        {
            var protocol = request.Protocol.Trim().ToLowerInvariant();
            if (!SupportedProtocols.Contains(protocol))
            {
                return ServiceResult<ReportSourceDto>.Failure("protocol must be imap or pop3", 400);
            }

            source.Protocol = protocol;
        }

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ServiceResult<ReportSourceDto>.Failure("name cannot be empty", 400);
            }

            source.Name = request.Name.Trim();
        }

        if (request.Host is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Host))
            {
                return ServiceResult<ReportSourceDto>.Failure("host cannot be empty", 400);
            }

            source.Host = request.Host.Trim().ToLowerInvariant();
        }

        if (request.Port.HasValue)
        {
            if (request.Port.Value <= 0)
            {
                return ServiceResult<ReportSourceDto>.Failure("port must be greater than 0", 400);
            }

            source.Port = request.Port.Value;
        }

        if (request.Username is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return ServiceResult<ReportSourceDto>.Failure("username cannot be empty", 400);
            }

            source.Username = request.Username.Trim();
        }

        if (request.Password is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Password))
            {
                return ServiceResult<ReportSourceDto>.Failure("password cannot be empty", 400);
            }

            source.PasswordEncrypted = credentialProtector.Protect(request.Password);
        }

        if (request.DefaultClientId.HasValue)
        {
            if (request.DefaultClientId.Value == Guid.Empty)
            {
                return ServiceResult<ReportSourceDto>.Failure("defaultClientId cannot be empty", 400);
            }

            var clientExists = await db.Clients.AnyAsync(x => x.Id == request.DefaultClientId.Value, ct);
            if (!clientExists)
            {
                return ServiceResult<ReportSourceDto>.Failure("default client not found", 400);
            }

            source.DefaultClientId = request.DefaultClientId.Value;
        }

        if (request.UseTls.HasValue)
        {
            source.UseTls = request.UseTls.Value;
        }

        if (request.IsActive.HasValue)
        {
            source.IsActive = request.IsActive.Value;
        }

        if (request.DeleteAfterRetention.HasValue)
        {
            source.DeleteAfterRetention = request.DeleteAfterRetention.Value;
        }

        source.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ServiceResult<ReportSourceDto>.Success(ToDto(source, null));
    }

    private static ReportSourceDto ToDto(ReportSource x, string? defaultClientName) =>
        new(
            x.Id,
            x.Name,
            x.Protocol,
            x.Host,
            x.Port,
            x.UseTls,
            x.Username,
            x.DefaultClientId,
            defaultClientName,
            x.IsActive,
            x.DeleteAfterRetention,
            x.OldestMessageAtUtc,
            x.LastSuccessSyncAtUtc,
            x.LastProcessedUid,
            x.LastProcessedUidValidity,
            x.CreatedAtUtc,
            x.UpdatedAtUtc);
}
