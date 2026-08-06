namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// Provenance ledger for TLS reports — the parallel of <see cref="DmarcReportIngest"/>,
/// deliberately a sibling table rather than a discriminator on it: the DMARC
/// ledger's five-column unique key and retention pass stay untouched, and a TLS
/// report's natural key differs anyway (no single policy domain, so the
/// organization name disambiguates report-id collisions across reporters).
/// Small and append-only; what answers "did we ever receive that report?" after
/// the purge has removed the report itself.
/// </summary>
public sealed class TlsReportIngest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Guid MailboxSourceId { get; set; }
    public string OrganizationName { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public DateTime ReportRangeBeginUtc { get; set; }
    public DateTime ReportRangeEndUtc { get; set; }

    /// <summary>Comma-joined normalized policy-domains, truncated at the column width — searchable with LIKE.</summary>
    public string PolicyDomains { get; set; } = string.Empty;

    public int PolicyCount { get; set; }
    public long TotalSuccessfulSessionCount { get; set; }
    public long TotalFailureSessionCount { get; set; }
    public string? ContactInfo { get; set; }
    public DateTime IngestedAtUtc { get; set; } = DateTime.UtcNow;

    public Client? Client { get; set; }
    public MailboxSource? MailboxSource { get; set; }
}
