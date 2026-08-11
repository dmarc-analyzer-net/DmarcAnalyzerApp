namespace DmarcAnalyzer.Api.Application.ReportSources;

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
    DateTime? OldestMessageAtUtc,
    DateTime? LastSuccessSyncAtUtc,
    long? LastProcessedUid,
    long? LastProcessedUidValidity,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
