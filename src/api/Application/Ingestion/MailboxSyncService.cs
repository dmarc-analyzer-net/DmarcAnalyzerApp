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

        try
        {
            using var client = new ImapClient();
            var secureSocketOptions = mailboxSource.UseTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

            await client.ConnectAsync(mailboxSource.Host, mailboxSource.Port, secureSocketOptions, ct);
            await client.AuthenticateAsync(mailboxSource.Username, mailboxSource.PasswordEncrypted, operationToken);

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

                    var xmlStreams = await ExtractXmlStreamsAsync(attachment, operationToken);
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

                                var domainId = await ResolveOrCreateDomainIdAsync(
                                    mailboxSource.DefaultClientId,
                                    normalizedPolicyDomain,
                                    operationToken);

                                var reportId = await TryInsertDmarcReportAsync(
                                    domainId,
                                    mailboxSource.Id,
                                    result.OrganizationName.Trim(),
                                    normalizedReportId,
                                    result.RangeBeginUtc,
                                    result.RangeEndUtc,
                                    result.RecordCount,
                                    operationToken);

                                if (!reportId.HasValue)
                                {
                                    reportsSkippedAsDuplicate++;
                                    continue;
                                }

                                var reportEntityId = reportId.Value;
                                await using var transaction = await db.Database.BeginTransactionAsync(operationToken);
                                await InsertDmarcReportRecordsAsync(reportEntityId, result.Records, operationToken);

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
        CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO dmarc_report
                (""Id"", ""DomainId"", ""MailboxSourceId"", ""OrganizationName"", ""ReportId"", ""RangeBeginUtc"", ""RangeEndUtc"", ""RecordCount"", ""IngestedAtUtc"")
            VALUES
                ({id}, {domainId}, {mailboxSourceId}, {organizationName}, {reportId}, {rangeBeginUtc}, {rangeEndUtc}, {recordCount}, {DateTime.UtcNow})
            ON CONFLICT (""DomainId"", ""ReportId"", ""RangeBeginUtc"", ""RangeEndUtc"") DO NOTHING;
            ", ct);

        return rows > 0 ? id : null;
    }

    private async Task InsertDmarcReportRecordsAsync(
        Guid dmarcReportId,
        IReadOnlyList<DmarcReportRecordParseResult> records,
        CancellationToken ct)
    {
        foreach (var record in records)
        {
            var recordId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO dmarc_report_record
                    (""Id"", ""DmarcReportId"", ""SourceIp"", ""MessageCount"", ""Disposition"", ""DkimResult"", ""SpfResult"", ""HeaderFrom"", ""EnvelopeFrom"", ""EnvelopeTo"")
                VALUES
                    ({recordId}, {dmarcReportId}, {record.SourceIp}, {record.MessageCount}, {record.Disposition}, {record.DkimResult}, {record.SpfResult}, {record.HeaderFrom}, {record.EnvelopeFrom}, {record.EnvelopeTo});
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

    private static async Task<IReadOnlyList<MemoryStream>> ExtractXmlStreamsAsync(MimeEntity attachment, CancellationToken ct)
    {
        var result = new List<MemoryStream>();

        await using var raw = new MemoryStream();

        if (attachment is MessagePart embeddedMessagePart)
        {
            await embeddedMessagePart.Message.WriteToAsync(raw, ct);
        }
        else if (attachment is MimePart mimePart)
        {
            await mimePart.Content.DecodeToAsync(raw, ct);
        }
        else
        {
            return result;
        }

        raw.Position = 0;
        var fileName = (attachment.ContentDisposition?.FileName ?? attachment.ContentType?.Name ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

        if (fileName.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var zip = new ZipArchive(raw, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await using var entryStream = entry.Open();
                var xml = new MemoryStream();
                await entryStream.CopyToAsync(xml, ct);
                xml.Position = 0;
                result.Add(xml);
            }

            return result;
        }

        if (fileName.EndsWith(".gz", StringComparison.Ordinal) || fileName.EndsWith(".gzip", StringComparison.Ordinal))
        {
            raw.Position = 0;
            await using var gzip = new GZipStream(raw, CompressionMode.Decompress, leaveOpen: true);
            var xml = new MemoryStream();
            await gzip.CopyToAsync(xml, ct);
            xml.Position = 0;
            result.Add(xml);
            return result;
        }

        var mimeType = attachment.ContentType?.MimeType ?? string.Empty;

        if (fileName.EndsWith(".xml", StringComparison.Ordinal) ||
            string.Equals(mimeType, "text/xml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mimeType, "application/xml", StringComparison.OrdinalIgnoreCase))
        {
            raw.Position = 0;
            var xml = new MemoryStream();
            await raw.CopyToAsync(xml, ct);
            xml.Position = 0;
            result.Add(xml);
        }

        return result;
    }
}
