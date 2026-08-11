namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// One SMTP TLS report (RFC 8460) as a reporter sent it. Deliberately carries
/// no DomainId: a single report can hold policies for several policy-domains,
/// possibly belonging to different clients — tenancy and analytics hang off the
/// per-policy rows instead.
/// </summary>
public sealed class SmtpTlsReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReportSourceId { get; set; }
    public string OrganizationName { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public string? ContactInfo { get; set; }
    public DateTime RangeBeginUtc { get; set; }
    public DateTime RangeEndUtc { get; set; }
    public int PolicyCount { get; set; }

    /// <summary>Denormalized sums over the policies, for list views that never need the children.</summary>
    public long TotalSuccessfulSessionCount { get; set; }

    public long TotalFailureSessionCount { get; set; }
    public DateTime IngestedAtUtc { get; set; } = DateTime.UtcNow;

    public ReportSource? ReportSource { get; set; }
    public ICollection<SmtpTlsReportPolicy> Policies { get; set; } = [];
}
