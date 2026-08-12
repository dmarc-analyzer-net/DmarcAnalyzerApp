using System.Text.Json;
using System.Xml;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data.Entities;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// What became of one extracted payload. <see cref="Format"/> is what the bytes turned out
/// to be, which is not always what the caller thought it was sending.
/// </summary>
public enum ReportPayloadOutcome
{
    Inserted,
    Duplicate,

    /// <summary>Refused: the report is for a domain this source may not ingest for.</summary>
    ForeignDomainRefused,
}

public sealed record ReportPayloadIngestResult(string Format, ReportPayloadOutcome Outcome)
{
    public const string Dmarc = "dmarc";
    public const string Tls = "tls";
}

public interface IReportPayloadIngestor
{
    /// <summary>
    /// Parses one extracted payload according to what its bytes are, and stores it through
    /// the ingestor for that format.
    /// </summary>
    Task<ReportPayloadIngestResult> IngestAsync(
        ExtractedReportPayload payload, ReportSource source, CancellationToken ct);
}

/// <summary>
/// The step between "these bytes are a report of some kind" and "it is stored": choosing
/// the parser and the ingestor for a payload's format.
/// <para>
/// It exists because there are now two callers — the mailbox worker and the HTTP endpoint —
/// and this decision was written out in both. Two copies that currently agree is not the
/// same as one decision: a third format, or a change to how an ambiguous payload is
/// treated, would have to be made twice, and nothing fails if only one is changed. The
/// contributor who proposed the push endpoint asked for exactly this seam, for exactly
/// that reason, before either caller existed.
/// </para>
/// <para>
/// Deliberately not responsible for extraction, deduplication or transactions. Those
/// belong to the extractor and the two format ingestors respectively; this only routes —
/// and refuses anything that is not a whole document, for the reason on
/// <see cref="EnsureWellFormed"/>.
/// </para>
/// </summary>
public sealed class ReportPayloadIngestor(
    IDmarcReportParser dmarcParser,
    ITlsRptReportParser tlsParser,
    IDmarcReportIngestor dmarcIngestor,
    ITlsReportIngestor tlsIngestor) : IReportPayloadIngestor
{
    public async Task<ReportPayloadIngestResult> IngestAsync(
        ExtractedReportPayload payload, ReportSource source, CancellationToken ct)
    {
        if (payload.Kind == ReportPayloadKind.SmtpTlsReportJson)
        {
            EnsureWellFormed(payload, IsWellFormedJson);

            var parsed = tlsParser.Parse(payload.Stream);
            var outcome = await tlsIngestor.IngestAsync(parsed, source, ct);
            return new ReportPayloadIngestResult(ReportPayloadIngestResult.Tls, outcome switch
            {
                TlsReportIngestOutcome.Inserted => ReportPayloadOutcome.Inserted,
                TlsReportIngestOutcome.ForeignDomainRefused => ReportPayloadOutcome.ForeignDomainRefused,
                _ => ReportPayloadOutcome.Duplicate,
            });
        }

        // Everything else goes to the DMARC parser, including a payload whose format could
        // not be determined. That is the long-standing behaviour and it is deliberate: the
        // DMARC parser's failure accounting for garbage is what operators already know how
        // to read, and inventing a third outcome for "unrecognisable" would change what a
        // parse-failure count means.
        EnsureWellFormed(payload, IsWellFormedXml);

        var report = dmarcParser.Parse(payload.Stream);
        var dmarcOutcome = await dmarcIngestor.IngestAsync(report, source, ct);
        return new ReportPayloadIngestResult(ReportPayloadIngestResult.Dmarc, dmarcOutcome switch
        {
            DmarcReportIngestOutcome.Inserted => ReportPayloadOutcome.Inserted,
            DmarcReportIngestOutcome.ForeignDomainRefused => ReportPayloadOutcome.ForeignDomainRefused,
            _ => ReportPayloadOutcome.Duplicate,
        });
    }

    /// <summary>
    /// Refuses a payload that is not a complete document, before any parser sees it.
    /// <para>
    /// Truncation is silent, which is what makes this necessary. Deflate is a stream
    /// format, so half a gzip decompresses cleanly into half a document and the
    /// decompressor reports no error at all. The DMARC parser is then lenient enough to
    /// build a report out of the surviving header — one carrying the real report id and
    /// reporting window, and no records, because the records were in the half that was
    /// lost.
    /// </para>
    /// <para>
    /// That is the damaging part. Deduplication keys on exactly the domain, report id and
    /// window that survived, so the complete report arriving afterwards is rejected as a
    /// duplicate. The truncated one wins permanently and the records are never stored —
    /// the same shape as the report-and-records bug this ingestion path already had once.
    /// </para>
    /// <para>
    /// Checked here rather than in the extractor because "are these bytes a whole
    /// document" is a question about the format, and this is where the format is decided.
    /// The stream is rewound afterwards so the parser reads it from the start.
    /// </para>
    /// </summary>
    private static void EnsureWellFormed(ExtractedReportPayload payload, Func<Stream, bool> isWellFormed)
    {
        payload.Stream.Position = 0;
        var wellFormed = isWellFormed(payload.Stream);
        payload.Stream.Position = 0;

        if (!wellFormed)
        {
            throw new InvalidDataException(
                $"{payload.SourceName} is not a complete document — it was probably truncated in transit");
        }
    }

    private static bool IsWellFormedXml(Stream stream)
    {
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                // Report XML arrives from the internet, so no DTD and no resolver: the
                // reader must not be talked into fetching anything.
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                CloseInput = false,
            });

            while (reader.Read())
            {
            }

            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool IsWellFormedJson(Stream stream)
    {
        try
        {
            using var _ = JsonDocument.Parse(stream);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
