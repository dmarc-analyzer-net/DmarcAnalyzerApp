namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// One failure-details row of a TLS report policy: a result type, where it was
/// seen, and how many sessions it cost. The category is computed at ingest
/// (<c>TlsRptFailureClassifier</c>) and stored so the STS-vs-transport split —
/// the question that gates promoting MTA-STS to enforce — is one GROUP BY, while
/// the raw result type survives for re-bucketing.
/// </summary>
public sealed class SmtpTlsFailureDetail
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SmtpTlsReportPolicyId { get; set; }

    /// <summary>The reporter's result-type, lowercased but otherwise raw — RFC 8460 has no closed registry.</summary>
    public string ResultType { get; set; } = string.Empty;

    /// <summary>sts, dane, transport or other.</summary>
    public string FailureCategory { get; set; } = string.Empty;

    public string? SendingMtaIp { get; set; }
    public string? ReceivingMxHostname { get; set; }
    public string? ReceivingMxHelo { get; set; }
    public string? ReceivingIp { get; set; }
    public long FailedSessionCount { get; set; }
    public string? AdditionalInformation { get; set; }
    public string? FailureReasonCode { get; set; }

    public SmtpTlsReportPolicy? Policy { get; set; }
}
