using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// What one archived message is called, in terms both protocols can express.
/// </summary>
/// <param name="Generation">
/// The scope <paramref name="Uid"/> is unique within. IMAP puts UIDVALIDITY here, because a
/// UID only identifies a message within one generation; POP3 has no generations and puts a
/// literal <c>pop3</c>, which also keeps the two protocols' keys from ever colliding.
/// </param>
/// <param name="Uid">The message's own name within that scope: an IMAP UID, or a POP3 UIDL.</param>
public readonly record struct ReportMailIdentity(string Generation, string Uid)
{
    public static ReportMailIdentity ForImap(uint uid, long uidValidity)
        => new(uidValidity.ToString(System.Globalization.CultureInfo.InvariantCulture),
               uid.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// A POP3 message, named by its UIDL.
    /// <para>
    /// RFC 1939 lets a UIDL be any 1–70 printable ASCII characters, which includes plenty
    /// that have meaning in an object key — <c>/</c> would silently split the key into
    /// another prefix level, and a lifecycle rule written against the documented layout
    /// would then miss it. So a UIDL is used verbatim only while it is unambiguous, and
    /// otherwise replaced by a SHA-256 of itself: still deterministic, which is all the
    /// archive needs, since <c>ExistsAsync</c> recomputes the same key from the same UIDL.
    /// </para>
    /// </summary>
    public static ReportMailIdentity ForPop3(string uidl)
        => new("pop3", IsKeySafe(uidl) ? uidl : "h-" + Sha256Hex(uidl));

    /// <summary>
    /// An object pulled from a bucket, named by its key.
    /// <para>
    /// The key is almost always hashed rather than used verbatim, and that is expected: a real
    /// key has slashes in it, and a slash left alone would push the archived copy into a
    /// prefix nobody documented — where a lifecycle rule written against the documented layout
    /// would never expire it. The hash is deterministic, which is all the archive needs.
    /// </para>
    /// </summary>
    public static ReportMailIdentity ForS3(string key)
        => new("s3", IsKeySafe(key) ? key : "h-" + Sha256Hex(key));

    /// <summary>
    /// Whether a name can go into an object key as it stands: unambiguous characters only,
    /// and no longer than RFC 1939 allows a UIDL to be. The length bound is shared rather
    /// than per-protocol because its purpose is the same either way — keep the key readable
    /// and keep it bounded — and an S3 key over that length simply gets hashed, which is the
    /// common case and costs nothing.
    /// </summary>
    private static bool IsKeySafe(string value)
        => value.Length is > 0 and <= 70 && value.All(c =>
            c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static string Sha256Hex(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

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
        ReportMailIdentity identity,
        DateTime receivedAtUtc,
        CancellationToken ct);

    /// <summary>
    /// Whether this message is already in the archive. The deletion pass asks before
    /// removing anything, so "no delete without a confirmed write" is checked against the
    /// bucket rather than assumed from configuration.
    /// </summary>
    Task<bool> ExistsAsync(
        Guid reportSourceId,
        ReportMailIdentity identity,
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
        ReportMailIdentity identity,
        DateTime receivedAtUtc,
        CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var key = Key(_options.Prefix, reportSourceId, identity, receivedAtUtc);

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
        ReportMailIdentity identity,
        DateTime receivedAtUtc,
        CancellationToken ct)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var key = Key(_options.Prefix, reportSourceId, identity, receivedAtUtc);

        return await storage.GetLengthAsync(key, ct) is > 0;
    }

    /// <summary>
    /// Dated for legibility and for S3 lifecycle rules, which match on key prefixes — a
    /// date-partitioned prefix is what makes "expire the archive after N months"
    /// expressible at all. The generation is in the name because a UID only identifies a
    /// message within one validity generation.
    /// <para>
    /// The IMAP form is unchanged from before POP3 existed, deliberately: mail already in a
    /// bucket has to keep answering <c>ExistsAsync</c>, and a key format that shifted under
    /// it would make every archived message read as unarchived — which the retention pass
    /// would then refuse to delete, quietly and for ever.
    /// </para>
    /// </summary>
    public static string Key(
        string prefix,
        Guid reportSourceId,
        ReportMailIdentity identity,
        DateTime receivedAtUtc)
        => $"{prefix.Trim().Trim('/')}/reports/{receivedAtUtc:yyyy}/{receivedAtUtc:MM}/{receivedAtUtc:dd}/" +
           $"{reportSourceId}/{identity.Generation}-{identity.Uid}.eml.gz";
}
