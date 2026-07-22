using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Contracts.MailboxSources;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.MailboxSources;

public sealed class MailboxSourceService(DmarcAnalyzerDbContext db, ICredentialProtector credentialProtector) : IMailboxSourceService
{
    private static readonly string[] SupportedProtocols = ["imap", "pop3"];

    public async Task<IReadOnlyList<MailboxSourceDto>> ListAsync(CancellationToken ct)
    {
        return await db.MailboxSources
            .AsNoTracking()
            .Include(x => x.DefaultClient)
            .OrderBy(x => x.Name)
            .Select(x => ToDto(x, x.DefaultClient != null ? x.DefaultClient.Name : null))
            .ToListAsync(ct);
    }

    public async Task<ServiceResult<MailboxSourceDto>> CreateAsync(CreateMailboxSourceRequest request, CancellationToken ct)
    {
        var protocol = request.Protocol.Trim().ToLowerInvariant();
        if (!SupportedProtocols.Contains(protocol))
        {
            return ServiceResult<MailboxSourceDto>.Failure("protocol must be imap or pop3", 400);
        }

        if (string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Host) ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            request.Port <= 0 ||
            request.DefaultClientId == Guid.Empty)
        {
            return ServiceResult<MailboxSourceDto>.Failure("name, host, port, username, password, and defaultClientId are required", 400);
        }

        var clientExists = await db.Clients.AnyAsync(x => x.Id == request.DefaultClientId, ct);
        if (!clientExists)
        {
            return ServiceResult<MailboxSourceDto>.Failure("default client not found", 400);
        }

        var now = DateTime.UtcNow;
        var source = new MailboxSource
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
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.MailboxSources.Add(source);
        await db.SaveChangesAsync(ct);

        return ServiceResult<MailboxSourceDto>.Success(ToDto(source, null));
    }

    public async Task<ServiceResult<MailboxSourceDto>> UpdateAsync(Guid id, UpdateMailboxSourceRequest request, CancellationToken ct)
    {
        var source = await db.MailboxSources.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (source is null)
        {
            return ServiceResult<MailboxSourceDto>.Failure("not found", 404);
        }

        if (request.Protocol is not null)
        {
            var protocol = request.Protocol.Trim().ToLowerInvariant();
            if (!SupportedProtocols.Contains(protocol))
            {
                return ServiceResult<MailboxSourceDto>.Failure("protocol must be imap or pop3", 400);
            }

            source.Protocol = protocol;
        }

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return ServiceResult<MailboxSourceDto>.Failure("name cannot be empty", 400);
            }

            source.Name = request.Name.Trim();
        }

        if (request.Host is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Host))
            {
                return ServiceResult<MailboxSourceDto>.Failure("host cannot be empty", 400);
            }

            source.Host = request.Host.Trim().ToLowerInvariant();
        }

        if (request.Port.HasValue)
        {
            if (request.Port.Value <= 0)
            {
                return ServiceResult<MailboxSourceDto>.Failure("port must be greater than 0", 400);
            }

            source.Port = request.Port.Value;
        }

        if (request.Username is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Username))
            {
                return ServiceResult<MailboxSourceDto>.Failure("username cannot be empty", 400);
            }

            source.Username = request.Username.Trim();
        }

        if (request.Password is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Password))
            {
                return ServiceResult<MailboxSourceDto>.Failure("password cannot be empty", 400);
            }

            source.PasswordEncrypted = credentialProtector.Protect(request.Password);
        }

        if (request.DefaultClientId.HasValue)
        {
            if (request.DefaultClientId.Value == Guid.Empty)
            {
                return ServiceResult<MailboxSourceDto>.Failure("defaultClientId cannot be empty", 400);
            }

            var clientExists = await db.Clients.AnyAsync(x => x.Id == request.DefaultClientId.Value, ct);
            if (!clientExists)
            {
                return ServiceResult<MailboxSourceDto>.Failure("default client not found", 400);
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

        source.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ServiceResult<MailboxSourceDto>.Success(ToDto(source, null));
    }

    private static MailboxSourceDto ToDto(MailboxSource x, string? defaultClientName) =>
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
            x.LastSuccessSyncAtUtc,
            x.LastProcessedUid,
            x.LastProcessedUidValidity,
            x.CreatedAtUtc,
            x.UpdatedAtUtc);
}
