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

/// <summary>
/// Everything one attachment yielded, plus whether the walk stopped early.
/// <para>
/// The entry cap is the one limit that does not refuse the attachment — it takes what it
/// has and ignores the rest — and the two callers need opposite things from that. Mail
/// cannot be re-delivered on request, so dropping the excess and logging it is right for
/// the sync loop. An HTTP client can split its payload and post again, so returning it a
/// silent partial success is not: <c>{inserted: 5, failed: 0}</c> for a twenty-entry
/// archive is indistinguishable from complete.
/// </para>
/// </summary>
public sealed record ExtractedPayloadSet(
    IReadOnlyList<ExtractedReportPayload> Payloads,
    bool ArchiveTruncated);
