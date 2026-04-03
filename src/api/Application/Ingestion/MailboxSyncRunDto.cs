namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed record MailboxSyncRunDto(
    Guid Id,
    Guid MailboxSourceId,
    string Trigger,
    string Status,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    int MessagesScanned,
    int AttachmentsProcessed,
    int ReportsInserted,
    int ReportsSkippedAsDuplicate,
    int ParseFailures,
    string? Error,
    DateTime CreatedAtUtc);
