namespace DmarcAnalyzer.Api.Application.MailboxSources;

public sealed record MailboxSourceDto(
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
