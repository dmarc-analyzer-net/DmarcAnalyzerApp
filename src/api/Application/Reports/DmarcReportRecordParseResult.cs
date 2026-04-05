namespace DmarcAnalyzer.Api.Application.Reports;

public sealed record DmarcReportRecordParseResult(
    string SourceIp,
    int MessageCount,
    string Disposition,
    string DkimResult,
    string SpfResult,
    string HeaderFrom,
    string EnvelopeFrom,
    string EnvelopeTo,
    IReadOnlyList<DmarcReportRecordDkimAuthParseResult> DkimAuthResults,
    IReadOnlyList<DmarcReportRecordSpfAuthParseResult> SpfAuthResults);

public sealed record DmarcReportRecordDkimAuthParseResult(
    string Domain,
    string Selector,
    string Result,
    string HumanResult);

public sealed record DmarcReportRecordSpfAuthParseResult(
    string Domain,
    string Scope,
    string Result,
    string HumanResult);
