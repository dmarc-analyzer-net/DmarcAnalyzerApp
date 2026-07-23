namespace DmarcAnalyzer.Api.Application.Reports;

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
    string SubdomainPolicy,
    int PublishedPct,
    string DkimAlignment,
    string SpfAlignment);
