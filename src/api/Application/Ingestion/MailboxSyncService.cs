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
using Npgsql;

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

        var mailboxSource = await db.MailboxSources
            .SingleOrDefaultAsync(x => x.Id == mailboxSourceId, ct);

        if (mailboxSource is null)
        {
            return ServiceResult<MailboxSyncResult>.Failure("mailbox source not found", 404);
        }

        if (!string.Equals(mailboxSource.Protocol, "imap", StringComparison.OrdinalIgnoreCase))
        {
            return ServiceResult<MailboxSyncResult>.Failure("manual sync currently supports only IMAP", 400);
        }

        var hasActiveRun = await db.MailboxSyncRuns.AnyAsync(
            x => x.MailboxSourceId == mailboxSource.Id && x.Status == "running",
            ct);

        if (hasActiveRun)
        {
            logger.LogInformation(
                "Skipping sync for mailbox source {MailboxSourceId} because an active run already exists",
                mailboxSource.Id);

            return ServiceResult<MailboxSyncResult>.Failure("active sync already running", 409);
        }

        var syncRun = new MailboxSyncRun
        {
            MailboxSourceId = mailboxSource.Id,
            Trigger = string.IsNullOrWhiteSpace(trigger) ? "unknown" : trigger.Trim().ToLowerInvariant(),
            Status = "running",
            StartedAtUtc = startedAtUtc,
            CreatedAtUtc = startedAtUtc,
        };
        try
        {
            db.MailboxSyncRuns.Add(syncRun);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsActiveRunUniqueViolation(ex))
        {
            return ServiceResult<MailboxSyncResult>.Failure("active sync already running", 409);
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
            await client.AuthenticateAsync(mailboxSource.Username, mailboxSource.PasswordEncrypted, ct);

            var inbox = client.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

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

            var uids = await inbox.SearchAsync(query, ct);
            var maxMessagesPerSync = Math.Max(1, _options.MaxMessagesPerSync);
            var selectedUids = uids
                .Take(maxMessagesPerSync)
                .ToArray();

            long? highestProcessedUid = null;

            foreach (var uid in selectedUids)
            {
                ct.ThrowIfCancellationRequested();
                messagesScanned++;
                highestProcessedUid = uid.Id;

                var message = await inbox.GetMessageAsync(uid, ct);

                if (!message.Attachments.Any())
                {
                    continue;
                }

                foreach (var attachment in message.Attachments)
                {
                    ct.ThrowIfCancellationRequested();

                    var xmlStreams = await ExtractXmlStreamsAsync(attachment, ct);
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

                                var exists = await db.DmarcReportIngests.AnyAsync(x =>
                                    x.ClientId == mailboxSource.DefaultClientId &&
                                    x.PolicyDomain == normalizedPolicyDomain &&
                                    x.ReportId == normalizedReportId &&
                                    x.ReportRangeBeginUtc == result.RangeBeginUtc &&
                                    x.ReportRangeEndUtc == result.RangeEndUtc,
                                    ct);

                                if (exists)
                                {
                                    reportsSkippedAsDuplicate++;
                                    continue;
                                }

                                db.DmarcReportIngests.Add(new DmarcReportIngest
                                {
                                    ClientId = mailboxSource.DefaultClientId,
                                    MailboxSourceId = mailboxSource.Id,
                                    PolicyDomain = normalizedPolicyDomain,
                                    ReportId = normalizedReportId,
                                    ReportRangeBeginUtc = result.RangeBeginUtc,
                                    ReportRangeEndUtc = result.RangeEndUtc,
                                    OrganizationName = result.OrganizationName.Trim(),
                                    RecordCount = result.RecordCount,
                                    IngestedAtUtc = DateTime.UtcNow,
                                });

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

            syncRun.Status = "success";
            syncRun.FinishedAtUtc = DateTime.UtcNow;
            syncRun.MessagesScanned = messagesScanned;
            syncRun.AttachmentsProcessed = attachmentsProcessed;
            syncRun.ReportsInserted = reportsInserted;
            syncRun.ReportsSkippedAsDuplicate = reportsSkippedAsDuplicate;
            syncRun.ParseFailures = parseFailures;

            await db.SaveChangesAsync(ct);

            await client.DisconnectAsync(true, ct);

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

            syncRun.Status = "failed";
            syncRun.FinishedAtUtc = DateTime.UtcNow;
            syncRun.MessagesScanned = messagesScanned;
            syncRun.AttachmentsProcessed = attachmentsProcessed;
            syncRun.ReportsInserted = reportsInserted;
            syncRun.ReportsSkippedAsDuplicate = reportsSkippedAsDuplicate;
            syncRun.ParseFailures = parseFailures;
            syncRun.Error = ex is OperationCanceledException
                ? "sync cancelled"
                : ex.Message;

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

    private static bool IsActiveRunUniqueViolation(DbUpdateException ex)
    {
        if (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return string.Equals(pg.ConstraintName, "IX_mailbox_sync_run_active_unique", StringComparison.Ordinal);
        }

        return false;
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
