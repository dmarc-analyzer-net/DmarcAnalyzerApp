namespace DmarcAnalyzer.Api.Application.Backup;

/// <param name="Ran">False when no bucket is configured, so the pass was inert.</param>
/// <param name="SnapshotKey">The key <c>latest.json</c> was promoted from, when one was written.</param>
/// <param name="HistoryObjects">Append-only objects written this pass, by stream.</param>
public sealed record BackupOffloadResult(
    bool Ran,
    string? SnapshotKey,
    IReadOnlyDictionary<string, int> HistoryObjects,
    string? Error);

/// <summary>What the console shows an operator deciding whether to trust the backup.</summary>
/// <param name="BucketVersioning">
/// <c>enabled</c>, <c>disabled</c> or <c>unknown</c>. Anything but enabled matters because
/// <c>config/latest.json</c> is overwritten on every pass, so without versioning a single
/// bad write is the end of the only copy.
/// </param>
/// <param name="CredentialsProtected">
/// False means no encryption key is configured, mailbox passwords are stored in plaintext,
/// and offload therefore refuses to run at all.
/// </param>
public sealed record BackupStatusDto(
    bool OffloadConfigured,
    string? Destination,
    bool CredentialsProtected,
    string BucketVersioning,
    int IntervalMinutes,
    bool HistoryEnabled,
    bool ReportArchiveEnabled,
    DateTime? LastSuccessfulOffloadAtUtc,
    DateTime? LastAttemptAtUtc,
    string? LastError,
    IReadOnlyList<BackupStreamStatusDto> Streams);

public sealed record BackupStreamStatusDto(
    string Stream,
    DateTime? WatermarkUtc,
    DateTime? LastSuccessAtUtc,
    string? LastError);
