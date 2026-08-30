namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>A mailbox_sync_run row as the console's history table reads it.</summary>
public sealed record MailboxSyncRunDto(
    Guid Id,
    Guid ReportSourceId,
    string Trigger,
    string Status,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    int MessagesScanned,
    int AttachmentsProcessed,
    int ReportsInserted,
    int ReportsSkippedAsDuplicate,
    int ParseFailures,
    int TlsReportsInserted,
    int TlsReportsSkippedAsDuplicate,
    string? Error,
    DateTime CreatedAtUtc);
