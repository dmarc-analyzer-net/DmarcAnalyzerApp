using System.IO.Compression;
using DmarcAnalyzer.Api.Application.Reports;
using MimeKit;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// How much decompressed data one mail attachment may produce.
/// </summary>
/// <param name="MaxEntryBytes">
/// Cap on a single decompressed payload. The classic decompression bomb is one small
/// entry that expands without limit, and this is what stops it.
/// </param>
/// <param name="MaxTotalBytes">
/// Cap on everything one attachment expands to, across all its entries. Not redundant
/// with <paramref name="MaxEntryBytes"/>: the sync loop extracts every payload in an
/// attachment before it processes any of them, so they are all held in memory at once
/// and a thousand entries just under the per-entry cap is the same attack.
/// </param>
/// <param name="MaxEntries">
/// Cap on archive entries examined. Bounds the loop itself, so an archive with millions
/// of tiny members costs bounded work even when nothing it contains is large.
/// </param>
public sealed record ReportPayloadLimits(long MaxEntryBytes, long MaxTotalBytes, int MaxEntries);

/// <summary>
/// Raised when an attachment tries to expand past its limits. Carries which limit and
/// what it was, because the operator response — raise the limit, or go look at the
/// mailbox — depends entirely on that.
/// </summary>
public sealed class ReportPayloadTooLargeException(string limitName, long limitValue, string detail)
    : Exception($"report payload exceeded {limitName} ({limitValue}): {detail}")
{
    public string LimitName { get; } = limitName;
    public long LimitValue { get; } = limitValue;
}

/// <summary>
/// Pulls report payloads out of a mail attachment, bounded.
/// <para>
/// Everything this touches arrived from the internet, and a DMARC RUA address is
/// <em>published in DNS</em> — the whole point of a <c>rua=mailto:</c> record is to tell
/// strangers where to send reports. So the input here is not merely untrusted, its
/// address is advertised. Decompressing it without a limit means anyone who can read a
/// DNS record can decide how much memory this process allocates, and because there is
/// exactly one ingestion worker per database, exhausting it stops ingestion for every
/// client at once rather than just the mailbox that was targeted.
/// </para>
/// <para>
/// The limits are absolute byte counts rather than a compression ratio. A ratio reads
/// like the more principled control, but it needs a trustworthy compressed size for each
/// entry, and the archive formats here do not reliably carry one — a ratio check that
/// silently does not apply on some paths is worse than not claiming to have one. Absolute
/// caps are enforceable on every path, including the ones where the archive lies about
/// itself.
/// </para>
/// <para>
/// What is <em>not</em> bounded here: the compressed attachment itself, which is already
/// in memory by the time this is called — the sync loop fetched the whole message. The
/// practical limit on that is the receiving mail server's maximum message size, which is
/// outside this application. Expansion is the part with no natural ceiling, and it is the
/// part this bounds.
/// </para>
/// </summary>
public static class ReportPayloadExtractor
{
    public static async Task<ExtractedPayloadSet> ExtractAsync(
        MimeEntity attachment,
        ReportPayloadLimits limits,
        ILogger logger,
        CancellationToken ct)
    {
        var result = new List<ExtractedReportPayload>();
        var truncated = false;

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
            return new ExtractedPayloadSet(result, ArchiveTruncated: false);
        }

        var fileName = GetAttachmentFileName(attachment);
        var payload = raw.ToArray();

        // Container detection prefers magic bytes over filename: DMARC senders
        // frequently misname attachments (.zip holding gzip data and vice versa).
        if (IsZip(payload))
        {
            truncated = await ExtractZipAsync(payload, fileName, limits, result, logger, ct);
            return new ExtractedPayloadSet(result, truncated);
        }

        if (IsGzip(payload))
        {
            await ExtractGzipAsync(payload, fileName, limits, result, ct);
            return new ExtractedPayloadSet(result, ArchiveTruncated: false);
        }

        var mimeType = attachment.ContentType?.MimeType ?? string.Empty;
        var bareKind = ReportPayloadFormat.Classify(payload, fileName, mimeType);
        if (bareKind != ReportPayloadKind.Unknown)
        {
            result.Add(new ExtractedReportPayload(
                bareKind, new MemoryStream(payload, writable: false), fileName));
        }

        return new ExtractedPayloadSet(result, ArchiveTruncated: false);
    }

    /// <summary>
    /// Walks the archive into <paramref name="result"/>. Returns whether the entry cap
    /// stopped the walk early, so a caller that can act on that — the HTTP endpoint, whose
    /// client can split the payload and post again — is able to, rather than being handed
    /// a partial extraction that looks complete.
    /// </summary>
    private static async Task<bool> ExtractZipAsync(
        byte[] payload,
        string fileName,
        ReportPayloadLimits limits,
        List<ExtractedReportPayload> result,
        ILogger logger,
        CancellationToken ct)
    {
        using var zipStream = new MemoryStream(payload, writable: false);
        using var zip = SharpCompress.Archives.ArchiveFactory.OpenArchive(zipStream);

        long totalBytes = 0;
        var entriesSeen = 0;

        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.Key is null)
            {
                continue;
            }

            // Counted before the suffix filter, not after. The filter is a convenience
            // for real senders, not a control: an archive of ten million entries named
            // .txt still costs the same walk, and an attacker picks the names.
            if (++entriesSeen > limits.MaxEntries)
            {
                logger.LogWarning(
                    "Stopped reading {AttachmentName} at {MaxEntries} entries; the rest were ignored. " +
                    "Raise Worker:MaxReportArchiveEntries if a real sender legitimately packs more.",
                    fileName, limits.MaxEntries);
                return true;
            }

            // The suffix pre-filter keeps skipping junk; the extracted bytes
            // decide the format, same contract as everywhere else.
            if (!entry.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !entry.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            MemoryStream extracted;
            try
            {
                await using var entryStream = entry.OpenEntryStream();
                extracted = await ReadBoundedAsync(
                    entryStream, limits, totalBytes, $"zip entry {entry.Key}", ct);
            }
            catch (ReportPayloadTooLargeException)
            {
                // The whole attachment stops, not just this entry, and anything already
                // pulled out of it is dropped: an archive that has tried this is not one
                // to take partial results from. Rethrown rather than swallowed so the
                // sync loop counts it a parse failure — a refused bomb that returned
                // quietly would leave no trace in the run's own statistics, which is the
                // one place an operator would look for it.
                foreach (var done in result)
                {
                    await done.Stream.DisposeAsync();
                }

                result.Clear();
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Failed to extract zip entry {EntryName} from attachment {AttachmentName}",
                    entry.Key, fileName);
                continue;
            }

            totalBytes += extracted.Length;

            var entryKind = ReportPayloadFormat.Classify(
                extracted.GetBuffer().AsSpan(0, (int)extracted.Length), entry.Key, null);
            if (entryKind == ReportPayloadKind.Unknown)
            {
                await extracted.DisposeAsync();
                logger.LogInformation(
                    "Skipped unrecognisable zip entry {EntryName} in attachment {AttachmentName}",
                    entry.Key, fileName);
                continue;
            }

            result.Add(new ExtractedReportPayload(entryKind, extracted, entry.Key));
        }

        return false;
    }

    private static async Task ExtractGzipAsync(
        byte[] payload,
        string fileName,
        ReportPayloadLimits limits,
        List<ExtractedReportPayload> result,
        CancellationToken ct)
    {
        using var gzipSource = new MemoryStream(payload, writable: false);
        await using var gzip = new GZipStream(gzipSource, CompressionMode.Decompress);
        var decoded = await ReadBoundedAsync(gzip, limits, 0, fileName, ct);

        // Gzip is detected by magic bytes, so whatever was inside lands here
        // regardless of format — TLS reports arrive exactly this way
        // (application/tlsrpt+gzip). The filename fallback strips the .gz so
        // report.json.gz still label-classifies when the bytes are inconclusive.
        var innerName = StripGzipSuffix(fileName);
        var kind = ReportPayloadFormat.Classify(
            decoded.GetBuffer().AsSpan(0, (int)decoded.Length), innerName, null);

        // Unknown keeps the legacy route: gzip content that is neither format
        // always went to the DMARC parser, whose parse-failure accounting for
        // garbage is behavior operators already understand.
        result.Add(new ExtractedReportPayload(
            kind == ReportPayloadKind.Unknown ? ReportPayloadKind.DmarcAggregateXml : kind,
            decoded,
            fileName));
    }

    /// <summary>
    /// Copies a decompression stream into memory, refusing to read past the limits.
    /// <para>
    /// Deliberately not <c>CopyToAsync</c>. The point is to stop <em>while</em> reading:
    /// by the time a copy helper could report how much it moved, the memory is already
    /// allocated and the bomb has already gone off.
    /// </para>
    /// </summary>
    private static async Task<MemoryStream> ReadBoundedAsync(
        Stream source,
        ReportPayloadLimits limits,
        long alreadyExtracted,
        string what,
        CancellationToken ct)
    {
        var entryBudget = limits.MaxEntryBytes;
        var totalBudget = limits.MaxTotalBytes - alreadyExtracted;

        var destination = new MemoryStream();
        var buffer = new byte[81920];
        long written = 0;

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    break;
                }

                written += read;

                if (written > entryBudget)
                {
                    throw new ReportPayloadTooLargeException(
                        "Worker:MaxReportEntryBytes", limits.MaxEntryBytes,
                        $"{what} expanded past {limits.MaxEntryBytes} bytes");
                }

                if (written > totalBudget)
                {
                    throw new ReportPayloadTooLargeException(
                        "Worker:MaxReportAttachmentBytes", limits.MaxTotalBytes,
                        $"{what} took this attachment past {limits.MaxTotalBytes} expanded bytes");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch
        {
            await destination.DisposeAsync();
            throw;
        }

        destination.Position = 0;
        return destination;
    }

    /// <summary>report.json.gz → report.json, so the label fallback still applies inside gzip.</summary>
    private static string StripGzipSuffix(string fileName)
    {
        if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^3];
        }

        return fileName.EndsWith(".gzip", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^5]
            : fileName;
    }

    private static string GetAttachmentFileName(MimeEntity attachment)
        => attachment.ContentDisposition?.FileName
           ?? attachment.ContentType?.Name
           ?? "attachment";

    private static bool IsZip(byte[] payload)
        => payload.Length >= 4 && payload[0] == 0x50 && payload[1] == 0x4B &&
           (payload[2] == 0x03 || payload[2] == 0x05 || payload[2] == 0x07);

    private static bool IsGzip(byte[] payload)
        => payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B;
}
