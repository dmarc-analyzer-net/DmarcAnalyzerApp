using System.Reflection;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// Produces the configuration artifact: everything a fresh install needs to become this
/// one, minus the report data.
/// <para>
/// The exclusion criterion is volume, not category. <c>dmarc_report</c> and its three
/// child tables are millions of rows that arrived over IMAP and can arrive again;
/// everything else is hundreds of rows a human typed, and re-typing it — mailbox
/// credentials most of all — is the part of a recovery that actually hurts.
/// </para>
/// </summary>
public sealed class BackupExportService(
    DmarcAnalyzerDbContext db,
    IConfiguration configuration,
    ILogger<BackupExportService> logger) : IBackupExportService
{
    /// <summary>
    /// Tables left out of the artifact, reported with their row counts so the file states
    /// its own scope rather than looking complete.
    /// </summary>
    private static readonly string[] ExcludedTables =
    [
        "dmarc_report",
        "dmarc_report_record",
        "dmarc_report_record_dkim_auth_result",
        "dmarc_report_record_spf_auth_result",
    ];

    public async Task<ServiceResult<BackupArtifact>> ExportAsync(
        bool allowPlaintextCredentials,
        CancellationToken ct)
    {
        // Read the same configuration path AddCredentialProtection reads, so this agrees
        // with what actually happened to the stored passwords rather than guessing from
        // the shape of a value.
        var key = configuration[CredentialProtectionExtensions.KeyConfigPath];
        var credentialsProtected = !string.IsNullOrWhiteSpace(key);

        if (!credentialsProtected && !allowPlaintextCredentials)
        {
            // Refused rather than warned. Without a key the app stores mailbox passwords
            // in plaintext with only a log line, so this artifact would be a plaintext
            // credential file — and the whole point of an export is that it leaves the
            // database and gets copied somewhere.
            return ServiceResult<BackupArtifact>.Failure(
                $"{CredentialProtectionExtensions.KeyConfigPath} is not configured, so mailbox " +
                "passwords are stored in plaintext and would be exported in plaintext. Configure a " +
                "key (openssl rand -base64 32), or pass allowPlaintextCredentials=true to accept it.",
                409);
        }

        var clients = await db.Clients
            .AsNoTracking()
            .OrderBy(x => x.Slug)
            .Select(x => new BackupClient(
                x.Id, x.Name, x.Slug, x.IsActive, x.RetentionMonths, x.LegalHold,
                x.AlertsEnabled, x.AlertComplianceDropPercent, x.AlertMinMessages,
                x.Timezone, x.CreatedAtUtc, x.UpdatedAtUtc))
            .ToListAsync(ct);

        // The DNS cache columns are deliberately absent — the worker refreshes them
        // within hours of a restore, and a stale copy would show a policy the domain may
        // no longer publish.
        var domains = await db.Domains
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new BackupDomain(
                x.Id, x.ClientId, x.Name, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc))
            .ToListAsync(ct);

        // PasswordEncrypted goes out verbatim: it is the enc:v1: ciphertext, useless
        // without the key the manifest fingerprints. LastProcessedUid is left behind so a
        // restored source rescans — see BackupMailboxSource.
        var mailboxSources = await db.MailboxSources
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new BackupMailboxSource(
                x.Id, x.Name, x.Protocol, x.Host, x.Port, x.UseTls, x.Username,
                x.PasswordEncrypted, x.DefaultClientId, x.IsActive,
                x.CreatedAtUtc, x.UpdatedAtUtc))
            .ToListAsync(ct);

        var recipients = await db.NotificationRecipients
            .AsNoTracking()
            .OrderBy(x => x.Email)
            .Select(x => new BackupNotificationRecipient(
                x.Id, x.ClientId, x.Email, x.Kind, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc))
            .ToListAsync(ct);

        var users = await db.AgencyUsers
            .AsNoTracking()
            .OrderBy(x => x.Email)
            .Select(x => new BackupUser(
                x.Id, x.Email, x.PasswordHash, x.DisplayName, x.Role, x.IsActive,
                x.CreatedAtUtc, x.UpdatedAtUtc))
            .ToListAsync(ct);

        var identities = await db.UserIdentities
            .AsNoTracking()
            .OrderBy(x => x.Issuer).ThenBy(x => x.Subject)
            .Select(x => new BackupUserIdentity(
                x.Id, x.UserId, x.Issuer, x.Subject, x.EmailAtLink, x.CreatedAtUtc))
            .ToListAsync(ct);

        var grants = await db.UserClientGrants
            .AsNoTracking()
            .OrderBy(x => x.UserId).ThenBy(x => x.ClientId)
            .Select(x => new BackupUserClientGrant(
                x.Id, x.UserId, x.ClientId, x.CreatedAtUtc, x.CreatedByUserId))
            .ToListAsync(ct);

        var (migrationId, migrationCount) = await MigrationStateAsync(ct);

        var manifest = new BackupManifest(
            FormatVersion: BackupJson.FormatVersion,
            ExportedAtUtc: DateTime.UtcNow,
            AppVersion: AppVersion(),
            MigrationId: migrationId,
            MigrationCount: migrationCount,
            EncryptionKeyFingerprint: CredentialKeyFingerprint.Compute(key),
            CredentialsProtected: credentialsProtected,
            Scope: new BackupScope(Config: true, History: "none", Reports: "none"),
            Excluded: await ExcludedCountsAsync(ct),
            LegalHoldClients: [.. clients.Where(x => x.LegalHold).Select(x => x.Slug)]);

        if (!credentialsProtected)
        {
            logger.LogWarning(
                "Exported configuration with unprotected credentials: {SourceCount} mailbox " +
                "password(s) are in this artifact in plaintext",
                mailboxSources.Count);
        }

        if (manifest.LegalHoldClients.Count > 0)
        {
            // Said out loud because for these clients "we can re-ingest it from the
            // mailbox" is not a defensible answer, and this artifact carries no reports.
            logger.LogInformation(
                "Configuration export covers {Count} client(s) under legal hold ({Slugs}); their " +
                "report data is not in this artifact",
                manifest.LegalHoldClients.Count, string.Join(", ", manifest.LegalHoldClients));
        }

        return ServiceResult<BackupArtifact>.Success(new BackupArtifact(
            manifest, clients, domains, mailboxSources, recipients, users, identities, grants));
    }

    /// <summary>
    /// Advisory only. Nothing in the build stamps a version, so this reads 1.0.0 until
    /// the release pipeline sets one — the manifest's migration id is the field to trust
    /// for compatibility.
    /// </summary>
    private static string AppVersion()
        => typeof(BackupExportService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? typeof(BackupExportService).Assembly.GetName().Version?.ToString()
           ?? "unknown";

    /// <summary>
    /// The artifact's real schema identity: the newest applied migration, and how many
    /// have been applied.
    /// <para>
    /// Guarded on <c>IsRelational</c> because the migration history is a relational
    /// concept — asking the in-memory provider the whole test suite runs on throws. A
    /// non-relational context reports "unknown" rather than failing the export.
    /// </para>
    /// </summary>
    private async Task<(string? Id, int Count)> MigrationStateAsync(CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            return (null, 0);
        }

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();

        // Migration ids are timestamp-prefixed, so ordinal order is chronological order.
        return (applied.OrderBy(x => x, StringComparer.Ordinal).LastOrDefault(), applied.Length);
    }

    /// <summary>
    /// Counted rather than estimated, and counted every time. Four sequential scans is
    /// real work on a table with millions of rows, but a scope claim that might be stale
    /// is worse than a slow export — and this runs on an operator action or a half-hourly
    /// pass, not per request.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, long>> ExcludedCountsAsync(CancellationToken ct)
        => new Dictionary<string, long>
        {
            [ExcludedTables[0]] = await db.DmarcReports.LongCountAsync(ct),
            [ExcludedTables[1]] = await db.DmarcReportRecords.LongCountAsync(ct),
            [ExcludedTables[2]] = await db.DmarcReportRecordDkimAuthResults.LongCountAsync(ct),
            [ExcludedTables[3]] = await db.DmarcReportRecordSpfAuthResults.LongCountAsync(ct),
        };
}
