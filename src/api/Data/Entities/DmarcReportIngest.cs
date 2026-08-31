namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// The DMARC provenance ledger: one row per accepted report — keyed by
/// (ClientId, PolicyDomain, ReportId, range) — saying where it came from and
/// when. Survives report deletion so retention does not erase the record that
/// ingestion happened.
/// </summary>
public sealed class DmarcReportIngest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Guid ReportSourceId { get; set; }
    public string PolicyDomain { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;
    public DateTime ReportRangeBeginUtc { get; set; }
    public DateTime ReportRangeEndUtc { get; set; }
    public string OrganizationName { get; set; } = string.Empty;
    public int RecordCount { get; set; }
    public DateTime IngestedAtUtc { get; set; } = DateTime.UtcNow;

    public Client? Client { get; set; }
    public ReportSource? ReportSource { get; set; }
}
