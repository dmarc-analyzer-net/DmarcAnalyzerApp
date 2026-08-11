namespace DmarcAnalyzer.Api.Application.Reports;

/// <summary>
/// What a report attachment actually contains, decided from the bytes rather
/// than the filename — senders routinely misname attachments.
/// </summary>
public enum ReportPayloadKind
{
    /// <summary>Not something we recognise; ignore it.</summary>
    Unknown = 0,

    /// <summary>DMARC aggregate (RUA) XML — RFC 7489.</summary>
    DmarcAggregateXml,

    /// <summary>SMTP TLS report JSON — RFC 8460, parsed by <c>TlsRptReportParser</c>.</summary>
    SmtpTlsReportJson,
}

/// <summary>
/// Classifies a decoded attachment payload.
/// <para>
/// This exists because TLS reports were being counted as DMARC parse failures.
/// They arrive as I-JSON, normally gzipped as <c>application/tlsrpt+gzip</c>, and
/// the extraction path decompresses gzip by magic bytes without checking what
/// came out — so the JSON reached the DMARC parser, threw, and incremented the
/// parse-failure counter that marks a report source unhealthy in the console.
/// </para>
/// </summary>
public static class ReportPayloadFormat
{
    /// <summary>
    /// Classifies a payload. Content wins over filename and MIME type, which are
    /// only consulted when the bytes are inconclusive.
    /// </summary>
    public static ReportPayloadKind Classify(
        ReadOnlySpan<byte> payload,
        string? fileName = null,
        string? mimeType = null)
    {
        var first = FirstMeaningfulByte(payload);

        if (first == (byte)'<') return ReportPayloadKind.DmarcAggregateXml;
        if (first == (byte)'{') return ReportPayloadKind.SmtpTlsReportJson;

        var name = fileName ?? string.Empty;
        var mime = mimeType ?? string.Empty;

        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            mime.Contains("tlsrpt", StringComparison.OrdinalIgnoreCase) ||
            mime.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return ReportPayloadKind.SmtpTlsReportJson;
        }

        if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
            mime.Equals("text/xml", StringComparison.OrdinalIgnoreCase) ||
            mime.Equals("application/xml", StringComparison.OrdinalIgnoreCase))
        {
            return ReportPayloadKind.DmarcAggregateXml;
        }

        return ReportPayloadKind.Unknown;
    }

    /// <summary>First byte that isn't a UTF-8 BOM or leading whitespace; 0 if none.</summary>
    private static byte FirstMeaningfulByte(ReadOnlySpan<byte> payload)
    {
        var i = 0;
        if (payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF)
        {
            i = 3;
        }

        while (i < payload.Length &&
               (payload[i] == 0x20 || payload[i] == 0x09 || payload[i] == 0x0D || payload[i] == 0x0A))
        {
            i++;
        }

        return i < payload.Length ? payload[i] : (byte)0;
    }
}
