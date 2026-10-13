namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// One DMARC aggregate report. Deduped by the (DomainId, ReportId,
/// RangeBeginUtc, RangeEndUtc) unique index; deleting one cascades to its
/// records and their auth results.
/// </summary>
public sealed class DmarcReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }
    public Guid ReportSourceId { get; set; }
    public string OrganizationName { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public DateTime RangeBeginUtc { get; set; }
    public DateTime RangeEndUtc { get; set; }
    public int RecordCount { get; set; }
    public DateTime IngestedAtUtc { get; set; } = DateTime.UtcNow;

    // Published DMARC policy from this report's policy_published block.
    public string PublishedPolicy { get; set; } = "none";
    /// <summary>null when the reporter sent no sp tag — subdomains inherit p.</summary>
    public string? SubdomainPolicy { get; set; }
    /// <summary>
    /// null when the reporter sent no pct tag. RFC 9990 removed the tag, so that is every
    /// conformant RFC 9990 report. Rows stored before this became nullable hold 100, the
    /// value the parser used to invent for an absent tag.
    /// </summary>
    public int? PublishedPct { get; set; }
    public string DkimAlignment { get; set; } = "relaxed";
    public string SpfAlignment { get; set; } = "relaxed";

    /// <summary>
    /// <c>rfc7489</c> or <c>rfc9990</c> (<see cref="Application.Reports.DmarcReportFormats"/>), from the root
    /// namespace. null means the row was stored before this column existed, so every
    /// field below is "not recorded" rather than "not sent".
    /// </summary>
    public string? ReportFormat { get; set; }
    /// <summary><c>np</c>; null when the reporter sent none.</summary>
    public string? NonexistentSubdomainPolicy { get; set; }
    /// <summary><c>testing</c> (<c>y</c> = true); null when the reporter sent none.</summary>
    public bool? Testing { get; set; }
    /// <summary><c>discovery_method</c>: <c>psl</c> or <c>treewalk</c>; null when the reporter sent none.</summary>
    public string? DiscoveryMethod { get; set; }
    /// <summary><c>report_metadata/generator</c>, trimmed and capped; null when the reporter sent none.</summary>
    public string? Generator { get; set; }
    /// <summary><c>report_metadata/error</c> values as sent; null when the reporter sent none.</summary>
    public List<string>? ReportErrors { get; set; }

    public Domain? Domain { get; set; }
    public ReportSource? ReportSource { get; set; }
    public ICollection<DmarcReportRecord> Records { get; set; } = new List<DmarcReportRecord>();
}
