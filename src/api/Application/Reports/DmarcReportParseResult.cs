namespace DmarcAnalyzer.Api.Application.Reports;

/// <summary>One parsed RUA aggregate report: header, published policy, and its rows.</summary>
/// <param name="SubdomainPolicy">null when the reporter sent no sp tag — subdomains inherit p.</param>
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
    IReadOnlyList<string> ValidationMessages,
    string PublishedPolicy,
    string? SubdomainPolicy,
    int PublishedPct,
    string DkimAlignment,
    string SpfAlignment);
