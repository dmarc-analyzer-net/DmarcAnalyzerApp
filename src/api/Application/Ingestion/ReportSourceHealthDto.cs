namespace DmarcAnalyzer.Api.Application.Ingestion;

/// <summary>
/// One polled source's health row: the protocol-specific checkpoint (IMAP
/// UID+UIDVALIDITY, POP3 UIDL, or S3 object key — the unused ones are null)
/// plus the latest run's status and counters.
/// </summary>
public sealed record ReportSourceHealthDto(
    Guid ReportSourceId,
    string Name,
    bool IsActive,
    DateTime? LastSuccessSyncAtUtc,
    long? LastProcessedUid,
    long? LastProcessedUidValidity,
    string? LastProcessedUidl,
    string? LastProcessedObjectKey,
    string? LastRunStatus,
    DateTime? LastRunStartedAtUtc,
    DateTime? LastRunFinishedAtUtc,
    string? LastRunError,
    int? LastRunMessagesScanned,
    int? LastRunAttachmentsProcessed,
    int? LastRunReportsInserted,
    int? LastRunReportsSkippedAsDuplicate,
    int? LastRunParseFailures,
    int? LastRunTlsReportsInserted,
    int? LastRunTlsReportsSkippedAsDuplicate);
