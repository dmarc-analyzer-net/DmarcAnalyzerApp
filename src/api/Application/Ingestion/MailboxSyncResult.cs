namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed record MailboxSyncResult(
    Guid MailboxSourceId,
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
