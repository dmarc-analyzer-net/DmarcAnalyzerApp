namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>One sync attempt of one polled source: trigger, status, counters, and error if it failed.</summary>
public sealed class MailboxSyncRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReportSourceId { get; set; }
    /// <summary>scheduled (the worker loop), manual (the console button), or unknown.</summary>
    public string Trigger { get; set; } = "scheduled";

    /// <summary>running → success, partial (timed out mid-drain but checkpointed), or failed.</summary>
    public string Status { get; set; } = "running";
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }
    public int MessagesScanned { get; set; }
    public int AttachmentsProcessed { get; set; }
    public int ReportsInserted { get; set; }
    public int ReportsSkippedAsDuplicate { get; set; }
    public int ParseFailures { get; set; }
    public int TlsReportsInserted { get; set; }
    public int TlsReportsSkippedAsDuplicate { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ReportSource? ReportSource { get; set; }
}
