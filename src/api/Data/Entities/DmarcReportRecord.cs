namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class DmarcReportRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DmarcReportId { get; set; }
    public string SourceIp { get; set; } = string.Empty;
    public int MessageCount { get; set; }
    public string Disposition { get; set; } = string.Empty;
    public string DkimResult { get; set; } = string.Empty;
    public string SpfResult { get; set; } = string.Empty;
    public string HeaderFrom { get; set; } = string.Empty;
    public string EnvelopeFrom { get; set; } = string.Empty;
    public string EnvelopeTo { get; set; } = string.Empty;

    /// <summary>
    /// Copy of the parent report's <see cref="DmarcReport.RangeBeginUtc"/>, indexed so
    /// analytics can filter a window directly on this table.
    ///
    /// Every analytics query is scoped to a time window that lives on the report, and
    /// filtering through the navigation made Postgres hash-join the whole record table:
    /// a 30-day window selects ~3% of rows, yet all 5.3M were scanned, ~250ms a time,
    /// once per aggregate. Adding an index to dmarc_report did not help — the planner
    /// kept choosing the full scan. Denormalising the date is what lets the window
    /// become an index range scan.
    ///
    /// Written by ingestion alongside the row; never updated, because a report's range
    /// never changes after it is stored.
    /// </summary>
    public DateTime ReportRangeBeginUtc { get; set; }

    public DmarcReport? DmarcReport { get; set; }
    public ICollection<DmarcReportRecordDkimAuthResult> DkimAuthResults { get; set; } = new List<DmarcReportRecordDkimAuthResult>();
    public ICollection<DmarcReportRecordSpfAuthResult> SpfAuthResults { get; set; } = new List<DmarcReportRecordSpfAuthResult>();
}
