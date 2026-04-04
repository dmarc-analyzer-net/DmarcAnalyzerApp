namespace DmarcAnalyzer.Api.Application.Ingestion;

public sealed record MailboxSourceHealthDto(
    Guid MailboxSourceId,
    string Name,
    bool IsActive,
    DateTime? LastSuccessSyncAtUtc,
    long? LastProcessedUid,
    long? LastProcessedUidValidity,
    string? LastRunStatus,
    DateTime? LastRunStartedAtUtc,
    DateTime? LastRunFinishedAtUtc,
    string? LastRunError,
    int? LastRunMessagesScanned,
    int? LastRunAttachmentsProcessed,
    int? LastRunReportsInserted,
    int? LastRunReportsSkippedAsDuplicate,
    int? LastRunParseFailures);
