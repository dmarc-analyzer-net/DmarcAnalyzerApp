namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// What one archived copy of a report said, staged by the backfill's collect pass.
/// <para>
/// Staged rather than applied on sight because copies of the same report can be archived
/// under different days — one per mailbox that received it — so no single object shows
/// all of them. Nothing is written to <c>dmarc_report</c> until every copy has been seen
/// and they agree; a report whose copies disagree is left alone and counted.
/// </para>
/// <para>
/// Keyed by (report, object key), so the same object seen again — a resumed collect pass
/// re-reading its last page — stages nothing twice. Cleared when the apply pass ends.
/// </para>
/// </summary>
public sealed class ReportBackfillCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DmarcReportId { get; set; }

    /// <summary>The archive object this copy came from.</summary>
    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>SHA-256 over the values below, so the apply pass can tell agreeing copies from conflicting ones without comparing columns.</summary>
    public string ValuesHash { get; set; } = string.Empty;

    // The dmarc_report columns the backfill writes, as the current parser read them.
    public string ReportFormat { get; set; } = string.Empty;
    public string? NonexistentSubdomainPolicy { get; set; }
    public bool? Testing { get; set; }
    public string? DiscoveryMethod { get; set; }
    public string? Generator { get; set; }
    /// <summary>JSON list of strings, as <c>dmarc_report.ReportErrors</c> stores it; null when none.</summary>
    public string? ReportErrors { get; set; }
    public int? PublishedPct { get; set; }
    /// <summary>JSON, as <c>dmarc_report.ParseMessages</c> stores it.</summary>
    public string ParseMessages { get; set; } = "[]";
    public int ParseWarningCount { get; set; }

    public DateTime CollectedAtUtc { get; set; } = DateTime.UtcNow;

    public DmarcReport? Report { get; set; }
}
