namespace DmarcAnalyzer.Api.Application.ReportSources;

/// <summary>
/// A report source as the console reads it. Never carries the password/secret;
/// the protocol decides which of the connection and checkpoint fields mean
/// anything (mail: host/port/UID(L); s3: the S3 fields; api: none of them).
/// </summary>
public sealed record ReportSourceDto(
    Guid Id,
    string Name,
    string Protocol,
    string Host,
    int Port,
    bool UseTls,
    string Username,
    Guid DefaultClientId,
    string? DefaultClientName,
    bool IsActive,
    bool DeleteAfterRetention,
    bool AllowForeignDomains,
    DateTime? OldestMessageAtUtc,
    DateTime? LastSuccessSyncAtUtc,
    long? LastProcessedUid,
    long? LastProcessedUidValidity,
    string? LastProcessedUidl,
    string? S3Bucket,
    string? S3Prefix,
    string? S3Region,
    string? S3Endpoint,
    bool S3ForcePathStyle,
    DateTime? LastProcessedObjectAtUtc,
    string? LastProcessedObjectKey,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
