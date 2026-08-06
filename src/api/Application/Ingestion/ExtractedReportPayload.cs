using DmarcAnalyzer.Api.Application.Reports;

namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// One report payload pulled out of a mail attachment, typed by what the bytes
/// turned out to be — the extraction's job ends at "what is this", and the sync
/// loop routes each kind to its parser. SourceName is the zip entry key or
/// attachment filename, for log lines only.
/// </summary>
public sealed record ExtractedReportPayload(
    ReportPayloadKind Kind,
    MemoryStream Stream,
    string SourceName);
