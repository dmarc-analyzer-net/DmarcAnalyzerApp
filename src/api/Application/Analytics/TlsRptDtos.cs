namespace DmarcAnalyzer.Api.Application.Analytics;

public sealed record TlsRptPolicyTypeStatDto(
    string PolicyType,
    long SuccessfulSessions,
    long FailedSessions);

public sealed record TlsRptCategoryStatDto(string Category, long FailedSessions);

public sealed record TlsRptFailureTypeStatDto(
    string ResultType,
    string Category,
    long FailedSessions);

public sealed record TlsRptMxHostStatDto(
    string ReceivingMxHostname,
    long FailedSessions,
    IReadOnlyList<string> ResultTypes);

/// <summary>
/// One domain's TLS-RPT view over a window: are sessions to it actually
/// encrypted, and when they fail, is that the domain's MTA-STS policy breaking
/// delivery (sts) or a receiving MX misconfigured (transport)?
/// </summary>
public sealed record TlsRptDomainSummaryDto(
    AnalyticsWindowDto Window,
    long TotalSessions,
    long SuccessfulSessions,
    long FailedSessions,
    double SuccessRate,
    int ReportCount,
    int ReporterCount,
    IReadOnlyList<TlsRptPolicyTypeStatDto> ByPolicyType,
    IReadOnlyList<TlsRptCategoryStatDto> FailuresByCategory,
    IReadOnlyList<TlsRptFailureTypeStatDto> FailuresByType,
    IReadOnlyList<TlsRptMxHostStatDto> ByReceivingMx);

/// <summary>
/// What the promotion gate needs from TLS-RPT: did anyone report at all, and
/// did any STS-category sessions fail. No tenancy check — the caller has
/// already authorized the domain.
/// </summary>
public sealed record TlsRptGateSample(
    long TotalSessions,
    long StsFailureSessions,
    int ReportCount);
