using System.Text;
using System.Text.Json;
using Carter;
using DmarcAnalyzer.Api.Application.Audit;
using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Ingestion;
using DmarcAnalyzer.Api.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Modules;

/// <summary>
/// Configuration export.
/// <para>
/// The artifact holds <c>enc:v1:</c> mailbox ciphertext and PBKDF2 password hashes, so
/// it is a credential file and not a config file — admin-only, audited, and refused
/// outright when no encryption key is configured, because in that state the passwords
/// inside it are plaintext.
/// </para>
/// </summary>
public sealed class BackupModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        // Returned as a download rather than a plain body: this is a file an operator
        // stores, and a filename carrying the date is what makes a directory of them
        // legible six months later.
        app.MapGet("/api/v1/admin/config/export", async (
            bool? allowPlaintextCredentials,
            IBackupExportService service,
            IAuditLog audit,
            CancellationToken ct) =>
        {
            var result = await service.ExportAsync(allowPlaintextCredentials ?? false, ct);
            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var artifact = result.Value!;
            var json = BackupJson.Serialize(artifact);

            await audit.RecordAsync(
                AuditEvents.ConfigExported,
                $"Exported configuration: {artifact.Clients.Count} client(s), " +
                $"{artifact.Domains.Count} domain(s), {artifact.MailboxSources.Count} mailbox source(s)",
                details: artifact.Manifest.CredentialsProtected
                    ? null
                    : "credentials unprotected: mailbox passwords exported in plaintext",
                ct: ct);

            var name = $"dmarc-config-{artifact.Manifest.ExportedAtUtc:yyyy-MM-dd}.json";

            return Results.File(Encoding.UTF8.GetBytes(json), "application/json", name);
        }).RequireAgencyAdmin();

        // Read-only. The console uses this to answer "can I trust the backup?", which is
        // mostly about the three things that fail quietly: no bucket, no encryption key,
        // and no bucket versioning under an overwritten latest.json.
        app.MapGet("/api/v1/admin/backup/status", async (
            IBackupOffloadService service,
            CancellationToken ct) =>
        {
            return Results.Ok(await service.GetStatusAsync(ct));
        }).RequireAgencyAdmin();

        // The offload runs on the worker's schedule; this exists so an operator can prove
        // the destination works without waiting out an interval — the same reason the
        // retention purge has a manual trigger.
        app.MapPost("/api/v1/admin/backup/offload", async (
            IBackupOffloadService service,
            CancellationToken ct) =>
        {
            var result = await service.RunAsync(ct);

            if (!result.Ran && result.Error is not null)
            {
                return Results.Json(new { error = result.Error }, statusCode: 409);
            }

            return Results.Ok(result);
        }).RequireAgencyAdmin();

        // Deserialized by hand rather than through model binding, because the artifact is a
        // published format and the app's response JSON policy is not pinned — see BackupJson.
        // ReadOptions is case-insensitive too, so a hand-edited artifact still imports.
        static async Task<BackupArtifact?> ReadArtifactAsync(HttpRequest request, CancellationToken ct)
        {
            try
            {
                return await JsonSerializer.DeserializeAsync<BackupArtifact>(
                    request.Body, BackupJson.ReadOptions, ct);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // A GET, so it carries no artifact: it reports facts about *this install* plus
        // whatever object storage can offer. An uploaded file is checked in the browser
        // against these, which is what stops an invalid one from ever being sent.
        app.MapGet("/api/v1/admin/config/import/preview", async (
            IConfigImportPreviewService service,
            CancellationToken ct) =>
        {
            return Results.Ok(await service.PreviewAsync(ct));
        }).RequireAgencyAdmin();

        // The service's real dry run: same gates, same matching, same counts as the apply,
        // over an artifact the caller supplies. Named separately from the GET above because
        // one previews *this install* and the other previews *this artifact*.
        app.MapPost("/api/v1/admin/config/import/dry-run", async (
            HttpRequest request,
            string? mode,
            bool? allowKeyFingerprintMismatch,
            IBackupImportService service,
            ICurrentUserContext currentUser,
            CancellationToken ct) =>
        {
            var artifact = await ReadArtifactAsync(request, ct);
            if (artifact is null)
            {
                return Results.Json(
                    new { error = "that file is not a configuration artifact" }, statusCode: 400);
            }

            // mode is deliberately not defaulted: an unrecognised or absent value has to
            // become the service's 400, because the two modes differ in what they permit.
            var result = await service.PreviewAsync(
                artifact, mode ?? string.Empty, allowKeyFingerprintMismatch ?? false, ct);

            return result.IsSuccess
                ? Results.Ok(Adapt(result.Value!, currentUser.Email))
                : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
        }).RequireAgencyAdmin();

        app.MapPost("/api/v1/admin/config/import", async (
            HttpRequest request,
            string? mode,
            string? source,
            bool? allowKeyFingerprintMismatch,
            IBackupImportService service,
            IConfigImportPreviewService previewService,
            ICurrentUserContext currentUser,
            DmarcAnalyzerDbContext db,
            IAuditLog audit,
            CancellationToken ct) =>
        {
            // source=bucket is the one that matters in a real recovery: the operator should
            // not have to find and download the artifact before the console can offer it.
            var artifact = string.Equals(source, "bucket", StringComparison.OrdinalIgnoreCase)
                ? await previewService.ReadLatestFromBucketAsync(ct)
                : await ReadArtifactAsync(request, ct);

            if (artifact is null)
            {
                return Results.Json(
                    new
                    {
                        error = string.Equals(source, "bucket", StringComparison.OrdinalIgnoreCase)
                            ? "no configuration artifact could be read from object storage"
                            : "that file is not a configuration artifact",
                    },
                    statusCode: 400);
            }

            var result = await service.ImportAsync(
                artifact, mode ?? string.Empty, allowKeyFingerprintMismatch ?? false, ct);

            if (!result.IsSuccess)
            {
                return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            }

            var imported = result.Value!;
            var response = Adapt(imported, currentUser.Email);

            // Audited before the sessions go, so the row exists even if this request is the
            // last one this operator's session is able to make.
            await audit.RecordAsync(
                AuditEvents.ConfigImported,
                $"Imported configuration ({imported.Mode}): " +
                $"{response.Created} created, {response.Updated} updated",
                details: imported.Warnings.Count == 0 ? null : string.Join("; ", imported.Warnings),
                ct: ct);

            // Only the accounts whose stored hash the artifact actually replaced. Their old
            // password no longer exists anywhere, so leaving those sessions open would keep
            // someone signed in on a credential the install has no record of — including,
            // quite possibly, the operator running this. Everyone else stays signed in.
            if (imported.Users.SessionsToInvalidateUserIds.Count > 0)
            {
                var affected = imported.Users.SessionsToInvalidateUserIds.ToList();
                var open = await db.UserSessions
                    .Where(x => affected.Contains(x.UserId) && x.RevokedAtUtc == null)
                    .ToListAsync(ct);

                foreach (var session in open)
                {
                    session.RevokedAtUtc = DateTime.UtcNow;
                }

                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(response);
        }).RequireAgencyAdmin();

        // Non-destructive, and the only safe way to answer "what would this delete?" about
        // mail the app does not own. Reports every source, including the suspended ones —
        // a preview that hid them could not answer "why is that mailbox still growing?".
        app.MapGet("/api/v1/admin/mailbox-retention/preview", async (
            IMailboxRetentionService service,
            CancellationToken ct) =>
        {
            return Results.Ok(await service.RunAsync(dryRun: true, ct));
        }).RequireAgencyAdmin();

        // Irreversible: it expunges mail from a customer's mailbox. Opt-in per source,
        // suspended entirely for any source serving a client under legal hold, and bounded
        // by a grace margin on top of the retention window.
        app.MapPost("/api/v1/admin/mailbox-retention/purge", async (
            IMailboxRetentionService service,
            CancellationToken ct) =>
        {
            return Results.Ok(await service.RunAsync(dryRun: false, ct));
        }).RequireAgencyAdmin();
    }

    /// <summary>
    /// Flattens the service's per-table detail into the two totals and the two facts the
    /// console acts on — whose passwords changed, and whether this session is one of them.
    /// </summary>
    private static ConfigImportResponseDto Adapt(BackupImportResult result, string? actingEmail)
        => new(
            DryRun: result.DryRun,
            Mode: result.Mode,
            Created: result.Entities.Sum(x => x.Created),
            Updated: result.Entities.Sum(x => x.Updated),
            MailboxCredentialsWillNotDecrypt: result.MailboxCredentialsWillNotDecrypt,
            SignedInSessionInvalidated: actingEmail is not null
                && result.Users.PasswordChangedEmails.Contains(actingEmail, StringComparer.OrdinalIgnoreCase),
            Entities: [.. result.Entities.Select(x =>
                new ConfigImportEntityResultDto(x.Entity, x.Created, x.Updated, x.Skipped))],
            UsersWithChangedPasswords: result.Users.PasswordChangedEmails,
            // Rendered as lines rather than as structured rows: the console shows them
            // verbatim, and a conflict is a sentence about one row, not a table.
            Conflicts: [.. result.Entities
                .SelectMany(e => e.Conflicts)
                .Select(c => $"{c.Entity} {c.NaturalKey}: {c.Resolution} — {c.Reason}")],
            Warnings: result.Warnings);
}
