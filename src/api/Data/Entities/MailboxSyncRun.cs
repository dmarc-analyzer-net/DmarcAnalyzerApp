namespace DmarcAnalyzer.Api.Data.Entities;

public sealed class MailboxSyncRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MailboxSourceId { get; set; }
    public string Trigger { get; set; } = "scheduled";
    public string Status { get; set; } = "running";
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }
    public int MessagesScanned { get; set; }
    public int AttachmentsProcessed { get; set; }
    public int ReportsInserted { get; set; }
    public int ReportsSkippedAsDuplicate { get; set; }
    public int ParseFailures { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public MailboxSource? MailboxSource { get; set; }
}
