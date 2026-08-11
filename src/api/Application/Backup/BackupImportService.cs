using DmarcAnalyzer.Api.Application.Clients;
using DmarcAnalyzer.Api.Application.Common;
using DmarcAnalyzer.Api.Application.Security;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// Reads a configuration artifact back into an install.
/// <para>
/// <b>The invariant: this service never deletes a row.</b> There is no <c>Remove</c> in this
/// file and there must never be one. A client, domain, user, grant or recipient that exists
/// here and is absent from the artifact is left exactly as it is — most immediately because
/// the operator running the import is signed in as a freshly bootstrapped admin that no
/// artifact contains, and an import that tidied up rows it did not recognise would delete
/// the account holding the session performing it.
/// </para>
/// <para>
/// That invariant is also why <c>restore</c> demands an empty install. An additive import
/// cannot reproduce a state in which something had been <em>deleted</em> before the disaster,
/// so restoring into a populated install would quietly produce a union and call it a copy.
/// <c>merge</c> is a union on purpose; <c>restore</c> refuses to pretend.
/// </para>
/// <para>
/// Guid primary keys are client-generated (<c>= Guid.NewGuid()</c> on every entity), so Ids
/// travel verbatim and every foreign key in the artifact stays valid with no rewiring. That
/// holds right up until a natural key matches a row that is already here: a primary key
/// cannot be rewritten, so the existing Id wins and everything in the artifact that pointed
/// at the artifact's Id has to be repointed. That is what
/// <see cref="ImportState.ClientIds"/> and <see cref="ImportState.UserIds"/> are for, and
/// skipping them is how an import produces orphaned grants that look like a successful
/// restore.
/// </para>
/// </summary>
public sealed class BackupImportService(
    DmarcAnalyzerDbContext db,
    IConfiguration configuration,
    ILogger<BackupImportService> logger) : IBackupImportService
{
    public Task<ServiceResult<BackupImportResult>> PreviewAsync(
        BackupArtifact artifact,
        string mode,
        bool allowKeyFingerprintMismatch,
        CancellationToken ct)
        => RunAsync(artifact, mode, allowKeyFingerprintMismatch, dryRun: true, ct);

    public Task<ServiceResult<BackupImportResult>> ImportAsync(
        BackupArtifact artifact,
        string mode,
        bool allowKeyFingerprintMismatch,
        CancellationToken ct)
        => RunAsync(artifact, mode, allowKeyFingerprintMismatch, dryRun: false, ct);

    /// <summary>
    /// One implementation for both, because a preview whose arithmetic differs from the
    /// apply is worse than no preview: it is a number an operator makes a recovery decision
    /// on. <paramref name="dryRun"/> changes exactly one thing — whether the pass ends in a
    /// <c>SaveChanges</c>.
    /// </summary>
    private async Task<ServiceResult<BackupImportResult>> RunAsync(
        BackupArtifact artifact,
        string mode,
        bool allowKeyFingerprintMismatch,
        bool dryRun,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;

        if (!BackupImportModes.TryParse(mode, out var importMode))
        {
            // Never a default. "restore" and "merge" have different safety properties, and a
            // typo that silently picked one would be discovered by its consequences.
            return ServiceResult<BackupImportResult>.Failure(
                $"unknown import mode '{mode}'. Use '{BackupImportModes.Restore}' (disaster recovery " +
                $"into an empty install) or '{BackupImportModes.Merge}' (upsert into an existing one).",
                400);
        }

        var version = artifact.Manifest.FormatVersion;

        if (version > BackupJson.FormatVersion)
        {
            // Refused rather than read optimistically. A newer writer may have changed what a
            // field means, and the failure mode of guessing is a restore that looks complete
            // and is subtly wrong — the worst possible outcome for a backup.
            return ServiceResult<BackupImportResult>.Failure(
                $"artifact formatVersion {version} is newer than this build understands " +
                $"({BackupJson.FormatVersion}). Upgrade the target install rather than importing a " +
                "document whose fields this version would have to guess at.",
                400);
        }

        if (version < 1)
        {
            // Zero is what a missing manifest deserializes to, so this catches "that JSON was
            // not one of ours" before it half-imports.
            return ServiceResult<BackupImportResult>.Failure(
                $"artifact formatVersion {version} is not a version this app ever wrote; the " +
                "manifest is missing or malformed.",
                400);
        }

        var warnings = new List<string>();
        var credentialsWillNotDecrypt = false;

        // Read the same configuration path AddCredentialProtection reads, so this agrees with
        // the protector the app is actually running rather than inferring one.
        var runningKey = configuration[CredentialProtectionExtensions.KeyConfigPath];

        if (artifact.ReportSources.Count > 0
            && !CredentialKeyFingerprint.Matches(artifact.Manifest.EncryptionKeyFingerprint, runningKey))
        {
            if (!allowKeyFingerprintMismatch)
            {
                // The failure being prevented: enc:v1: carries no key identity, so importing
                // sources encrypted under another key succeeds, reads as a clean restore, and
                // surfaces days later as AuthenticationTagMismatchException on a mailbox sync.
                return ServiceResult<BackupImportResult>.Failure(
                    "this artifact's mailbox credentials were encrypted with a different key than " +
                    $"{CredentialProtectionExtensions.KeyConfigPath} holds (artifact " +
                    $"{artifact.Manifest.EncryptionKeyFingerprint ?? "none"}, running " +
                    $"{CredentialKeyFingerprint.Compute(runningKey) ?? "none"}), so they could never " +
                    "be decrypted. Configure the matching key, or pass " +
                    "allowKeyFingerprintMismatch=true to import the configuration and re-enter every " +
                    "mailbox password by hand.",
                    409);
            }

            credentialsWillNotDecrypt = true;
            warnings.Add(
                $"{artifact.ReportSources.Count} report source(s) were imported with credentials " +
                "this install holds no key for; each one needs its password re-entered before it will " +
                "sync.");
        }

        if (!artifact.Manifest.CredentialsProtected)
        {
            warnings.Add(
                "this artifact was exported without credential encryption, so the mailbox passwords " +
                "in it are plaintext; treat the file as a leaked credential and rotate them.");
        }

        if (importMode == BackupImportMode.Restore)
        {
            // What counts as empty — and why the bootstrapped default client does not — lives
            // in DefaultClient.IsPristineInstallAsync, shared with the preview so the console
            // can never offer a restore this would then refuse.
            if (!await DefaultClient.IsPristineInstallAsync(db, ct))
            {
                return ServiceResult<BackupImportResult>.Failure(
                    "restore is only allowed into an install nothing has been added to yet (no " +
                    "clients of your own, no domains, no report sources). Because import never " +
                    "deletes, restoring over existing rows would produce a union of two installs " +
                    "rather than a copy of one. Use merge if a union is what you want.",
                    409);
            }
        }

        var clientTally = new Tally(BackupImportEntities.Client);
        var domainTally = new Tally(BackupImportEntities.Domain);
        var sourceTally = new Tally(BackupImportEntities.ReportSource);
        var userTally = new Tally(BackupImportEntities.AgencyUser);
        var identityTally = new Tally(BackupImportEntities.UserIdentity);
        var grantTally = new Tally(BackupImportEntities.UserClientGrant);
        var recipientTally = new Tally(BackupImportEntities.NotificationRecipient);
        var mtaStsPolicyTally = new Tally(BackupImportEntities.MtaStsPolicy);

        var state = await LoadStateAsync(ct);
        BackupImportUserReport userReport;
        var saved = false;

        try
        {
            // Pass order is foreign-key order: clients before the domains, sources and
            // recipients that point at them; users before the identities and grants that point
            // at them. It has to be, because each child pass resolves its parent's *effective*
            // Id out of the map the parent pass just filled in.
            ImportClients(artifact, state, clientTally);
            ImportDomains(artifact, state, domainTally);
            ImportMtaStsPolicies(artifact, state, mtaStsPolicyTally);
            ImportReportSources(artifact, state, sourceTally);
            userReport = ImportUsers(artifact, state, userTally);
            ImportUserIdentities(artifact, state, identityTally);
            ImportGrants(artifact, state, grantTally);
            ImportNotificationRecipients(artifact, state, recipientTally);

            if (!dryRun)
            {
                // One SaveChanges for the whole artifact: EF orders the inserts by the
                // dependency graph, and a single call is a single transaction on a relational
                // provider, so a failure half way through leaves nothing behind. It is also
                // what makes the preview exact — the preview *is* this pass, minus this line.
                await db.SaveChangesAsync(ct);
                saved = true;
            }
        }
        finally
        {
            if (!saved)
            {
                // Mandatory, not tidiness. This DbContext is scoped to the request, and the
                // caller writes an audit event through the same instance the moment we return —
                // that SaveChanges would flush a preview's pending inserts and commit an import
                // nobody asked for. Clearing also stops a failed import leaving a half-built
                // graph for someone else's save to trip over.
                db.ChangeTracker.Clear();
            }
        }

        var entities = new[]
        {
            clientTally.Freeze(),
            domainTally.Freeze(),
            sourceTally.Freeze(),
            userTally.Freeze(),
            identityTally.Freeze(),
            grantTally.Freeze(),
            recipientTally.Freeze(),
            mtaStsPolicyTally.Freeze(),
        };

        logger.LogInformation(
            "Configuration import ({Mode}{DryRun}): {Created} row(s) {CreatedVerb}, {Updated} updated, " +
            "{Skipped} skipped, {Conflicts} conflict(s); {Sessions} account(s) had their password changed",
            BackupImportModes.ToWireValue(importMode),
            dryRun ? ", preview" : string.Empty,
            entities.Sum(x => x.Created),
            dryRun ? "would be created" : "created",
            entities.Sum(x => x.Updated),
            entities.Sum(x => x.Skipped),
            entities.Sum(x => x.Conflicts.Count),
            userReport.SessionsToInvalidateUserIds.Count);

        return ServiceResult<BackupImportResult>.Success(new BackupImportResult(
            DryRun: dryRun,
            Mode: BackupImportModes.ToWireValue(importMode),
            StartedAtUtc: startedAt,
            FormatVersion: version,
            MailboxCredentialsWillNotDecrypt: credentialsWillNotDecrypt,
            Entities: entities,
            Users: userReport,
            Warnings: warnings));
    }

    private void ImportClients(BackupArtifact artifact, ImportState state, Tally tally)
    {
        foreach (var client in artifact.Clients)
        {
            var slug = NormalizeText(client.Slug);

            if (state.ClientsBySlug.TryGetValue(slug, out var existing))
            {
                state.ClientIds[client.Id] = existing.Id;
                NoteKeptExistingId(tally, BackupImportEntities.Client, slug, client.Id, existing.Id);

                // Slug is not reassigned: it is what matched, and rewriting its casing could
                // collide with Postgres' case-sensitive unique index on a sibling row.
                existing.Name = client.Name;
                existing.IsActive = client.IsActive;
                existing.RetentionMonths = client.RetentionMonths;
                existing.LegalHold = client.LegalHold;
                existing.AlertsEnabled = client.AlertsEnabled;
                existing.AlertComplianceDropPercent = client.AlertComplianceDropPercent;
                existing.AlertMinMessages = client.AlertMinMessages;
                existing.Timezone = client.Timezone;
                existing.CreatedAtUtc = client.CreatedAtUtc;
                existing.UpdatedAtUtc = client.UpdatedAtUtc;
                tally.Updated++;
                continue;
            }

            if (state.ClientIdsInUse.Contains(client.Id))
            {
                Skip(tally, BackupImportEntities.Client, slug, client.Id, client.Id,
                    "that id already belongs to a client with a different slug here; inserting would " +
                    "violate the primary key and updating would rename an unrelated client");
                continue;
            }

            var row = new Client
            {
                Id = client.Id,
                Name = client.Name,
                Slug = slug,
                IsActive = client.IsActive,
                RetentionMonths = client.RetentionMonths,
                LegalHold = client.LegalHold,
                AlertsEnabled = client.AlertsEnabled,
                AlertComplianceDropPercent = client.AlertComplianceDropPercent,
                AlertMinMessages = client.AlertMinMessages,
                Timezone = client.Timezone,
                CreatedAtUtc = client.CreatedAtUtc,
                UpdatedAtUtc = client.UpdatedAtUtc,
            };

            db.Clients.Add(row);
            state.ClientIdsInUse.Add(row.Id);
            state.ClientsBySlug[slug] = row;
            state.ClientIds[client.Id] = row.Id;
            tally.Created++;
        }
    }

    /// <summary>
    /// Nothing else in the artifact references a domain Id, so domains need no Id map — they
    /// are the leaf of the configuration graph as far as this artifact is concerned.
    /// </summary>
    private void ImportDomains(BackupArtifact artifact, ImportState state, Tally tally)
    {
        foreach (var domain in artifact.Domains)
        {
            var name = NormalizeText(domain.Name);

            if (!TryResolveClientId(state, domain.ClientId, out var clientId))
            {
                Skip(tally, BackupImportEntities.Domain, name, domain.Id, Guid.Empty,
                    $"its client ({domain.ClientId}) is neither in this artifact nor in this install, so " +
                    "the row has no owner to attach to");
                continue;
            }

            if (state.DomainsByName.TryGetValue(name, out var existing))
            {
                if (existing.ClientId != clientId)
                {
                    // Domain names are globally unique and every report derives its tenancy
                    // through the domain, so re-parenting one would silently move another
                    // client's report history into this artifact's client. Reported, never done.
                    Skip(tally, BackupImportEntities.Domain, name, domain.Id, existing.Id,
                        "a domain with this name already belongs to a different client here; " +
                        "re-parenting it would move that client's report history with it");
                    continue;
                }

                NoteKeptExistingId(tally, BackupImportEntities.Domain, name, domain.Id, existing.Id);

                // The DNS cache columns are untouched: they are not in the artifact, and the
                // worker's refresh pass owns them.
                existing.IsActive = domain.IsActive;
                existing.CreatedAtUtc = domain.CreatedAtUtc;
                existing.UpdatedAtUtc = domain.UpdatedAtUtc;
                tally.Updated++;
                continue;
            }

            if (state.DomainIdsInUse.Contains(domain.Id))
            {
                Skip(tally, BackupImportEntities.Domain, name, domain.Id, domain.Id,
                    "that id already belongs to a domain with a different name here");
                continue;
            }

            var row = new Domain
            {
                Id = domain.Id,
                ClientId = clientId,
                Name = name,
                IsActive = domain.IsActive,
                CreatedAtUtc = domain.CreatedAtUtc,
                UpdatedAtUtc = domain.UpdatedAtUtc,
            };

            db.Domains.Add(row);
            state.DomainIdsInUse.Add(row.Id);
            state.DomainsByName[name] = row;
            tally.Created++;
        }
    }

    /// <summary>
    /// Hosted policies resolve their domain through the artifact's own domain list — the
    /// artifact's DomainId names a row in the same file, and the domain pass just decided
    /// what that row's effective identity here is. A policy whose domain was skipped is
    /// skipped with it, for the skipped domain's reason.
    /// </summary>
    private void ImportMtaStsPolicies(BackupArtifact artifact, ImportState state, Tally tally)
    {
        foreach (var policy in artifact.MtaStsPolicies ?? [])
        {
            var artifactDomain = artifact.Domains.FirstOrDefault(d => d.Id == policy.DomainId);
            var label = artifactDomain is null ? policy.DomainId.ToString() : NormalizeText(artifactDomain.Name);

            if (artifactDomain is null
                || !state.DomainsByName.TryGetValue(NormalizeText(artifactDomain.Name), out var domainRow))
            {
                Skip(tally, BackupImportEntities.MtaStsPolicy, label, policy.Id, Guid.Empty,
                    artifactDomain is null
                        ? "its domain is not in this artifact, so there is nothing to attach it to"
                        : "its domain was not imported, so the policy has no row to attach to");
                continue;
            }

            if (state.MtaStsPoliciesByDomainId.TryGetValue(domainRow.Id, out var existing))
            {
                NoteKeptExistingId(tally, BackupImportEntities.MtaStsPolicy, label, policy.Id, existing.Id);

                existing.Enabled = policy.Enabled;
                existing.Mode = policy.Mode;
                existing.MaxAgeSeconds = policy.MaxAgeSeconds;
                existing.MxPatterns = policy.MxPatterns;
                // Verbatim, so a restore never forces a TXT record update.
                existing.PolicyId = policy.PolicyId;
                existing.ModeChangedAtUtc = policy.ModeChangedAtUtc;
                existing.CreatedAtUtc = policy.CreatedAtUtc;
                existing.UpdatedAtUtc = policy.UpdatedAtUtc;
                tally.Updated++;
                continue;
            }

            if (state.MtaStsPolicyIdsInUse.Contains(policy.Id))
            {
                Skip(tally, BackupImportEntities.MtaStsPolicy, label, policy.Id, policy.Id,
                    "that id already belongs to a policy for a different domain here");
                continue;
            }

            var row = new MtaStsPolicy
            {
                Id = policy.Id,
                DomainId = domainRow.Id,
                Enabled = policy.Enabled,
                Mode = policy.Mode,
                MaxAgeSeconds = policy.MaxAgeSeconds,
                MxPatterns = policy.MxPatterns,
                PolicyId = policy.PolicyId,
                ModeChangedAtUtc = policy.ModeChangedAtUtc,
                CreatedAtUtc = policy.CreatedAtUtc,
                UpdatedAtUtc = policy.UpdatedAtUtc,
            };

            db.MtaStsPolicies.Add(row);
            state.MtaStsPolicyIdsInUse.Add(row.Id);
            state.MtaStsPoliciesByDomainId[domainRow.Id] = row;
            tally.Created++;
        }
    }

    /// <summary>
    /// Matched on Id and nothing else. <c>mailbox_source</c> has no unique index beyond its
    /// primary key, and that is not an oversight: two sources may legitimately share host and
    /// username — different folders, different default clients — so host+username is not an
    /// identity and deduping on it would silently collapse two real sources into one.
    /// </summary>
    private void ImportReportSources(BackupArtifact artifact, ImportState state, Tally tally)
    {
        foreach (var source in artifact.ReportSources)
        {
            var label = $"id:{source.Id}";

            if (!TryResolveClientId(state, source.DefaultClientId, out var defaultClientId))
            {
                Skip(tally, BackupImportEntities.ReportSource, label, source.Id, Guid.Empty,
                    $"its default client ({source.DefaultClientId}) is neither in this artifact nor in " +
                    "this install, so newly discovered domains would have nowhere to land");
                continue;
            }

            if (state.SourcesById.TryGetValue(source.Id, out var existing))
            {
                // The IMAP checkpoint is deliberately left alone rather than reset: it is this
                // install's own record of how far it has read that mailbox, and clearing it
                // would re-fetch the entire history for a config edit.
                existing.Name = source.Name;
                existing.Protocol = source.Protocol;
                existing.Host = source.Host;
                existing.Port = source.Port;
                existing.UseTls = source.UseTls;
                existing.Username = source.Username;
                existing.PasswordEncrypted = source.PasswordEncrypted;
                existing.DefaultClientId = defaultClientId;
                existing.IsActive = source.IsActive;
                existing.CreatedAtUtc = source.CreatedAtUtc;
                existing.UpdatedAtUtc = source.UpdatedAtUtc;
                tally.Updated++;
                continue;
            }

            var row = new ReportSource
            {
                Id = source.Id,
                Name = source.Name,
                Protocol = source.Protocol,
                Host = source.Host,
                Port = source.Port,
                UseTls = source.UseTls,
                Username = source.Username,
                PasswordEncrypted = source.PasswordEncrypted,
                DefaultClientId = defaultClientId,
                IsActive = source.IsActive,
                CreatedAtUtc = source.CreatedAtUtc,
                UpdatedAtUtc = source.UpdatedAtUtc,

                // Left null on purpose, which is why the checkpoint is not exported: a new
                // source must rescan from the beginning. A checkpoint from another install's
                // view of the mailbox would skip mail, and UIDVALIDITY makes a stale one
                // actively misleading.
                LastProcessedUid = null,
                LastProcessedUidValidity = null,
            };

            db.ReportSources.Add(row);
            state.SourcesById[row.Id] = row;
            tally.Created++;
        }
    }

    /// <summary>
    /// On an email collision the imported account wins — hash, display name, role, active
    /// flag. That is what makes a restore faithful: the operator signs back in with the
    /// pre-disaster credentials their password manager already holds, rather than inheriting
    /// whatever the bootstrap account was given ten minutes ago.
    /// </summary>
    private BackupImportUserReport ImportUsers(BackupArtifact artifact, ImportState state, Tally tally)
    {
        var created = new List<string>();
        var updated = new List<string>();
        var passwordChanged = new List<string>();
        var sessionsToInvalidate = new List<Guid>();

        foreach (var user in artifact.Users)
        {
            var email = NormalizeText(user.Email);

            if (state.UsersByEmail.TryGetValue(email, out var existing))
            {
                state.UserIds[user.Id] = existing.Id;
                NoteKeptExistingId(tally, BackupImportEntities.AgencyUser, email, user.Id, existing.Id);

                // Compared before the assignment, and only for a collision: a created account
                // has no sessions to end. This list is the whole reason the caller can invalidate
                // *exactly* the affected sessions instead of signing out the admin running the
                // import for no reason.
                if (!string.Equals(existing.PasswordHash, user.PasswordHash, StringComparison.Ordinal))
                {
                    passwordChanged.Add(email);
                    sessionsToInvalidate.Add(existing.Id);
                }

                existing.PasswordHash = user.PasswordHash;
                existing.DisplayName = user.DisplayName;
                existing.Role = user.Role;
                existing.IsActive = user.IsActive;
                existing.CreatedAtUtc = user.CreatedAtUtc;
                existing.UpdatedAtUtc = user.UpdatedAtUtc;
                updated.Add(email);
                tally.Updated++;
                continue;
            }

            if (state.UserIdsInUse.Contains(user.Id))
            {
                Skip(tally, BackupImportEntities.AgencyUser, email, user.Id, user.Id,
                    "that id already belongs to an account with a different email here; importing it " +
                    "would either violate the primary key or take over an unrelated account");
                continue;
            }

            var row = new AgencyUser
            {
                Id = user.Id,
                // Stored lowercased because every sign-in path lowercases the address before it
                // looks a user up: a mixed-case row is an account nobody can authenticate as.
                Email = email,
                PasswordHash = user.PasswordHash,
                DisplayName = user.DisplayName,
                Role = user.Role,
                IsActive = user.IsActive,
                CreatedAtUtc = user.CreatedAtUtc,
                UpdatedAtUtc = user.UpdatedAtUtc,
            };

            db.AgencyUsers.Add(row);
            state.UserIdsInUse.Add(row.Id);
            state.UsersByEmail[email] = row;
            state.UserIds[user.Id] = row.Id;
            created.Add(email);
            tally.Created++;
        }

        return new BackupImportUserReport(created, updated, passwordChanged, sessionsToInvalidate);
    }

    private void ImportUserIdentities(BackupArtifact artifact, ImportState state, Tally tally)
    {
        foreach (var identity in artifact.UserIdentities)
        {
            var key = IdentityKey(identity.Issuer, identity.Subject);

            if (!TryResolveUserId(state, identity.UserId, out var userId))
            {
                Skip(tally, BackupImportEntities.UserIdentity, key, identity.Id, Guid.Empty,
                    $"the account it links ({identity.UserId}) is neither in this artifact nor in this " +
                    "install");
                continue;
            }

            if (state.IdentitiesByIssuerSubject.TryGetValue(key, out var existing))
            {
                NoteKeptExistingId(tally, BackupImportEntities.UserIdentity, key, identity.Id, existing.Id);

                // Re-pointing an external identity at the artifact's account is the same rule as
                // "the imported user wins", applied to federated login: the artifact is this
                // operator's own configuration, and it is authoritative about who that subject is.
                existing.UserId = userId;
                existing.EmailAtLink = identity.EmailAtLink;
                existing.CreatedAtUtc = identity.CreatedAtUtc;
                tally.Updated++;
                continue;
            }

            if (state.IdentityIdsInUse.Contains(identity.Id))
            {
                Skip(tally, BackupImportEntities.UserIdentity, key, identity.Id, identity.Id,
                    "that id already belongs to a different issuer/subject pair here");
                continue;
            }

            var row = new UserIdentity
            {
                Id = identity.Id,
                UserId = userId,
                Issuer = identity.Issuer,
                Subject = identity.Subject,
                EmailAtLink = identity.EmailAtLink,
                CreatedAtUtc = identity.CreatedAtUtc,
            };

            db.UserIdentities.Add(row);
            state.IdentityIdsInUse.Add(row.Id);
            state.IdentitiesByIssuerSubject[key] = row;
            tally.Created++;
        }
    }

    private void ImportGrants(BackupArtifact artifact, ImportState state, Tally tally)
    {
        foreach (var grant in artifact.Grants)
        {
            if (!TryResolveUserId(state, grant.UserId, out var userId)
                || !TryResolveClientId(state, grant.ClientId, out var clientId))
            {
                Skip(tally, BackupImportEntities.UserClientGrant,
                    GrantKey(grant.UserId, grant.ClientId), grant.Id, Guid.Empty,
                    "the account or the client it joins is neither in this artifact nor in this install");
                continue;
            }

            // Keyed on the *resolved* ids, so a grant still matches after merge kept an existing
            // user's or client's Id. Keyed on the artifact's ids it would insert a duplicate and
            // hit the (userId, clientId) unique index.
            var key = GrantKey(userId, clientId);

            // The grant's author may be an account that is in neither side. The FK is
            // ON DELETE SET NULL, so null is a value the schema already treats as "unknown" —
            // and who created a grant matters far less than the access it confers.
            Guid? createdBy = null;
            if (grant.CreatedByUserId is { } author && TryResolveUserId(state, author, out var authorId))
            {
                createdBy = authorId;
            }

            if (state.GrantsByUserClient.TryGetValue(key, out var existing))
            {
                NoteKeptExistingId(tally, BackupImportEntities.UserClientGrant, key, grant.Id, existing.Id);
                existing.CreatedAtUtc = grant.CreatedAtUtc;
                existing.CreatedByUserId = createdBy;
                tally.Updated++;
                continue;
            }

            if (state.GrantIdsInUse.Contains(grant.Id))
            {
                Skip(tally, BackupImportEntities.UserClientGrant, key, grant.Id, grant.Id,
                    "that id already belongs to a different account/client pairing here");
                continue;
            }

            var row = new UserClientGrant
            {
                Id = grant.Id,
                UserId = userId,
                ClientId = clientId,
                CreatedAtUtc = grant.CreatedAtUtc,
                CreatedByUserId = createdBy,
            };

            db.UserClientGrants.Add(row);
            state.GrantIdsInUse.Add(row.Id);
            state.GrantsByUserClient[key] = row;
            tally.Created++;
        }
    }

    private void ImportNotificationRecipients(BackupArtifact artifact, ImportState state, Tally tally)
    {
        foreach (var recipient in artifact.NotificationRecipients)
        {
            var email = NormalizeText(recipient.Email);
            Guid? clientId = null;

            if (recipient.ClientId is { } artifactClientId)
            {
                if (!TryResolveClientId(state, artifactClientId, out var resolved))
                {
                    Skip(tally, BackupImportEntities.NotificationRecipient,
                        RecipientKey(artifactClientId, email), recipient.Id, Guid.Empty,
                        $"its client ({artifactClientId}) is neither in this artifact nor in this install");
                    continue;
                }

                clientId = resolved;
            }

            var key = RecipientKey(clientId, email);

            if (state.RecipientsByScopeEmail.TryGetValue(key, out var existing))
            {
                NoteKeptExistingId(
                    tally, BackupImportEntities.NotificationRecipient, key, recipient.Id, existing.Id);
                existing.Kind = recipient.Kind;
                existing.IsActive = recipient.IsActive;
                existing.CreatedAtUtc = recipient.CreatedAtUtc;
                existing.UpdatedAtUtc = recipient.UpdatedAtUtc;
                tally.Updated++;
                continue;
            }

            if (state.RecipientIdsInUse.Contains(recipient.Id))
            {
                Skip(tally, BackupImportEntities.NotificationRecipient, key, recipient.Id, recipient.Id,
                    "that id already belongs to a different address or scope here");
                continue;
            }

            var row = new NotificationRecipient
            {
                Id = recipient.Id,
                ClientId = clientId,
                Email = email,
                Kind = recipient.Kind,
                IsActive = recipient.IsActive,
                CreatedAtUtc = recipient.CreatedAtUtc,
                UpdatedAtUtc = recipient.UpdatedAtUtc,
            };

            db.NotificationRecipients.Add(row);
            state.RecipientIdsInUse.Add(row.Id);
            state.RecipientsByScopeEmail[key] = row;
            tally.Created++;
        }
    }

    /// <summary>
    /// Loads every configuration table whole, and tracked.
    /// <para>
    /// Whole, because these tables are hundreds of rows — that asymmetry against five million
    /// report rows is the entire premise of this feature — so one pass each is cheaper than a
    /// query per artifact row. Tracked, because that is what lets an update be a property
    /// assignment the single <c>SaveChanges</c> at the end picks up, and it keeps the update
    /// path and the insert path in one readable shape.
    /// </para>
    /// </summary>
    private async Task<ImportState> LoadStateAsync(CancellationToken ct)
    {
        // Ordered oldest-first so that if an install somehow holds two rows whose natural keys
        // differ only by case — Postgres' unique indexes are case-sensitive, so it can — the
        // one the import matches is deterministic rather than whatever the provider returned
        // first.
        var clients = await db.Clients.OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
        var domains = await db.Domains.OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
        var sources = await db.ReportSources.OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
        var users = await db.AgencyUsers.OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
        var identities = await db.UserIdentities.OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
        var grants = await db.UserClientGrants.OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
        var recipients = await db.NotificationRecipients.OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
        var mtaStsPolicies = await db.MtaStsPolicies.OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);

        return new ImportState
        {
            ClientsBySlug = Index(clients, x => NormalizeText(x.Slug)),
            ClientIdsInUse = [.. clients.Select(x => x.Id)],
            DomainsByName = Index(domains, x => NormalizeText(x.Name)),
            DomainIdsInUse = [.. domains.Select(x => x.Id)],
            SourcesById = sources.ToDictionary(x => x.Id),
            UsersByEmail = Index(users, x => NormalizeText(x.Email)),
            UserIdsInUse = [.. users.Select(x => x.Id)],
            IdentitiesByIssuerSubject = Index(identities, x => IdentityKey(x.Issuer, x.Subject)),
            IdentityIdsInUse = [.. identities.Select(x => x.Id)],
            GrantsByUserClient = Index(grants, x => GrantKey(x.UserId, x.ClientId)),
            GrantIdsInUse = [.. grants.Select(x => x.Id)],
            RecipientsByScopeEmail = Index(recipients, x => RecipientKey(x.ClientId, x.Email)),
            RecipientIdsInUse = [.. recipients.Select(x => x.Id)],
            MtaStsPoliciesByDomainId = mtaStsPolicies.ToDictionary(x => x.DomainId),
            MtaStsPolicyIdsInUse = [.. mtaStsPolicies.Select(x => x.Id)],
        };
    }

    /// <summary>
    /// First-wins, rather than <c>ToDictionary</c>, which throws on a duplicate key. The keys
    /// here are unique in the database, but only under a case-sensitive index — a duplicate
    /// after normalisation is possible, and an operator mid-recovery should get a conflict
    /// report, not an exception from the indexing step.
    /// </summary>
    private static Dictionary<string, T> Index<T>(IEnumerable<T> rows, Func<T, string> key)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            map.TryAdd(key(row), row);
        }

        return map;
    }

    /// <summary>
    /// Natural-key text is normalised the same way the services that write it normalise it
    /// (<c>ClientService</c>, <c>DomainService</c>, <c>AuthService</c> all trim and lowercase),
    /// so a hand-edited artifact matches the rows an export produced.
    /// </summary>
    private static string NormalizeText(string value) => value.Trim().ToLowerInvariant();

    /// <summary>
    /// Case is preserved: an OIDC issuer and subject are opaque, case-sensitive strings, and
    /// folding them would let two distinct external identities collapse into one account.
    /// </summary>
    private static string IdentityKey(string issuer, string subject)
        => $"{issuer.Trim()}\n{subject.Trim()}";

    private static string GrantKey(Guid userId, Guid clientId) => $"{userId:N}|{clientId:N}";

    /// <summary>
    /// A null client is the agency-wide scope and gets its own bucket, matching the
    /// <c>(clientId, email)</c> unique index: the same address subscribed agency-wide is a
    /// different row from that address subscribed to one client, and merging them would
    /// silently narrow who gets alerted.
    /// </summary>
    private static string RecipientKey(Guid? clientId, string email)
        => $"{(clientId is { } id ? id.ToString("N") : "agency")}|{NormalizeText(email)}";

    /// <summary>
    /// The artifact's client Id translated to the one this install uses: the map when the
    /// client travelled in this artifact, the Id itself when the artifact only referenced a
    /// client that already exists here. Anything else has no owner, and the caller skips the
    /// row rather than letting a foreign key fail the whole import.
    /// </summary>
    private static bool TryResolveClientId(ImportState state, Guid artifactClientId, out Guid clientId)
    {
        if (state.ClientIds.TryGetValue(artifactClientId, out clientId))
        {
            return true;
        }

        if (state.ClientIdsInUse.Contains(artifactClientId))
        {
            clientId = artifactClientId;
            return true;
        }

        clientId = Guid.Empty;
        return false;
    }

    private static bool TryResolveUserId(ImportState state, Guid artifactUserId, out Guid userId)
    {
        if (state.UserIds.TryGetValue(artifactUserId, out userId))
        {
            return true;
        }

        if (state.UserIdsInUse.Contains(artifactUserId))
        {
            userId = artifactUserId;
            return true;
        }

        userId = Guid.Empty;
        return false;
    }

    private static void NoteKeptExistingId(
        Tally tally, string entity, string naturalKey, Guid artifactId, Guid existingId)
    {
        if (artifactId == existingId)
        {
            // The common case, and the point of exporting Ids verbatim: same row, same id,
            // nothing to reconcile and nothing worth reporting.
            return;
        }

        tally.Conflicts.Add(new BackupImportConflict(
            entity, naturalKey, artifactId, existingId,
            BackupImportResolutions.KeptExistingId,
            "the natural key matched a row with a different id; the existing id is kept — a primary " +
            "key cannot be rewritten — and everything in the artifact that referenced the artifact's " +
            "id is repointed at it"));
    }

    private static void Skip(
        Tally tally, string entity, string naturalKey, Guid artifactId, Guid existingId, string reason)
    {
        tally.Skipped++;
        tally.Conflicts.Add(new BackupImportConflict(
            entity, naturalKey, artifactId, existingId, BackupImportResolutions.Skipped, reason));
    }

    /// <summary>Mutable while a pass runs, frozen into the immutable per-entity report after.</summary>
    private sealed class Tally(string entity)
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public List<BackupImportConflict> Conflicts { get; } = [];

        public BackupImportEntityCounts Freeze() => new(entity, Created, Updated, Skipped, Conflicts);
    }

    /// <summary>
    /// Everything already here, indexed by the natural keys the spec pins — plus the Id
    /// translation the merge path builds as it goes.
    /// </summary>
    private sealed class ImportState
    {
        public required Dictionary<string, Client> ClientsBySlug { get; init; }
        public required Dictionary<string, Domain> DomainsByName { get; init; }

        /// <summary>Keyed by Id alone, because <c>mailbox_source</c> has no natural key at all.</summary>
        public required Dictionary<Guid, ReportSource> SourcesById { get; init; }
        public required Dictionary<string, AgencyUser> UsersByEmail { get; init; }
        public required Dictionary<string, UserIdentity> IdentitiesByIssuerSubject { get; init; }
        public required Dictionary<string, UserClientGrant> GrantsByUserClient { get; init; }
        public required Dictionary<string, NotificationRecipient> RecipientsByScopeEmail { get; init; }

        /// <summary>Keyed by DomainId — the unique index the table itself enforces.</summary>
        public required Dictionary<Guid, MtaStsPolicy> MtaStsPoliciesByDomainId { get; init; }

        /// <summary>
        /// Ids already spoken for, per table, including the ones this pass has just added. They
        /// are what turns "the artifact wants to insert an Id that is taken by a row with a
        /// different natural key" from a primary-key violation into a reported conflict.
        /// </summary>
        public required HashSet<Guid> ClientIdsInUse { get; init; }
        public required HashSet<Guid> DomainIdsInUse { get; init; }
        public required HashSet<Guid> UserIdsInUse { get; init; }
        public required HashSet<Guid> IdentityIdsInUse { get; init; }
        public required HashSet<Guid> GrantIdsInUse { get; init; }
        public required HashSet<Guid> RecipientIdsInUse { get; init; }
        public required HashSet<Guid> MtaStsPolicyIdsInUse { get; init; }

        /// <summary>
        /// Artifact client Id → the Id this install actually uses. Identical for everything a
        /// restore inserts; different exactly where merge matched a slug that was already here,
        /// which is where an un-remapped domain, source, grant or recipient would end up
        /// pointing at a client that does not exist.
        /// </summary>
        public Dictionary<Guid, Guid> ClientIds { get; } = [];

        /// <summary>Artifact user Id → the Id this install uses, for the same reason.</summary>
        public Dictionary<Guid, Guid> UserIds { get; } = [];
    }
}
