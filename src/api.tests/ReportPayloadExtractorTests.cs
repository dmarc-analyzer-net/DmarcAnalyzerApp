using System.IO.Compression;
using System.Text;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Application.Reports;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The bombs here are real ones, built in the test rather than described in a comment.
/// A limit that is only asserted against a mock of itself proves nothing, and the whole
/// value of this file is that the bytes it feeds the extractor are the bytes a hostile
/// sender would post to a published <c>rua=mailto:</c> address.
/// </summary>
public class ReportPayloadExtractorTests
{
    private static readonly ReportPayloadLimits Tiny = new(
        MaxEntryBytes: 64 * 1024, MaxTotalBytes: 128 * 1024, MaxEntries: 8);

    private const string SmallReport =
        """<?xml version="1.0"?><feedback><report_metadata><org_name>acme</org_name></report_metadata></feedback>""";

    [Fact]
    public async Task GzipBombIsRefusedInsteadOfExpanded()
    {
        // 64 MB of zeroes compresses to a few KB. Unbounded, this allocates 64 MB from
        // an attachment small enough to look unremarkable in a mailbox.
        var attachment = GzipAttachment("report.xml.gz", new byte[64 * 1024 * 1024]);

        var ex = await Assert.ThrowsAsync<ReportPayloadTooLargeException>(
            () => ExtractAsync(attachment, Tiny));

        Assert.Equal("Worker:MaxReportEntryBytes", ex.LimitName);
    }

    [Fact]
    public async Task ZipBombIsRefusedAndNothingFromThatAttachmentIsKept()
    {
        // A good entry first, then a bomb. The good one must not survive: an archive
        // that has already tried this is not one to take partial results from.
        var attachment = ZipAttachment("reports.zip",
            ("good.xml", Encoding.UTF8.GetBytes(SmallReport)),
            ("bomb.xml", new byte[64 * 1024 * 1024]));

        // Rethrown, not swallowed: the sync loop counts it a parse failure, so a refused
        // bomb leaves a trace in the run statistics instead of looking like a quiet no-op.
        var ex = await Assert.ThrowsAsync<ReportPayloadTooLargeException>(
            () => ExtractAsync(attachment, Tiny));

        Assert.Equal("Worker:MaxReportEntryBytes", ex.LimitName);
    }

    [Fact]
    public async Task ManyEntriesEachUnderTheEntryCapStillCannotExceedTheTotal()
    {
        // Every entry is legal on its own. Only the total catches this, which is why
        // the per-entry cap alone would not be enough.
        var entries = Enumerable.Range(0, 8)
            .Select(i => ($"r{i}.xml", new byte[32 * 1024]))
            .ToArray();

        var ex = await Assert.ThrowsAsync<ReportPayloadTooLargeException>(
            () => ExtractAsync(ZipAttachment("many.zip", entries), Tiny));

        Assert.Equal("Worker:MaxReportAttachmentBytes", ex.LimitName);
    }

    [Fact]
    public async Task EntryCountIsBoundedEvenWhenEveryEntryIsTiny()
    {
        // Nothing here is large; the cost is the walk. Entries past the cap are ignored
        // and the ones already read are kept, because no limit on size was breached.
        var entries = Enumerable.Range(0, 50)
            .Select(i => ($"r{i}.xml", Encoding.UTF8.GetBytes(SmallReport)))
            .ToArray();

        var payloads = await ExtractAsync(ZipAttachment("many.zip", entries), Tiny);

        Assert.True(payloads.Count <= Tiny.MaxEntries);
    }

    [Fact]
    public async Task OrdinaryZippedReportStillExtracts()
    {
        var attachment = ZipAttachment("reports.zip", ("report.xml", Encoding.UTF8.GetBytes(SmallReport)));

        var payloads = await ExtractAsync(attachment, Tiny);

        var payload = Assert.Single(payloads);
        Assert.Equal(ReportPayloadKind.DmarcAggregateXml, payload.Kind);
        Assert.Equal(SmallReport.Length, payload.Stream.Length);
    }

    [Fact]
    public async Task OrdinaryGzippedReportStillExtracts()
    {
        var attachment = GzipAttachment("report.xml.gz", Encoding.UTF8.GetBytes(SmallReport));

        var payloads = await ExtractAsync(attachment, Tiny);

        var payload = Assert.Single(payloads);
        Assert.Equal(ReportPayloadKind.DmarcAggregateXml, payload.Kind);
        Assert.Equal(SmallReport.Length, payload.Stream.Length);
    }

    [Fact]
    public async Task PayloadExactlyOnTheEntryLimitIsAccepted()
    {
        // The limit is a ceiling, not a fencepost bug. One byte of slack here would mean
        // a legitimate report at exactly the configured size gets dropped.
        var exact = new byte[Tiny.MaxEntryBytes];
        Encoding.UTF8.GetBytes(SmallReport).CopyTo(exact, 0);

        var payloads = await ExtractAsync(GzipAttachment("report.xml.gz", exact), Tiny);

        var payload = Assert.Single(payloads);
        Assert.Equal(Tiny.MaxEntryBytes, payload.Stream.Length);
    }

    [Fact]
    public async Task UncompressedAttachmentIsUnaffectedByTheLimits()
    {
        var attachment = new MimePart("application", "xml")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(SmallReport))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "report.xml" },
        };

        var payloads = await ExtractAsync(attachment, Tiny);

        Assert.Single(payloads);
    }

    private static async Task<IReadOnlyList<ExtractedReportPayload>> ExtractAsync(
        MimeEntity attachment, ReportPayloadLimits limits)
        => await ReportPayloadExtractor.ExtractAsync(
            attachment, limits, NullLogger.Instance, CancellationToken.None);

    private static MimePart GzipAttachment(string fileName, byte[] content)
    {
        var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content);
        }

        compressed.Position = 0;
        return new MimePart("application", "gzip")
        {
            Content = new MimeContent(compressed),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
        };
    }

    private static MimePart ZipAttachment(string fileName, params (string Name, byte[] Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var entryStream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                entryStream.Write(content);
            }
        }

        buffer.Position = 0;
        return new MimePart("application", "zip")
        {
            Content = new MimeContent(buffer),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
        };
    }
}
