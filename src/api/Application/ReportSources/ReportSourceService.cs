using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Contracts.ReportSources;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.ReportSources;

public sealed class ReportSourceService(DmarcAnalyzerDbContext db, ICredentialProtector credentialProtector) : IReportSourceService
{
    /// <summary>
    /// What a source may actually be, not what it might one day be. Every value here is
    /// read by something: <c>imap</c> and <c>pop3</c> by the polling worker, <c>api</c> by
    /// the ingestion endpoint.
    /// <para>
    /// <c>pop3</c> is here on its second attempt. It was accepted for a long time and never
    /// worked — the worker polled <c>Protocol == "imap"</c> and manual sync refused anything
    /// else, so a POP3 source could be created, would appear in the console, and would
    /// silently never ingest a single report — and was removed on the principle that an
    /// option that does nothing is worse than an absent one. It is back because the code
    /// that reads it now exists: <c>Pop3MailboxTransport</c>, the same drain, the same run
    /// rows and the same retention deletion as IMAP. Rows predating the removal start
    /// syncing on the next pass, which is what they were always meant to do.
    /// </para>
    /// <para>
    /// The rule the round trip is worth remembering for: add a value here in the same change
    /// as the code that acts on it, never before.
    /// </para>
    /// </summary>
    private static readonly string[] SupportedProtocols =
        [ReportSourceProtocols.Imap, ReportSourceProtocols.Pop3, ReportSourceProtocols.Api];

    private const string ProtocolError = "protocol must be imap, pop3 or api";

    /// <summary>
    /// An API source is pushed to, so it has no host, port, mailbox or password. Those
    /// columns stay NOT NULL and hold empty values rather than becoming nullable across
    /// every reader — the trade is recorded in the create path below.
    /// </summary>
    private static bool IsPushed(string protocol) => protocol == ReportSourceProtocols.Api;

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
            return ServiceResult<ReportSourceDto>.Failure(ProtocolError, 400);
        }

        var pushed = IsPushed(protocol);

        if (string.IsNullOrWhiteSpace(request.Name) || request.DefaultClientId == Guid.Empty)
        {
            return ServiceResult<ReportSourceDto>.Failure("name and defaultClientId are required", 400);
        }

        if (!pushed && (string.IsNullOrWhiteSpace(request.Host) ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            request.Port <= 0))
        {
            return ServiceResult<ReportSourceDto>.Failure("name, host, port, username, password, and defaultClientId are required", 400);
        }

        // Refused rather than ignored. Accepting mailbox settings on a source that will
        // never connect to a mailbox would leave a password sitting in the database that
        // nothing will ever use and nobody will remember is there.
        if (pushed && (!string.IsNullOrWhiteSpace(request.Host) ||
            !string.IsNullOrWhiteSpace(request.Username) ||
            !string.IsNullOrWhiteSpace(request.Password)))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "an api source is pushed to and takes no host, username or password", 400);
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
            // Empty rather than null for a pushed source: the columns are NOT NULL and
            // making them nullable would mean a migration plus a null check at every reader
            // and display site. Empty means not applicable, and Protocol is what says so.
            Host = pushed ? string.Empty : request.Host.Trim().ToLowerInvariant(),
            Port = pushed ? 0 : request.Port,
            UseTls = !pushed && request.UseTls,
            Username = pushed ? string.Empty : request.Username.Trim(),
            PasswordEncrypted = pushed ? string.Empty : credentialProtector.Protect(request.Password),
            DefaultClientId = request.DefaultClientId,
            IsActive = request.IsActive,
            DeleteAfterRetention = request.DeleteAfterRetention,
            AllowForeignDomains = request.AllowForeignDomains,
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

        if (request.AllowForeignDomains is not null)
        {
            source.AllowForeignDomains = request.AllowForeignDomains.Value;
        }

        if (request.Protocol is not null)
        {
            var protocol = request.Protocol.Trim().ToLowerInvariant();

            // Unchanged is always allowed, even when the value is no longer one that can
            // be created. Nothing in SupportedProtocols is in that position today — pop3 is
            // back — but a row holding a retired value would otherwise be uneditable: every
            // save resends its own protocol and would be refused, so an operator could not
            // even rename the source, let alone move it to a protocol that works. Only a
            // *change* has to land on something supported.
            var unchanged = string.Equals(protocol, source.Protocol, StringComparison.Ordinal);
            if (!unchanged && !SupportedProtocols.Contains(protocol))
            {
                return ServiceResult<ReportSourceDto>.Failure(ProtocolError, 400);
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
            x.AllowForeignDomains,
            x.OldestMessageAtUtc,
            x.LastSuccessSyncAtUtc,
            x.LastProcessedUid,
            x.LastProcessedUidValidity,
            x.LastProcessedUidl,
            x.CreatedAtUtc,
            x.UpdatedAtUtc);
}
