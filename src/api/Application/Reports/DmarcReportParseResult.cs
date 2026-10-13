namespace DmarcAnalyzer.Api.Application.Reports;

/// <summary>One parsed RUA aggregate report: header, published policy, and its rows.</summary>
/// <param name="SubdomainPolicy">null when the reporter sent no sp tag — subdomains inherit p.</param>
/// <param name="PublishedPct">null when the reporter sent no pct tag, which RFC 9990 removed.</param>
/// <param name="ReportFormat">One of <see cref="DmarcReportFormats"/>, from the root namespace.</param>
/// <param name="NonexistentSubdomainPolicy">np; null when not sent. Real RFC 7489 reports send it too.</param>
/// <param name="Testing">testing (y = true); null when not sent.</param>
/// <param name="DiscoveryMethod">psl or treewalk; null when not sent.</param>
/// <param name="Generator">report_metadata/generator, trimmed and capped; null when not sent.</param>
/// <param name="ReportErrors">report_metadata/error values as sent; empty when none.</param>
/// <param name="HasValidationWarnings">Any warning-severity message. Info messages do not count.</param>
/// <param name="HasValidationErrors">Any schema validation error.</param>
/// <param name="ValidationMessages">Everything the parser had to say, deduplicated, in the order it was said.</param>
public sealed record DmarcReportParseResult(
    string OrganizationName,
    string ReportId,
    DateTime RangeBeginUtc,
    DateTime RangeEndUtc,
    string PolicyDomain,
    int RecordCount,
    IReadOnlyList<DmarcReportRecordParseResult> Records,
    bool HasValidationWarnings,
    bool HasValidationErrors,
    IReadOnlyList<DmarcReportParseMessage> ValidationMessages,
    string PublishedPolicy,
    string? SubdomainPolicy,
    int? PublishedPct,
    string DkimAlignment,
    string SpfAlignment,
    string ReportFormat,
    string? NonexistentSubdomainPolicy,
    bool? Testing,
    string? DiscoveryMethod,
    string? Generator,
    IReadOnlyList<string> ReportErrors);
