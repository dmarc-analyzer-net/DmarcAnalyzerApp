using System.IO.Compression;
using System.Text;
using DmarcAnalyzer.Api.Application.Ingestion;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Deciding what one stored object actually is.
/// <para>
/// The two shapes need opposite handling and there is no metadata that reliably tells them
/// apart — a bucket filled by an SES delivery rule holds whole messages, one filled by a
/// provider holds bare report files, and the key suffix is a hint at best. Get it backwards
/// either way and the failure is quiet: an <c>.eml</c> handed to the payload extractor counts
/// as a parse failure on every object, and a report file parsed as mail yields a message with
/// no attachments and ingests nothing.
/// </para>
/// </summary>
public sealed class PolledObjectContentTests
{
    private const long Unbounded = 64 * 1024 * 1024;

    private static byte[] Gzip(byte[] content)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return compressed.ToArray();
    }

    private const string Xml =
        """<?xml version="1.0" encoding="UTF-8" ?><feedback><report_metadata /></feedback>""";

    private static readonly string Eml = string.Join("\r\n",
        "Return-Path: <noreply@google.com>",
        "From: noreply@google.com",
        "To: rua@acme.test",
        "Subject: Report domain: acme.test",
        "Date: Fri, 1 Aug 2026 00:00:00 +0000",
        "MIME-Version: 1.0",
        "Content-Type: text/plain",
        "",
        "Report attached.");

    [Fact]
    public void ABareXmlObjectBecomesAnAttachment()
    {
        var message = PolledObjectContent.ToMessage(
            Encoding.UTF8.GetBytes(Xml), "reports/acme.xml", DateTime.UtcNow, Unbounded);

        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("acme.xml", attachment.ContentDisposition?.FileName);
    }

    [Fact]
    public void AZippedReportBecomesAnAttachmentRatherThanBeingUnpacked()
    {
        // Unpacking is the extractor's job, and it is bounded there. This step only has to
        // hand it the bytes.
        var message = PolledObjectContent.ToMessage(
            [0x50, 0x4b, 0x03, 0x04, 0x00], "reports/acme.zip", DateTime.UtcNow, Unbounded);

        Assert.Single(message.Attachments);
    }

    [Fact]
    public void AWholeMessageIsParsedSoItsOwnAttachmentsAreUsed()
    {
        var message = PolledObjectContent.ToMessage(
            Encoding.ASCII.GetBytes(Eml), "ses/abc123", DateTime.UtcNow, Unbounded);

        Assert.Equal("Report domain: acme.test", message.Subject);
        Assert.Equal("noreply@google.com", message.From.Mailboxes.Single().Address);
    }

    /// <summary>
    /// The case that makes pointing a source at this application's own archive work at all —
    /// it writes <c>.eml.gz</c>. Without looking inside the gzip, every object in it would be
    /// handed to the extractor as a compressed blob of headers and counted as a parse failure.
    /// </summary>
    [Fact]
    public void AGzippedMessageIsUnpackedAndParsedAsMail()
    {
        var message = PolledObjectContent.ToMessage(
            Gzip(Encoding.ASCII.GetBytes(Eml)), "archive/2026/08/01/x.eml.gz", DateTime.UtcNow, Unbounded);

        Assert.Equal("Report domain: acme.test", message.Subject);
    }

    /// <summary>
    /// And the mirror of it: a gzipped <em>report</em> must not be unpacked here. It stays
    /// compressed and goes to the extractor, which is where decompression is bounded.
    /// </summary>
    [Fact]
    public void AGzippedReportIsLeftCompressedForTheExtractor()
    {
        var gzipped = Gzip(Encoding.UTF8.GetBytes(Xml));

        var message = PolledObjectContent.ToMessage(
            gzipped, "reports/acme.xml.gz", DateTime.UtcNow, Unbounded);

        var attachment = Assert.Single(message.Attachments);
        Assert.Equal("acme.xml.gz", attachment.ContentDisposition?.FileName);
    }

    /// <summary>
    /// A text file whose first line happens to look like a header field is not mail. Parsing
    /// it as mail would produce a message with no attachments, which ingests nothing and
    /// reports no error — the worst of the available outcomes.
    /// </summary>
    [Theory]
    [InlineData("Domain: acme.test\nPolicy: reject\n")]
    [InlineData("{\"organization-name\":\"google.com\"}")]
    [InlineData("<?xml version=\"1.0\"?><feedback/>")]
    [InlineData("")]
    public void SomethingThatIsNotMailIsNotParsedAsMail(string content)
    {
        var message = PolledObjectContent.ToMessage(
            Encoding.UTF8.GetBytes(content), "reports/thing", DateTime.UtcNow, Unbounded);

        // MimeKit answers "" rather than null for a header that was never written.
        Assert.True(string.IsNullOrEmpty(message.Subject));
        Assert.Single(message.Attachments);
    }

    /// <summary>
    /// A header name found in the body proves nothing — the blank line ends the header block,
    /// and anything after it is content that happens to contain a colon.
    /// </summary>
    [Fact]
    public void AHeaderNameInTheBodyDoesNotMakeItMail()
    {
        var content = "Domain: acme.test\n\nFrom: this is prose, not a header\n";

        var message = PolledObjectContent.ToMessage(
            Encoding.UTF8.GetBytes(content), "reports/notes.txt", DateTime.UtcNow, Unbounded);

        Assert.Single(message.Attachments);
    }

    /// <summary>
    /// The stub carries the object's last-modified date and its key, and nothing else. It
    /// would be easy to invent a From and a Subject that make the archived copy look like
    /// mail; nobody sent this, so it does not claim anyone did.
    /// </summary>
    [Fact]
    public void TheStubCarriesTheFactsAndInventsNothing()
    {
        var at = new DateTime(2026, 8, 1, 6, 30, 0, DateTimeKind.Utc);

        var message = PolledObjectContent.ToMessage(
            Encoding.UTF8.GetBytes(Xml), "reports/acme.xml", at, Unbounded);

        Assert.Equal(at, message.Date.UtcDateTime);
        Assert.Equal("reports/acme.xml", message.Headers["X-DmarcAnalyzer-Object-Key"]);
        Assert.True(string.IsNullOrEmpty(message.Subject));
        Assert.Empty(message.From);
    }

    /// <summary>
    /// This path decompresses before the extractor's limits get a say, so it needs its own.
    /// The bytes came from a bucket somebody else fills.
    /// </summary>
    [Fact]
    public void AGzippedMessageThatExpandsPastTheCapIsRefused()
    {
        var big = Eml + "\r\n" + new string('a', 200_000);

        var ex = Assert.Throws<ReportPayloadTooLargeException>(() => PolledObjectContent.ToMessage(
            Gzip(Encoding.ASCII.GetBytes(big)), "archive/big.eml.gz", DateTime.UtcNow, maxMessageBytes: 4096));

        Assert.Equal("Worker:MaxReportAttachmentBytes", ex.LimitName);
    }

    /// <summary>
    /// Gzip magic bytes on something that is not gzip must not throw out of the sniff. It is
    /// a corrupt object, and the extractor is where that gets reported as a parse failure.
    /// </summary>
    [Fact]
    public void SomethingClaimingToBeGzipButIsNotStillGetsWrapped()
    {
        var message = PolledObjectContent.ToMessage(
            [0x1f, 0x8b, 0x00, 0x01, 0x02, 0x03], "reports/broken.gz", DateTime.UtcNow, Unbounded);

        Assert.Single(message.Attachments);
    }

    [Theory]
    [InlineData("reports/2026/08/acme.xml", "acme.xml")]
    [InlineData("acme.xml", "acme.xml")]
    [InlineData("reports/", "reports/")]
    public void TheFileNameIsTheKeysLastSegment(string key, string expected)
        => Assert.Equal(expected, PolledObjectContent.FileNameFor(key));
}
