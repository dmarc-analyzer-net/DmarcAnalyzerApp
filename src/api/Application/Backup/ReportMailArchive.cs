using System.IO.Compression;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DmarcAnalyzer.Api.Application.Backup;

public interface IReportMailArchive
{
    /// <summary>False when archiving is off or no bucket is configured.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Stores the message, gzipped. Returns false when archiving is off or the write
    /// failed — a caller must never treat "not archived" as "archived", because the
    /// retention deletion pass keys its safety on exactly this answer.
    /// </summary>
    Task<bool> TryArchiveAsync(
        MimeMessage message,
        Guid reportSourceId,
        uint uid,
        long uidValidity,
        DateTime receivedAtUtc,
        CancellationToken ct);

    /// <summary>
    /// Whether this message is already in the archive. The deletion pass asks before
    /// removing anything, so "no delete without a confirmed write" is checked against the
    /// bucket rather than assumed from configuration.
    /// </summary>
    Task<bool> ExistsAsync(
        Guid reportSourceId,
        uint uid,
        long uidValidity,
        DateTime receivedAtUtc,
        CancellationToken ct);
}

/// <summary>
/// The optional report-mail archive: raw report mail copied to the same bucket as the
/// configuration snapshot, so report history survives independently of the mailbox.
/// <para>
/// It stores the <em>whole message</em>, not the extracted XML. The message carries the
/// provenance a bare attachment loses — sending organisation, date, envelope — and it is
/// the exact input the existing parser already handles, so a replay can reuse
/// <c>ExtractXmlStreamsAsync</c> rather than growing a second ingestion route.
/// </para>
/// <para>
/// Enabling it does not reduce the data footprint, it relocates it. An archive prefix with
/// no lifecycle rule re-creates the unbounded second copy that bounded mailbox retention
/// exists to remove — see the configuration reference.
/// </para>
/// </summary>
public sealed class ReportMailArchive(
    IObjectStorage storage,
    IOptions<BackupOptions> options,
    ILogger<ReportMailArchive> logger) : IReportMailArchive
{
    private readonly BackupOptions _options = options.Value;

    public bool IsEnabled => _options.ArchiveReportMail && storage.IsConfigured;

    public async Task<bool> TryArchiveAsync(
        MimeMessage message,
        Guid reportSourceId,
        uint uid,
        long uidValidity,
        DateTime receivedAtUtc,
        CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var key = Key(_options.Prefix, reportSourceId, uid, uidValidity, receivedAtUtc);

        try
        {
            using var raw = new MemoryStream();
            await message.WriteToAsync(raw, ct);

            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                raw.Position = 0;
                await raw.CopyToAsync(gzip, ct);
            }

            await storage.PutAsync(key, compressed.ToArray(), "application/gzip", ct);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately not fatal to the sync. The report itself is already being
            // persisted to Postgres, and failing the whole message because a bucket was
            // briefly unreachable would trade a complete database for a complete archive.
            // The consequence is bounded: an unarchived message is one the retention
            // deletion pass will refuse to delete.
            logger.LogWarning(ex, "Failed to archive report mail to {Key}", key);

            return false;
        }
    }

    public async Task<bool> ExistsAsync(
        Guid reportSourceId,
        uint uid,
        long uidValidity,
        DateTime receivedAtUtc,
        CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var key = Key(_options.Prefix, reportSourceId, uid, uidValidity, receivedAtUtc);

        return await storage.GetLengthAsync(key, ct) is > 0;
    }

    /// <summary>
    /// Dated for legibility and for S3 lifecycle rules, which match on key prefixes — a
    /// date-partitioned prefix is what makes "expire the archive after N months"
    /// expressible at all. UIDVALIDITY is in the name because a UID only identifies a
    /// message within one validity generation.
    /// </summary>
    public static string Key(
        string prefix,
        Guid reportSourceId,
        uint uid,
        long uidValidity,
        DateTime receivedAtUtc)
        => $"{prefix.Trim().Trim('/')}/reports/{receivedAtUtc:yyyy}/{receivedAtUtc:MM}/{receivedAtUtc:dd}/" +
           $"{reportSourceId}/{uidValidity}-{uid}.eml.gz";
}
