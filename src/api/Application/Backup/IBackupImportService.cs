using DmarcAnalyzer.Api.Application.Common;

namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// Reads a configuration artifact back into an install.
/// <para>
/// Two methods over one computation, mirroring
/// <c>retention/preview</c> → <c>retention/purge</c>. They are separate methods rather than a
/// <c>dryRun</c> flag so a caller cannot apply an import by getting one argument wrong, and
/// the shared implementation is what makes the preview a promise rather than an estimate.
/// </para>
/// </summary>
public interface IBackupImportService
{
    /// <summary>
    /// Runs the whole import and writes nothing. Same gates, same matching, same counts as
    /// <see cref="ImportAsync"/> — including the refusals, so an operator finds out about a
    /// key mismatch or a non-empty install here rather than half way through a recovery.
    /// </summary>
    Task<ServiceResult<BackupImportResult>> PreviewAsync(
        BackupArtifact artifact,
        string mode,
        bool allowKeyFingerprintMismatch,
        CancellationToken ct);

    /// <summary>
    /// Applies the artifact. Additive: inserts and updates only, never a delete — anything
    /// in this install that the artifact does not mention is left exactly as it is.
    /// </summary>
    /// <param name="mode">
    /// <c>restore</c> or <c>merge</c>. Anything else is a 400; there is no default, because
    /// the two modes have different safety properties.
    /// </param>
    /// <param name="allowKeyFingerprintMismatch">
    /// Proceed even though the artifact's mailbox credentials were encrypted with a
    /// different key than this install runs. For the operator who wants the configuration
    /// back and accepts re-entering every mailbox password. Refused by default: importing
    /// sources that can never decrypt looks like a successful restore until the next sync.
    /// </param>
    Task<ServiceResult<BackupImportResult>> ImportAsync(
        BackupArtifact artifact,
        string mode,
        bool allowKeyFingerprintMismatch,
        CancellationToken ct);
}
