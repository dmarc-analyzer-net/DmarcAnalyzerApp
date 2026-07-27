using DmarcAnalyzer.Api.Application.Common;

namespace DmarcAnalyzer.Api.Application.Backup;

public interface IBackupExportService
{
    /// <summary>
    /// Builds the configuration artifact for this install.
    /// </summary>
    /// <param name="allowPlaintextCredentials">
    /// Permits the export to proceed when no credential encryption key is configured —
    /// in which case mailbox passwords are stored, and would be exported, in plaintext.
    /// Refused by default: writing an unprotected artifact to disk or to a bucket is
    /// meaningfully worse than leaving those rows in Postgres.
    /// </param>
    Task<ServiceResult<BackupArtifact>> ExportAsync(
        bool allowPlaintextCredentials,
        CancellationToken ct);
}
