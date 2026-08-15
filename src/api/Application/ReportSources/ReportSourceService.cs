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
    /// read by something: <c>imap</c>, <c>pop3</c> and <c>s3</c> by the polling worker,
    /// <c>api</c> by the ingestion endpoint.
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
    [
        ReportSourceProtocols.Imap,
        ReportSourceProtocols.Pop3,
        ReportSourceProtocols.S3,
        ReportSourceProtocols.Api,
    ];

    private const string ProtocolError = "protocol must be imap, pop3, s3 or api";

    /// <summary>
    /// An API source is pushed to, so it has no host, port, mailbox or password. Those
    /// columns stay NOT NULL and hold empty values rather than becoming nullable across
    /// every reader — the trade is recorded in the create path below.
    /// </summary>
    private static bool IsPushed(string protocol) => protocol == ReportSourceProtocols.Api;

    /// <summary>
    /// An S3 source is polled, but not over a mailbox: it has a bucket and a region where the
    /// mail protocols have a host and a port, and it may legitimately carry no credential at
    /// all when the ambient chain supplies one.
    /// </summary>
    private static bool IsBucket(string protocol) => protocol == ReportSourceProtocols.S3;

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
        var bucket = IsBucket(protocol);
        var mailbox = ReportSourceProtocols.IsMailbox(protocol);

        if (string.IsNullOrWhiteSpace(request.Name) || request.DefaultClientId == Guid.Empty)
        {
            return ServiceResult<ReportSourceDto>.Failure("name and defaultClientId are required", 400);
        }

        if (mailbox && (string.IsNullOrWhiteSpace(request.Host) ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            request.Port <= 0))
        {
            return ServiceResult<ReportSourceDto>.Failure("name, host, port, username, password, and defaultClientId are required", 400);
        }

        // Refused rather than ignored. Accepting mailbox settings on a source that will
        // never connect to a mailbox would leave a password sitting in the database that
        // nothing will ever use and nobody will remember is there.
        if (!mailbox && (!string.IsNullOrWhiteSpace(request.Host) || request.Port > 0))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                $"a source with protocol '{protocol}' has no mailbox and takes no host or port", 400);
        }

        if (pushed && (!string.IsNullOrWhiteSpace(request.Username) ||
            !string.IsNullOrWhiteSpace(request.Password)))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "an api source is pushed to and takes no host, username or password", 400);
        }

        if (bucket && string.IsNullOrWhiteSpace(request.S3Bucket))
        {
            return ServiceResult<ReportSourceDto>.Failure("an s3 source requires s3Bucket", 400);
        }

        // Half a credential is the dangerous shape: it looks configured and authenticates as
        // nobody. Either both halves, or neither and the ambient chain — never one.
        if (bucket && string.IsNullOrWhiteSpace(request.Username) != string.IsNullOrWhiteSpace(request.Password))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                "an s3 source needs both username (access key id) and password (secret access key), " +
                "or neither to use the ambient credential chain", 400);
        }

        if (!bucket && (!string.IsNullOrWhiteSpace(request.S3Bucket) ||
            !string.IsNullOrWhiteSpace(request.S3Prefix) ||
            !string.IsNullOrWhiteSpace(request.S3Region) ||
            !string.IsNullOrWhiteSpace(request.S3Endpoint)))
        {
            return ServiceResult<ReportSourceDto>.Failure(
                $"a source with protocol '{protocol}' takes no s3 settings", 400);
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
            Host = mailbox ? request.Host.Trim().ToLowerInvariant() : string.Empty,
            Port = mailbox ? request.Port : 0,

            // TLS is not a choice on a bucket: the SDK speaks HTTPS to AWS, and to a custom
            // endpoint it does whatever that endpoint's scheme says. Recorded as true so the
            // console does not display an S3 source as if it were sending a password in the
            // clear.
            UseTls = mailbox ? request.UseTls : bucket,
            Username = pushed ? string.Empty : (request.Username ?? string.Empty).Trim(),
            PasswordEncrypted = pushed || string.IsNullOrEmpty(request.Password)
                ? string.Empty
                : credentialProtector.Protect(request.Password),
            S3Bucket = bucket ? request.S3Bucket!.Trim() : null,
            S3Prefix = bucket ? NullIfBlank(request.S3Prefix) : null,
            S3Region = bucket ? NullIfBlank(request.S3Region) : null,
            S3Endpoint = bucket ? NullIfBlank(request.S3Endpoint) : null,
            S3ForcePathStyle = !bucket || request.S3ForcePathStyle,
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

        if (request.S3Bucket is not null)
        {
            if (string.IsNullOrWhiteSpace(request.S3Bucket))
            {
                return ServiceResult<ReportSourceDto>.Failure("s3Bucket cannot be empty", 400);
            }

            source.S3Bucket = request.S3Bucket.Trim();
        }

        // Blank clears rather than being refused, unlike the bucket: an empty prefix is a
        // meaningful setting — poll the whole bucket — and so is dropping a custom endpoint
        // to go back to AWS.
        if (request.S3Prefix is not null)
        {
            source.S3Prefix = NullIfBlank(request.S3Prefix);
        }

        if (request.S3Region is not null)
        {
            source.S3Region = NullIfBlank(request.S3Region);
        }

        if (request.S3Endpoint is not null)
        {
            source.S3Endpoint = NullIfBlank(request.S3Endpoint);
        }

        if (request.S3ForcePathStyle.HasValue)
        {
            source.S3ForcePathStyle = request.S3ForcePathStyle.Value;
        }

        // Checked at the end, on the row as it will be saved, rather than per field: the
        // protocol and the bucket can arrive in the same request in either order, so no
        // single field's handler can tell whether the result is coherent.
        if (source.Protocol == ReportSourceProtocols.S3 && string.IsNullOrWhiteSpace(source.S3Bucket))
        {
            return ServiceResult<ReportSourceDto>.Failure("an s3 source requires s3Bucket", 400);
        }

        source.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ServiceResult<ReportSourceDto>.Success(ToDto(source, null));
    }

    /// <summary>
    /// Blank is stored as null, so "not set" has one representation rather than two. Every
    /// reader of these columns treats null as absent, and a column that can also hold an
    /// empty string is one every reader has to check twice.
    /// </summary>
    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
            x.S3Bucket,
            x.S3Prefix,
            x.S3Region,
            x.S3Endpoint,
            x.S3ForcePathStyle,
            x.LastProcessedObjectAtUtc,
            x.LastProcessedObjectKey,
            x.CreatedAtUtc,
            x.UpdatedAtUtc);
}
