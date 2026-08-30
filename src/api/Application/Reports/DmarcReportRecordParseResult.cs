namespace DmarcAnalyzer.Api.Application.Reports;

/// <summary>
/// One row of an aggregate report: a source IP and how its mail evaluated.
/// DkimResult/SpfResult are the policy-evaluated (aligned) verdicts; the raw
/// per-check outcomes live in the two auth-result lists.
/// </summary>
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

/// <summary>A raw DKIM verification (auth_results) as the reporter recorded it.</summary>
public sealed record DmarcReportRecordDkimAuthParseResult(
    string Domain,
    string Selector,
    string Result,
    string HumanResult);

/// <summary>A raw SPF check (auth_results) as the reporter recorded it.</summary>
public sealed record DmarcReportRecordSpfAuthParseResult(
    string Domain,
    string Scope,
    string Result,
    string HumanResult);
