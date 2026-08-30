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
    IReadOnlyList<TlsRptMxHostStatDto> ByReceivingMx,
    /// <summary>
    /// The live `_smtp._tls` record. Here rather than on the record-inspection
    /// card because the counts above are unreadable without it: zero sessions
    /// means "no reporter was asked" when this is missing and "no reporter
    /// answered" when it is published, and those call for opposite advice.
    /// </summary>
    TlsRptRecordDto Record);

/// <summary>
/// What the promotion gate needs from TLS-RPT: did anyone report at all, and
/// did any STS-category sessions fail. No tenancy check — the caller has
/// already authorized the domain.
/// </summary>
public sealed record TlsRptGateSample(
    long TotalSessions,
    long StsFailureSessions,
    int ReportCount);

/// <summary>
/// Outcome of the `_smtp._tls.{domain}` TXT lookup. Carries the same extra
/// state MTA-STS needs: RFC 8460 §3 says that once records not beginning with
/// v=TLSRPTv1 are discarded, anything other than exactly one usable record
/// means senders must assume the domain does not implement TLS-RPT — which
/// must not read as "found". There is no `inherited`: TLS-RPT has no tree walk.
/// </summary>
public static class TlsRptRecordStatus
{
    public const string Found = RecordLookupStatus.Found;
    public const string Missing = RecordLookupStatus.Missing;
    public const string LookupFailed = RecordLookupStatus.LookupFailed;
    public const string Invalid = "invalid";
}

/// <summary>
/// The live `_smtp._tls` TXT record — the record that invites reporters. It is
/// what separates "nobody reported" from "nobody was asked": a domain without
/// one receives no TLS reports at all, however long the window. Rua holds the
/// usable destinations only; a scheme RFC 8460 doesn't define lands in Issues.
/// </summary>
public sealed record TlsRptRecordDto(
    string Status,
    string? Raw,
    IReadOnlyList<string> Rua,
    IReadOnlyList<string> Issues);
