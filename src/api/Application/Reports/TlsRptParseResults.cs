namespace DmarcAnalyzer.Api.Application.Reports;

/// <summary>
/// One parsed SMTP TLS report (RFC 8460). Policies whose policy-domain was
/// missing are dropped with a validation message rather than failing the
/// report; the counts already summed here are what the report row stores.
/// </summary>
public sealed record TlsRptParseResult(
    string OrganizationName,
    string ReportId,
    string? ContactInfo,
    DateTime RangeBeginUtc,
    DateTime RangeEndUtc,
    IReadOnlyList<TlsRptPolicyParseResult> Policies,
    IReadOnlyList<string> ValidationMessages);

public sealed record TlsRptPolicyParseResult(
    string PolicyType,
    string PolicyDomain,
    string? PolicyString,
    string? MxHostPatterns,
    long SuccessfulSessionCount,
    long FailureSessionCount,
    IReadOnlyList<TlsRptFailureDetailParseResult> FailureDetails);

public sealed record TlsRptFailureDetailParseResult(
    string ResultType,
    string? SendingMtaIp,
    string? ReceivingMxHostname,
    string? ReceivingMxHelo,
    string? ReceivingIp,
    long FailedSessionCount,
    string? AdditionalInformation,
    string? FailureReasonCode);
