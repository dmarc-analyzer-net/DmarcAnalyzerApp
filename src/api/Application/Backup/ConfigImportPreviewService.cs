using System.Text.Json;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Backup;

public interface IConfigImportPreviewService
{
    Task<ConfigImportPreviewDto> PreviewAsync(CancellationToken ct);

    /// <summary>
    /// The artifact at <c>config/latest.json</c>, or null when there is no bucket, no object
    /// or nothing readable there.
    /// </summary>
    Task<BackupArtifact?> ReadLatestFromBucketAsync(CancellationToken ct);
}

/// <summary>
/// Everything the console needs to decide whether an import is safe, before it sends one.
/// <para>
/// This is the authenticated answer to "is this a clean install?", which
/// <c>GET /api/v1/auth/setup</c> cannot give: by the time the console loads, the bootstrap
/// administrator exists and <c>requiresBootstrap</c> reports false — the opposite of the
/// truth for a freshly created install that has no clients in it yet.
/// </para>
/// </summary>
public sealed class ConfigImportPreviewService(
    DmarcAnalyzerDbContext db,
    IObjectStorage storage,
    IConfiguration configuration,
    ICurrentUserContext currentUser,
    IOptions<BackupOptions> options,
    ILogger<ConfigImportPreviewService> logger) : IConfigImportPreviewService
{
    private readonly BackupOptions _options = options.Value;

    public async Task<ConfigImportPreviewDto> PreviewAsync(CancellationToken ct)
    {
        // Clients AND domains, not either: a restore into an install that already owns a
        // domain would produce a union of two configurations rather than a copy of one.
        var isEmpty = !await db.Clients.AnyAsync(ct) && !await db.Domains.AnyAsync(ct);
        var key = configuration[CredentialProtectionExtensions.KeyConfigPath];

        ConfigImportBucketArtifactDto? bucket = null;

        if (storage.IsConfigured)
        {
            var artifact = await ReadLatestFromBucketAsync(ct);

            if (artifact is not null)
            {
                bucket = new ConfigImportBucketArtifactDto(
                    Key: LatestKey(),
                    FormatVersion: artifact.Manifest.FormatVersion,
                    ExportedAtUtc: artifact.Manifest.ExportedAtUtc,
                    KeyFingerprintMatches: CredentialKeyFingerprint.Matches(
                        artifact.Manifest.EncryptionKeyFingerprint, key),
                    // Told before the import, not after: on an email collision the imported
                    // user wins, so this is the operator's own password being replaced.
                    CarriesSignedInUser: currentUser.Email is { } email
                        && artifact.Users.Any(u =>
                            string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)),
                    Entities:
                    [
                        new("clients", artifact.Clients.Count),
                        new("domains", artifact.Domains.Count),
                        new("mailboxSources", artifact.MailboxSources.Count),
                        new("notificationRecipients", artifact.NotificationRecipients.Count),
                        new("users", artifact.Users.Count),
                        new("userIdentities", artifact.UserIdentities.Count),
                        new("grants", artifact.Grants.Count),
                    ]);
            }
        }

        return new ConfigImportPreviewDto(
            IsEmptyInstall: isEmpty,
            SupportedFormatVersion: BackupJson.FormatVersion,
            KeyFingerprint: CredentialKeyFingerprint.Compute(key),
            BucketConfigured: storage.IsConfigured,
            Bucket: bucket);
    }

    public async Task<BackupArtifact?> ReadLatestFromBucketAsync(CancellationToken ct)
    {
        if (!storage.IsConfigured)
        {
            return null;
        }

        try
        {
            var bytes = await storage.GetAsync(LatestKey(), ct);
            if (bytes is null || bytes.Length == 0)
            {
                return null;
            }

            return JsonSerializer.Deserialize<BackupArtifact>(bytes, BackupJson.ReadOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fatal. A preview whose bucket read failed still has to report the install
            // facts, or the console cannot offer the upload path either.
            logger.LogWarning(ex, "Could not read the latest configuration artifact from object storage");

            return null;
        }
    }

    private string LatestKey() => $"{_options.Prefix.Trim().Trim('/')}/config/latest.json";
}
