namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// One policy block of a TLS report — the level tenancy and analytics work at,
/// because this is where the policy-domain (and so the client, through the
/// domain row) lives. The report window is denormalized here for the same
/// reason it is on <see cref="DmarcReportRecord"/>: window scans and retention
/// filter on it constantly and should never join to the report for it.
/// </summary>
public sealed class SmtpTlsReportPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SmtpTlsReportId { get; set; }
    public Guid DomainId { get; set; }

    /// <summary>sts, tlsa or no-policy-found (RFC 8460); unknown values kept raw.</summary>
    public string PolicyType { get; set; } = string.Empty;

    /// <summary>The policy-domain as reported, normalized lowercase — kept even though DomainId resolves it.</summary>
    public string PolicyDomain { get; set; } = string.Empty;

    /// <summary>The reporter's copy of the applied policy, newline-joined when it arrived as an array.</summary>
    public string? PolicyString { get; set; }

    /// <summary>mx-host / mx-host-pattern as reported, newline-joined.</summary>
    public string? MxHostPatterns { get; set; }

    public long SuccessfulSessionCount { get; set; }
    public long FailureSessionCount { get; set; }
    public DateTime ReportRangeBeginUtc { get; set; }
    public DateTime ReportRangeEndUtc { get; set; }

    public SmtpTlsReport? Report { get; set; }
    public Domain? Domain { get; set; }
    public ICollection<SmtpTlsFailureDetail> FailureDetails { get; set; } = [];
}
