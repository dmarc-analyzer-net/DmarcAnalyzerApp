namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// One sync's counters, as returned to the caller that triggered it. The same
/// numbers land on the mailbox_sync_run row; duplicates and parse failures are
/// counted, never errors.
/// </summary>
public sealed record MailboxSyncResult(
    Guid ReportSourceId,
    int MessagesScanned,
    int AttachmentsProcessed,
    int ReportsInserted,
    int ReportsSkippedAsDuplicate,
    int TlsReportsInserted,
    int TlsReportsSkippedAsDuplicate,
    int ParseFailures,
    bool Success,
    string? Error,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc);
