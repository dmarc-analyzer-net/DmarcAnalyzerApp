using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using System.IO.Compression;
using System.Linq;
using DmarcAnalyzer.Api.Workers;
using System.Threading.Tasks;

namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed class MailboxSyncService(
    DmarcAnalyzerDbContext db,
    IDmarcReportParser parser,
    Security.ICredentialProtector credentialProtector,
    IOptions<WorkerOptions> options,
    ILogger<MailboxSyncService> logger) : IMailboxSyncService
{
    private readonly WorkerOptions _options = options.Value;

    public async Task<ServiceResult<MailboxSyncResult>> SyncMailboxSourceAsync(Guid mailboxSourceId, CancellationToken ct)
        => await SyncMailboxSourceAsync(mailboxSourceId, "manual", ct);

    public async Task<ServiceResult<MailboxSyncResult>> SyncMailboxSourceAsync(Guid mailboxSourceId, string trigger, CancellationToken ct)
    {
        var startedAtUtc = DateTime.UtcNow;
        using var syncTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var syncRunTimeoutMinutes = Math.Max(1, _options.SyncRunTimeoutMinutes);
        syncTimeoutCts.CancelAfter(TimeSpan.FromMinutes(syncRunTimeoutMinutes));
        var operationToken = syncTimeoutCts.Token;

        var mailboxSource = await db.MailboxSources
            .SingleOrDefaultAsync(x => x.Id == mailboxSourceId, operationToken);

        if (mailboxSource is null)
        {
            return ServiceResult<MailboxSyncResult>.Failure("mailbox source not found", 404);
        }

        if (!string.Equals(mailboxSource.Protocol, "imap", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceResult<MailboxSyncResult>.Failure("manual sync currently supports only IMAP", 400);
        }

        var messagesScanned = 0;
        var attachmentsProcessed = 0;
        var reportsInserted = 0;
        var reportsSkippedAsDuplicate = 0;
        var parseFailures = 0;

        // Legacy rows store the password in plaintext; re-protect them on first use.
        if (!credentialProtector.IsProtected(mailboxSource.PasswordEncrypted))
        {
            var reprotected = credentialProtector.Protect(mailboxSource.PasswordEncrypted);
            if (reprotected != mailboxSource.PasswordEncrypted)
            {
                mailboxSource.PasswordEncrypted = reprotected;
                mailboxSource.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(operationToken);
                logger.LogInformation("Re-protected stored credential for mailbox source {MailboxSourceId}", mailboxSource.Id);
            }
        }

        var mailboxPassword = credentialProtector.Unprotect(mailboxSource.PasswordEncrypted);

        try
        {
            using var client = new ImapClient();
            var secureSocketOptions = mailboxSource.UseTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

            await client.ConnectAsync(mailboxSource.Host, mailboxSource.Port, secureSocketOptions, ct);
            await client.AuthenticateAsync(mailboxSource.Username, mailboxPassword, operationToken);

            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, operationToken);

            var currentUidValidity = (long)inbox.UidValidity;
            var lastProcessedUid = mailboxSource.LastProcessedUid;
            if (mailboxSource.LastProcessedUidValidity.HasValue &&
                mailboxSource.LastProcessedUidValidity.Value != currentUidValidity)
            {
                lastProcessedUid = null;
            }

            SearchQuery query = SearchQuery.All;
            if (lastProcessedUid.HasValue && lastProcessedUid.Value > 0 && lastProcessedUid.Value < uint.MaxValue)
            {
                var startUid = new UniqueId((uint)lastProcessedUid.Value + 1);
                query = SearchQuery.Uids(new UniqueIdRange(startUid, UniqueId.MaxValue));
            }

            var uids = await inbox.SearchAsync(query, operationToken);
            var maxMessagesPerSync = Math.Max(1, _options.MaxMessagesPerSync);
            var selectedUids = uids
                .Take(maxMessagesPerSync)
                .ToArray();

            long? highestProcessedUid = null;

            foreach (var uid in selectedUids)
            {
                operationToken.ThrowIfCancellationRequested();
                messagesScanned++;
                highestProcessedUid = uid.Id;

                var message = await inbox.GetMessageAsync(uid, operationToken);

                if (!message.Attachments.Any())
                {
                    continue;
                }

                foreach (var attachment in message.Attachments)
                {
                    operationToken.ThrowIfCancellationRequested();

                    IReadOnlyList<MemoryStream> xmlStreams;
                    try
                    {
                        xmlStreams = await ExtractXmlStreamsAsync(attachment, logger, operationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        parseFailures++;
                        logger.LogWarning(ex,
                            "Failed to extract DMARC attachment {AttachmentName} for mailbox source {MailboxSourceId}",
                            GetAttachmentFileName(attachment), mailboxSource.Id);
                        continue;
                    }

                    if (xmlStreams.Count == 0)
                    {
                        continue;
                    }

                    foreach (var xmlStream in xmlStreams)
                    {
                        await using (xmlStream)
                        {
                            attachmentsProcessed++;

                            try
                            {
                                var result = parser.Parse(xmlStream);

                                var normalizedPolicyDomain = result.PolicyDomain.Trim().ToLowerInvariant();
                                var normalizedReportId = result.ReportId.Trim();

                                // Left outside the transaction below on purpose: a domain
                                // is shared by every report for it, not owned by this
                                // one, so rolling it back with a failed report would be
                                // wrong. A domain with no reports yet is a state the
                                // console already handles.
                                var domainId = await ResolveOrCreateDomainIdAsync(
                                    mailboxSource.DefaultClientId,
                                    normalizedPolicyDomain,
                                    operationToken);

                                // The report row and its records must commit together.
                                // Inserted before this transaction opened, the report
                                // auto-committed on its own; if the records insert then
                                // failed, the report survived with no children — and
                                // because deduplication keys on that row, every later
                                // sync saw a duplicate and skipped it, so the records
                                // were never backfilled. A single bad record left a
                                // report permanently empty and silently wrong.
                                await using var transaction = await db.Database.BeginTransactionAsync(operationToken);

                                var reportId = await TryInsertDmarcReportAsync(
                                    domainId,
                                    mailboxSource.Id,
                                    result.OrganizationName.Trim(),
                                    normalizedReportId,
                                    result.RangeBeginUtc,
                                    result.RangeEndUtc,
                                    result.RecordCount,
                                    result,
                                    operationToken);

                                if (!reportId.HasValue)
                                {
                                    // Already ingested. Disposing the transaction on the
                                    // way out rolls back the no-op insert.
                                    reportsSkippedAsDuplicate++;
                                    continue;
                                }

                                var reportEntityId = reportId.Value;
                                await InsertDmarcReportRecordsAsync(
                                    reportEntityId, result.RangeBeginUtc, result.Records, operationToken);

                                await TryInsertReportIngestAsync(
                                    mailboxSource.DefaultClientId,
                                    mailboxSource.Id,
                                    normalizedPolicyDomain,
                                    normalizedReportId,
                                    result.RangeBeginUtc,
                                    result.RangeEndUtc,
                                    result.OrganizationName.Trim(),
                                    result.RecordCount,
                                    operationToken);

                                await transaction.CommitAsync(operationToken);

                                reportsInserted++;
                            }
                            catch (Exception ex)
                            {
                                parseFailures++;
                                logger.LogWarning(ex, "Failed to parse DMARC attachment for mailbox source {MailboxSourceId}", mailboxSource.Id);
                            }
                        }
                    }
                }
            }

            mailboxSource.LastSuccessSyncAtUtc = DateTime.UtcNow;
            mailboxSource.LastProcessedUidValidity = currentUidValidity;
            if (highestProcessedUid.HasValue)
            {
                mailboxSource.LastProcessedUid = highestProcessedUid;
            }
            mailboxSource.UpdatedAtUtc = DateTime.UtcNow;

            if (operationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"sync run exceeded configured timeout of {syncRunTimeoutMinutes} minute(s)");
            }

            db.MailboxSyncRuns.Add(new MailboxSyncRun
            {
                MailboxSourceId = mailboxSource.Id,
                Trigger = string.IsNullOrWhiteSpace(trigger) ? "unknown" : trigger.Trim().ToLowerInvariant(),
                Status = "success",
                StartedAtUtc = startedAtUtc,
                FinishedAtUtc = DateTime.UtcNow,
                MessagesScanned = messagesScanned,
                AttachmentsProcessed = attachmentsProcessed,
                ReportsInserted = reportsInserted,
                ReportsSkippedAsDuplicate = reportsSkippedAsDuplicate,
                ParseFailures = parseFailures,
                CreatedAtUtc = startedAtUtc,
            });

            await db.SaveChangesAsync(operationToken);

            await client.DisconnectAsync(true, operationToken);

            return ServiceResult<MailboxSyncResult>.Success(new MailboxSyncResult(
                mailboxSource.Id,
                messagesScanned,
                attachmentsProcessed,
                reportsInserted,
                reportsSkippedAsDuplicate,
                parseFailures,
                true,
                null,
                startedAtUtc,
                DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mailbox sync failed for source {MailboxSourceId}", mailboxSource.Id);

            db.ChangeTracker.Clear();

            db.MailboxSyncRuns.Add(new MailboxSyncRun
            {
                MailboxSourceId = mailboxSource.Id,
                Trigger = string.IsNullOrWhiteSpace(trigger) ? "unknown" : trigger.Trim().ToLowerInvariant(),
                Status = "failed",
                StartedAtUtc = startedAtUtc,
                FinishedAtUtc = DateTime.UtcNow,
                MessagesScanned = messagesScanned,
                AttachmentsProcessed = attachmentsProcessed,
                ReportsInserted = reportsInserted,
                ReportsSkippedAsDuplicate = reportsSkippedAsDuplicate,
                ParseFailures = parseFailures,
                Error = ex is OperationCanceledException
                    ? $"sync cancelled or timed out after {syncRunTimeoutMinutes} minute(s)"
                    : ex.Message,
                CreatedAtUtc = startedAtUtc,
            });

            await TryPersistRunStateAsync(mailboxSource.Id);

            return ServiceResult<MailboxSyncResult>.Success(new MailboxSyncResult(
                mailboxSource.Id,
                messagesScanned,
                attachmentsProcessed,
                reportsInserted,
                reportsSkippedAsDuplicate,
                parseFailures,
                false,
                ex.Message,
                startedAtUtc,
                DateTime.UtcNow));
        }
    }

    private async Task<bool> TryInsertReportIngestAsync(
        Guid clientId,
        Guid mailboxSourceId,
        string policyDomain,
        string reportId,
        DateTime reportRangeBeginUtc,
        DateTime reportRangeEndUtc,
        string organizationName,
        int recordCount,
        CancellationToken ct)
    {
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO dmarc_report_ingest
                (""Id"", ""ClientId"", ""MailboxSourceId"", ""PolicyDomain"", ""ReportId"", ""ReportRangeBeginUtc"", ""ReportRangeEndUtc"", ""OrganizationName"", ""RecordCount"", ""IngestedAtUtc"")
            VALUES
                ({Guid.NewGuid()}, {clientId}, {mailboxSourceId}, {policyDomain}, {reportId}, {reportRangeBeginUtc}, {reportRangeEndUtc}, {organizationName}, {recordCount}, {DateTime.UtcNow})
            ON CONFLICT (""ClientId"", ""PolicyDomain"", ""ReportId"", ""ReportRangeBeginUtc"", ""ReportRangeEndUtc"") DO NOTHING;
            ", ct);

        return rows > 0;
    }

    private async Task<Guid> ResolveOrCreateDomainIdAsync(Guid defaultClientId, string normalizedPolicyDomain, CancellationToken ct)
    {
        var existing = await db.Domains
            .AsNoTracking()
            .Where(x => x.Name == normalizedPolicyDomain)
            .Select(x => new { x.Id })
            .SingleOrDefaultAsync(ct);

        if (existing is not null)
        {
            return existing.Id;
        }

        var createdId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO domain
                (""Id"", ""ClientId"", ""Name"", ""IsActive"", ""CreatedAtUtc"", ""UpdatedAtUtc"")
            VALUES
                ({createdId}, {defaultClientId}, {normalizedPolicyDomain}, {true}, {DateTime.UtcNow}, {DateTime.UtcNow})
            ON CONFLICT (""Name"") DO NOTHING;
            ", ct);

        var resolved = await db.Domains
            .AsNoTracking()
            .Where(x => x.Name == normalizedPolicyDomain)
            .Select(x => new { x.Id })
            .SingleAsync(ct);

        return resolved.Id;
    }

    private async Task<Guid?> TryInsertDmarcReportAsync(
        Guid domainId,
        Guid mailboxSourceId,
        string organizationName,
        string reportId,
        DateTime rangeBeginUtc,
        DateTime rangeEndUtc,
        int recordCount,
        DmarcReportParseResult parsed,
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO dmarc_report
                (""Id"", ""DomainId"", ""MailboxSourceId"", ""OrganizationName"", ""ReportId"", ""RangeBeginUtc"", ""RangeEndUtc"", ""RecordCount"", ""IngestedAtUtc"", ""PublishedPolicy"", ""SubdomainPolicy"", ""PublishedPct"", ""DkimAlignment"", ""SpfAlignment"")
            VALUES
                ({id}, {domainId}, {mailboxSourceId}, {organizationName}, {reportId}, {rangeBeginUtc}, {rangeEndUtc}, {recordCount}, {DateTime.UtcNow}, {parsed.PublishedPolicy}, {parsed.SubdomainPolicy}, {parsed.PublishedPct}, {parsed.DkimAlignment}, {parsed.SpfAlignment})
            ON CONFLICT (""DomainId"", ""ReportId"", ""RangeBeginUtc"", ""RangeEndUtc"") DO NOTHING;
            ", ct);

        return rows > 0 ? id : null;
    }

    private async Task InsertDmarcReportRecordsAsync(
        Guid dmarcReportId,
        DateTime reportRangeBeginUtc,
        IReadOnlyList<DmarcReportRecordParseResult> records,
        CancellationToken ct)
    {
        foreach (var record in records)
        {
            var recordId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO dmarc_report_record
                    (""Id"", ""DmarcReportId"", ""SourceIp"", ""MessageCount"", ""Disposition"", ""DkimResult"", ""SpfResult"", ""HeaderFrom"", ""EnvelopeFrom"", ""EnvelopeTo"", ""ReportRangeBeginUtc"")
                VALUES
                    ({recordId}, {dmarcReportId}, {record.SourceIp}, {record.MessageCount}, {record.Disposition}, {record.DkimResult}, {record.SpfResult}, {record.HeaderFrom}, {record.EnvelopeFrom}, {record.EnvelopeTo}, {reportRangeBeginUtc});
                ", ct);

            foreach (var dkim in record.DkimAuthResults)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO dmarc_report_record_dkim_auth_result
                        (""Id"", ""DmarcReportRecordId"", ""Domain"", ""Selector"", ""Result"", ""HumanResult"")
                    VALUES
                        ({Guid.NewGuid()}, {recordId}, {dkim.Domain}, {dkim.Selector}, {dkim.Result}, {dkim.HumanResult});
                    ", ct);
            }

            foreach (var spf in record.SpfAuthResults)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO dmarc_report_record_spf_auth_result
                        (""Id"", ""DmarcReportRecordId"", ""Domain"", ""Scope"", ""Result"", ""HumanResult"")
                    VALUES
                        ({Guid.NewGuid()}, {recordId}, {spf.Domain}, {spf.Scope}, {spf.Result}, {spf.HumanResult});
                    ", ct);
            }
        }
    }

    private async Task TryPersistRunStateAsync(Guid mailboxSourceId)
    {
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception persistEx)
        {
            logger.LogWarning(
                persistEx,
                "Failed to persist mailbox sync run final state for mailbox source {MailboxSourceId}",
                mailboxSourceId);
        }
    }

    private static string GetAttachmentFileName(MimeEntity attachment)
        => (attachment.ContentDisposition?.FileName ?? attachment.ContentType?.Name ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

    private static async Task<IReadOnlyList<MemoryStream>> ExtractXmlStreamsAsync(MimeEntity attachment, ILogger logger, CancellationToken ct)
    {
        var result = new List<MemoryStream>();

        await using var raw = new MemoryStream();

        // Both of these are nullable: a malformed part can declare itself a message
        // or a MIME part and carry nothing. We are parsing attachments that arrived
        // from the internet, so treat that as an empty extraction — the same as an
        // entity type we don't handle — rather than throwing.
        if (attachment is MessagePart { Message: not null } embeddedMessagePart)
        {
            await embeddedMessagePart.Message.WriteToAsync(raw, ct);
        }
        else if (attachment is MimePart { Content: not null } mimePart)
        {
            await mimePart.Content.DecodeToAsync(raw, ct);
        }
        else
        {
            return result;
        }

        var fileName = GetAttachmentFileName(attachment);
        var payload = raw.ToArray();

        // Container detection prefers magic bytes over filename: DMARC senders
        // frequently misname attachments (.zip holding gzip data and vice versa).
        if (IsZip(payload))
        {
            using var zipStream = new MemoryStream(payload, writable: false);
            using var zip = SharpCompress.Archives.ArchiveFactory.OpenArchive(zipStream);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsDirectory || entry.Key is null)
                {
                    continue;
                }

                if (entry.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    LogSkippedTlsReport(logger, entry.Key);
                    continue;
                }

                if (!entry.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    await using var entryStream = entry.OpenEntryStream();
                    var xml = new MemoryStream();
                    await entryStream.CopyToAsync(xml, ct);
                    xml.Position = 0;
                    result.Add(xml);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Failed to extract zip entry {EntryName} from attachment {AttachmentName}",
                        entry.Key, fileName);
                }
            }

            return result;
        }

        if (IsGzip(payload))
        {
            using var gzipSource = new MemoryStream(payload, writable: false);
            await using var gzip = new GZipStream(gzipSource, CompressionMode.Decompress);
            var decoded = new MemoryStream();
            await gzip.CopyToAsync(decoded, ct);
            decoded.Position = 0;

            // Gzip is detected by magic bytes, so whatever was inside lands here
            // regardless of format. TLS reports arrive exactly this way
            // (application/tlsrpt+gzip) and used to be handed to the DMARC parser,
            // which threw and inflated the parse-failure count.
            if (ReportPayloadFormat.Classify(decoded.GetBuffer().AsSpan(0, (int)decoded.Length))
                == ReportPayloadKind.SmtpTlsReportJson)
            {
                await decoded.DisposeAsync();
                LogSkippedTlsReport(logger, fileName);
                return result;
            }

            result.Add(decoded);
            return result;
        }

        var mimeType = attachment.ContentType?.MimeType ?? string.Empty;

        switch (ReportPayloadFormat.Classify(payload, fileName, mimeType))
        {
            case ReportPayloadKind.DmarcAggregateXml:
                result.Add(new MemoryStream(payload, writable: false));
                break;
            case ReportPayloadKind.SmtpTlsReportJson:
                LogSkippedTlsReport(logger, fileName);
                break;
        }

        return result;
    }

    private static bool IsZip(byte[] payload)
        => payload.Length >= 4 && payload[0] == 0x50 && payload[1] == 0x4B &&
           (payload[2] == 0x03 || payload[2] == 0x05 || payload[2] == 0x07);

    private static bool IsGzip(byte[] payload)
        => payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B;

    /// <summary>
    /// TLS reports (RFC 8460) share the mailbox with DMARC reports but are a
    /// different format entirely. Skipped deliberately and logged, rather than
    /// dropped silently or counted as a parse failure — see the backlog item for
    /// actually supporting them.
    /// </summary>
    private static void LogSkippedTlsReport(ILogger logger, string fileName) =>
        logger.LogInformation(
            "Skipped SMTP TLS report attachment {AttachmentName}: TLS-RPT ingestion is not implemented",
            fileName);

}
