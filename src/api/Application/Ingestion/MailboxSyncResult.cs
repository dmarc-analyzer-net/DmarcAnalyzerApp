namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed record MailboxSyncResult(
    Guid MailboxSourceId,
    int MessagesScanned,
    int AttachmentsProcessed,
    int ReportsInserted,
    int ReportsSkippedAsDuplicate,
    int ParseFailures,
    bool Success,
    string? Error,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc);
