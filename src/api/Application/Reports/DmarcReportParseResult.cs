namespace DmarcAnalyzer.Api.Application.Reports;

public sealed record DmarcReportParseResult(
    string OrganizationName,
    string ReportId,
    DateTime RangeBeginUtc,
    DateTime RangeEndUtc,
    string PolicyDomain,
    int RecordCount,
    bool HasValidationWarnings,
    bool HasValidationErrors,
    IReadOnlyList<string> ValidationMessages);
