using System.IO.Compression;
using System.Text;
using MimeKit;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// Turns one stored object into something the shared extraction path can read.
/// <para>
/// A bucket is not a mailbox, and what lands in one depends entirely on who fills it. Two
/// shapes are common and they need opposite handling:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>A bare report file</b> — <c>.zip</c>, <c>.gz</c>, <c>.xml</c>, <c>.json</c> — which is
/// what a provider or an S3 delivery pipeline drops. There is no message, so one is stubbed
/// around it and the object becomes its single attachment. That is the same trick the pushed
/// ingestion endpoint already plays on a raw request body, and it is what lets one extractor
/// serve every source instead of growing a second route.
/// </description></item>
/// <item><description>
/// <b>A whole RFC822 message</b> — what SES's "deliver to S3" writes, and what this
/// application's own report-mail archive writes, gzipped. Wrapping one of those as an
/// attachment would hand the extractor an <c>.eml</c> where it expects XML, and every object
/// would count as a parse failure. So it is parsed as mail and its own attachments are used,
/// exactly as if it had come from a mailbox.
/// </description></item>
/// </list>
/// <para>
/// The second case is the reason the gzip sniff bothers to look inside: without it, a bucket
/// full of this application's own <c>.eml.gz</c> archive — the most obviously useful thing to
/// point an S3 source at — would ingest nothing at all.
/// </para>
/// </summary>
public static class PolledObjectContent
{
    /// <summary>
    /// How much of an object to look at before deciding what it is. A header block is at the
    /// very front of a message, so this needs to cover the first few headers and nothing
    /// more — and keeping it small is also what makes the gzip sniff cheap, since deciding
    /// costs only this much decompression rather than all of it.
    /// </summary>
    private const int SniffBytes = 8 * 1024;

    /// <summary>
    /// Headers that mean "this is mail" rather than "this happens to start with a colon".
    /// A DMARC report is XML or JSON, neither of which can open with a header field, so the
    /// first-line check does most of the work; this is what stops a stray text file that
    /// begins <c>Domain: acme.test</c> from being parsed as a message and silently dropped.
    /// </summary>
    private static readonly string[] MessageHeaders =
        ["received:", "from:", "message-id:", "mime-version:", "date:", "subject:", "return-path:"];

    /// <param name="maxMessageBytes">
    /// Cap on a gzipped object decompressed for parsing as mail. The bytes arrived from a
    /// bucket somebody else fills, so the expansion has to be bounded here for the same
    /// reason the extractor bounds its own — this path decompresses <em>before</em> the
    /// extractor's limits get a say.
    /// </param>
    public static MimeMessage ToMessage(
        byte[] content, string key, DateTime lastModifiedUtc, long maxMessageBytes)
    {
        if (LooksLikeMessage(content))
        {
            return Parse(content, key, lastModifiedUtc);
        }

        if (IsGzip(content) && LooksLikeMessage(PeekGzip(content, SniffBytes)))
        {
            return Parse(Gunzip(content, maxMessageBytes, key), key, lastModifiedUtc);
        }

        return Wrap(content, key, lastModifiedUtc);
    }

    /// <summary>
    /// A stub message carrying the object as its only attachment.
    /// <para>
    /// Deliberately almost empty. It would be easy to fill in a <c>From</c> and a
    /// <c>Subject</c> that make the archived copy look like mail, and it would be a lie —
    /// nobody sent this. The only two things written are facts the bucket actually told us:
    /// when the object was last modified, and what it is called.
    /// </para>
    /// </summary>
    private static MimeMessage Wrap(byte[] content, string key, DateTime lastModifiedUtc)
    {
        var message = new MimeMessage { Date = new DateTimeOffset(lastModifiedUtc, TimeSpan.Zero) };
        message.Headers.Add("X-DmarcAnalyzer-Object-Key", key);

        message.Body = new MimePart(MediaTypeFor(key))
        {
            Content = new MimeContent(new MemoryStream(content, writable: false)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
            {
                FileName = FileNameFor(key),
            },
            ContentTransferEncoding = ContentEncoding.Binary,
        };

        return message;
    }

    private static MimeMessage Parse(byte[] content, string key, DateTime lastModifiedUtc)
    {
        using var stream = new MemoryStream(content, writable: false);
        var message = MimeMessage.Load(stream);

        // Recorded whatever the message says, because the two answer different questions:
        // the Date header is when the sender claims to have sent it, and this is when the
        // object landed in the bucket. The retention pass judges age on the latter.
        message.Headers.Add("X-DmarcAnalyzer-Object-Key", key);

        if (message.Date == default)
        {
            message.Date = new DateTimeOffset(lastModifiedUtc, TimeSpan.Zero);
        }

        return message;
    }

    /// <summary>
    /// Whether these bytes open like an RFC822 message: a header field on the first line, and
    /// at least one header that only mail has.
    /// </summary>
    public static bool LooksLikeMessage(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0 || !StartsWithHeaderField(content))
        {
            return false;
        }

        var head = Encoding.ASCII.GetString(content[..Math.Min(content.Length, SniffBytes)]);

        foreach (var line in head.Split('\n'))
        {
            var trimmed = line.TrimStart();

            // A blank line ends the header block. Anything after it is the body, and a
            // header name found there proves nothing.
            if (trimmed.Length == 0)
            {
                return false;
            }

            foreach (var header in MessageHeaders)
            {
                if (trimmed.StartsWith(header, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// A field name followed by a colon, per RFC 5322: printable ASCII excluding the colon
    /// itself. This is what rules out XML and JSON in one step — neither can start this way.
    /// </summary>
    private static bool StartsWithHeaderField(ReadOnlySpan<byte> content)
    {
        var nameLength = 0;
        foreach (var b in content)
        {
            if (b == (byte)':')
            {
                return nameLength > 0;
            }

            if (b is < 33 or > 126)
            {
                return false;
            }

            // Bounded so a long line of printable junk is rejected rather than scanned:
            // no real header name comes near this.
            if (++nameLength > 64)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsGzip(ReadOnlySpan<byte> content)
        => content.Length >= 2 && content[0] == 0x1f && content[1] == 0x8b;

    /// <summary>
    /// The first <paramref name="count"/> decompressed bytes, or empty if the object does not
    /// decompress. Bounded by construction: deciding what an object is must not cost the
    /// whole expansion, or the sniff becomes the bomb.
    /// </summary>
    private static byte[] PeekGzip(byte[] content, int count)
    {
        try
        {
            using var source = new MemoryStream(content, writable: false);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);

            var buffer = new byte[count];
            var read = gzip.ReadAtLeast(buffer, count, throwOnEndOfStream: false);

            return read == count ? buffer : buffer[..read];
        }
        catch (InvalidDataException)
        {
            // Gzip magic bytes on something that is not gzip. Treated as "not a message",
            // which sends it down the wrap path where the extractor gets the same say.
            return [];
        }
    }

    private static byte[] Gunzip(byte[] content, long maxBytes, string key)
    {
        using var source = new MemoryStream(content, writable: false);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();

        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maxBytes)
            {
                throw new ReportPayloadTooLargeException(
                    "Worker:MaxReportAttachmentBytes", maxBytes, $"object {key} decompressed as a message");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>The key's last segment, which is what a person would call the file.</summary>
    public static string FileNameFor(string key)
    {
        var lastSlash = key.LastIndexOf('/');
        var name = lastSlash >= 0 && lastSlash < key.Length - 1 ? key[(lastSlash + 1)..] : key;

        return string.IsNullOrWhiteSpace(name) ? "object" : name;
    }

    /// <summary>
    /// A content type for the stub, from the key's suffix. Only a hint: the extractor
    /// classifies on magic bytes and falls back to the name, precisely because senders
    /// mislabel things, so getting this wrong costs nothing.
    /// </summary>
    private static ContentType MediaTypeFor(string key)
    {
        var name = FileNameFor(key).ToLowerInvariant();

        return name switch
        {
            _ when name.EndsWith(".zip") => new ContentType("application", "zip"),
            _ when name.EndsWith(".gz") => new ContentType("application", "gzip"),
            _ when name.EndsWith(".xml") => new ContentType("text", "xml"),
            _ when name.EndsWith(".json") => new ContentType("application", "json"),
            _ => new ContentType("application", "octet-stream"),
        };
    }
}
