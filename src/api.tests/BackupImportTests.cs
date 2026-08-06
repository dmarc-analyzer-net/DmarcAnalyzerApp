using System.Text.Json;
using DmarcAnalyzer.Api.Application.Backup;
using DmarcAnalyzer.Api.Application.Clients;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Import is the half of the backup story that runs while someone is having a bad day, so
/// the properties asserted here are the ones an operator is betting a recovery on.
/// <para>
/// Three of them carry the design. <b>It never deletes</b> — the admin running the import is
/// signed in as a bootstrapped account no artifact contains, and a tidy-up would delete the
/// session doing the work. <b>The preview is the apply</b> — same computation, minus the
/// save — because a preview that estimates is a number nobody should make a recovery
/// decision on. And <b>it refuses rather than guesses</b>: a wrong key or an unknown format
/// version has to fail loudly here, because both otherwise present as a restore that looked
/// fine and was quietly wrong.
/// </para>
/// </summary>
public sealed class BackupImportTests
{
    private const string Key = "lSqzPZf0negcljwLKSzvZhIZlvd5hya25OYp1ogntKk=";
    private static readonly string OtherKey = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
    private static readonly DateTime Stamp = new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);

    private static DmarcAnalyzerDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<DmarcAnalyzerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new DmarcAnalyzerDbContext(options);
    }

    private static BackupImportService Service(DmarcAnalyzerDbContext db, string? key = Key)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Security:CredentialEncryptionKey"] = key,
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new BackupImportService(db, configuration, NullLogger<BackupImportService>.Instance);
    }

    private static BackupManifest Manifest(
        int formatVersion = BackupJson.FormatVersion,
        string? keyForFingerprint = Key,
        bool credentialsProtected = true)
        => new(
            FormatVersion: formatVersion,
            ExportedAtUtc: Stamp,
            AppVersion: "1.0.0",
            MigrationId: null,
            MigrationCount: 0,
            EncryptionKeyFingerprint: CredentialKeyFingerprint.Compute(keyForFingerprint),
            CredentialsProtected: credentialsProtected,
            Scope: new BackupScope(true, "none", "none"),
            Excluded: new Dictionary<string, long>(),
            LegalHoldClients: []);

    private static BackupArtifact Artifact(
        BackupManifest? manifest = null,
        IReadOnlyList<BackupClient>? clients = null,
        IReadOnlyList<BackupDomain>? domains = null,
        IReadOnlyList<BackupMailboxSource>? sources = null,
        IReadOnlyList<BackupNotificationRecipient>? recipients = null,
        IReadOnlyList<BackupUser>? users = null,
        IReadOnlyList<BackupUserIdentity>? identities = null,
        IReadOnlyList<BackupUserClientGrant>? grants = null)
        => new(
            manifest ?? Manifest(),
            clients ?? [],
            domains ?? [],
            sources ?? [],
            recipients ?? [],
            users ?? [],
            identities ?? [],
            grants ?? []);

    private static BackupClient ExportedClient(
        Guid id, string slug, string? name = null, int retentionMonths = 12, bool legalHold = false)
        => new(id, name ?? slug, slug, true, retentionMonths, legalHold, true, null, null, "UTC", Stamp, Stamp);

    private static BackupDomain ExportedDomain(Guid id, Guid clientId, string name)
        => new(id, clientId, name, true, Stamp, Stamp);

    private static BackupMailboxSource ExportedSource(
        Guid id, Guid defaultClientId, string name = "Mailbox",
        string host = "imap.example", string username = "dmarc@example",
        string password = "enc:v1:ZmFrZS1jaXBoZXJ0ZXh0")
        => new(id, name, "imap", host, 993, true, username, password, defaultClientId, true, Stamp, Stamp);

    private static BackupUser ExportedUser(
        Guid id, string email, string hash = "pbkdf2$restored", string display = "Restored",
        string role = "agency_admin", bool isActive = true)
        => new(id, email, hash, display, role, isActive, Stamp, Stamp);

    private static BackupUserIdentity ExportedIdentity(Guid id, Guid userId, string subject)
        => new(id, userId, "https://idp.example", subject, "restored@acme.example", Stamp);

    private static BackupUserClientGrant ExportedGrant(Guid id, Guid userId, Guid clientId)
        => new(id, userId, clientId, Stamp, null);

    private static BackupNotificationRecipient ExportedRecipient(Guid id, Guid? clientId, string email)
        => new(id, clientId, email, "both", true, Stamp, Stamp);

    private static BackupImportEntityCounts Counts(BackupImportResult result, string entity)
        => result.Entities.Single(x => x.Entity == entity);

    /// <summary>
    /// The invariant. An install holds rows the artifact has never heard of — most obviously
    /// the bootstrap admin running the import — and every one of them survives.
    /// </summary>
    [Fact]
    public async Task NeverDeletesWhatTheArtifactDoesNotMention()
    {
        await using var db = NewDb();

        var beta = new Client { Slug = "beta", Name = "Beta", Timezone = "UTC" };
        var betaDomain = new Domain { ClientId = beta.Id, Name = "beta.example" };
        var betaSource = new MailboxSource
        {
            Name = "Beta mailbox", Host = "imap.beta", Port = 993, Username = "dmarc@beta",
            PasswordEncrypted = "enc:v1:YmV0YQ==", DefaultClientId = beta.Id,
        };
        var bootstrap = new AgencyUser
        {
            Email = "bootstrap@beta.example", DisplayName = "Bootstrap", Role = "agency_admin",
            PasswordHash = "pbkdf2$bootstrap",
        };

        db.AddRange(beta, betaDomain, betaSource, bootstrap);
        db.Add(new UserClientGrant { UserId = bootstrap.Id, ClientId = beta.Id });
        db.Add(new NotificationRecipient { ClientId = beta.Id, Email = "ops@beta.example", Kind = "both" });
        await db.SaveChangesAsync();

        var acmeId = Guid.NewGuid();
        var artifact = Artifact(
            clients: [ExportedClient(acmeId, "acme")],
            domains: [ExportedDomain(Guid.NewGuid(), acmeId, "acme.example")],
            users: [ExportedUser(Guid.NewGuid(), "restored@acme.example")]);

        var result = await Service(db).ImportAsync(artifact, BackupImportModes.Merge, false, default);

        Assert.True(result.IsSuccess);

        // Additive, all of it: two clients, two domains, two users, and the grant, recipient
        // and source that only the install knew about are all still here.
        Assert.Equal(2, await db.Clients.CountAsync());
        Assert.NotNull(await db.Clients.SingleOrDefaultAsync(x => x.Slug == "beta"));
        Assert.Equal(2, await db.Domains.CountAsync());
        Assert.NotNull(await db.Domains.SingleOrDefaultAsync(x => x.Name == "beta.example"));
        Assert.Equal(2, await db.AgencyUsers.CountAsync());
        Assert.NotNull(await db.AgencyUsers.SingleOrDefaultAsync(x => x.Email == "bootstrap@beta.example"));
        Assert.Equal(1, await db.UserClientGrants.CountAsync());
        Assert.Equal(1, await db.NotificationRecipients.CountAsync());
        Assert.Equal(1, await db.MailboxSources.CountAsync());
    }

    /// <summary>
    /// Restore is a copy, and a non-destructive import cannot copy a state in which something
    /// had been deleted before the disaster — it would produce a union and call it a restore.
    /// So it refuses instead, and refuses before writing anything.
    /// </summary>
    [Fact]
    public async Task RestoreIsRefusedWhenTheInstallIsNotEmpty()
    {
        await using var db = NewDb();
        db.Add(new Client { Slug = "beta", Name = "Beta", Timezone = "UTC" });
        await db.SaveChangesAsync();

        var artifact = Artifact(clients: [ExportedClient(Guid.NewGuid(), "acme")]);

        var result = await Service(db).ImportAsync(artifact, BackupImportModes.Restore, false, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(409, result.StatusCode);
        Assert.Contains("nothing has been added", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("merge", result.Error!, StringComparison.OrdinalIgnoreCase);

        // Refused, not partially applied.
        Assert.Equal(1, await db.Clients.CountAsync());

        // The same artifact is fine as a merge; the mode is the only thing that differed.
        Assert.True((await Service(db).ImportAsync(artifact, BackupImportModes.Merge, false, default)).IsSuccess);
    }

    /// <summary>
    /// A restore has to survive what bootstrap leaves behind, which is a user *and* the default
    /// client. Neither is counted: the console's own bootstrap flow is how the operator got an
    /// account to run the import with, and the default client is created by that same flow — so
    /// counting either would make restore unreachable on every install that could need it.
    /// </summary>
    [Fact]
    public async Task RestoreRunsOnAnInstallHoldingOnlyWhatBootstrapCreated()
    {
        await using var db = NewDb();
        db.Add(new AgencyUser
        {
            Email = "bootstrap@acme.example", DisplayName = "Bootstrap", Role = "agency_admin",
            PasswordHash = "pbkdf2$bootstrap",
        });
        await DefaultClient.EnsureAsync(db, default);
        await db.SaveChangesAsync();

        var clientId = Guid.NewGuid();
        var artifact = Artifact(clients: [ExportedClient(clientId, "acme")]);

        var result = await Service(db).ImportAsync(artifact, BackupImportModes.Restore, false, default);

        Assert.True(result.IsSuccess);
        Assert.Contains(clientId, await db.Clients.Select(x => x.Id).ToListAsync());
    }

    /// <summary>
    /// Emptiness is measured on clients and domains, deliberately not on users: the console's
    /// bootstrap flow is how the operator got an account to run the import with, so a restore
    /// always finds one user already here. Requiring zero users would make restore unreachable.
    /// </summary>
    [Fact]
    public async Task RestoreRunsOnAFreshInstallAndWritesTheIdsVerbatim()
    {
        await using var db = NewDb();
        db.Add(new AgencyUser
        {
            Email = "bootstrap@acme.example", DisplayName = "Bootstrap", Role = "agency_admin",
            PasswordHash = "pbkdf2$bootstrap",
        });
        await db.SaveChangesAsync();

        var clientId = Guid.NewGuid();
        var domainId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var grantId = Guid.NewGuid();

        var artifact = Artifact(
            clients: [ExportedClient(clientId, "acme")],
            domains: [ExportedDomain(domainId, clientId, "acme.example")],
            sources: [ExportedSource(sourceId, clientId)],
            recipients: [ExportedRecipient(Guid.NewGuid(), clientId, "ops@acme.example")],
            users: [ExportedUser(userId, "admin@acme.example")],
            identities: [ExportedIdentity(Guid.NewGuid(), userId, "sub-1")],
            grants: [ExportedGrant(grantId, userId, clientId)]);

        var result = await Service(db).ImportAsync(artifact, BackupImportModes.Restore, false, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(BackupImportModes.Restore, result.Value!.Mode);

        // Guid keys are client-generated, so the artifact's ids land as-is and every foreign
        // key in it stays valid with no rewiring.
        Assert.Equal(clientId, (await db.Clients.SingleAsync()).Id);
        Assert.Equal(domainId, (await db.Domains.SingleAsync()).Id);
        Assert.Equal(clientId, (await db.Domains.SingleAsync()).ClientId);
        Assert.Equal(clientId, (await db.MailboxSources.SingleAsync()).DefaultClientId);

        var grant = await db.UserClientGrants.SingleAsync();
        Assert.Equal(grantId, grant.Id);
        Assert.Equal(userId, grant.UserId);
        Assert.Equal(userId, (await db.UserIdentities.SingleAsync()).UserId);

        // The bootstrap admin's email did not collide, so it survives as a break-glass account
        // rather than being cleaned up.
        Assert.Equal(2, await db.AgencyUsers.CountAsync());
        Assert.Empty(result.Value!.Users.SessionsToInvalidateUserIds);

        // Nothing matched, so nothing conflicted.
        Assert.All(result.Value!.Entities, x => Assert.Empty(x.Conflicts));

        // The IMAP checkpoint is not in the artifact and must not be invented: a restored
        // source rescans from the beginning.
        Assert.Null((await db.MailboxSources.SingleAsync()).LastProcessedUid);
    }

    /// <summary>
    /// Merge's contract: match on the natural key, keep the row that is already here, and
    /// repoint everything in the artifact that referenced the artifact's id at the id this
    /// install uses. Without that last part the import produces domains, sources and grants
    /// hanging off a client that does not exist — an orphan set that reads as a success.
    /// </summary>
    [Fact]
    public async Task MergeUpsertsByNaturalKeyKeepsTheExistingIdAndRepointsChildren()
    {
        await using var db = NewDb();

        var existingClient = new Client { Slug = "acme", Name = "Acme (stale)", RetentionMonths = 27, Timezone = "UTC" };
        var existingUser = new AgencyUser
        {
            Email = "admin@acme.example", DisplayName = "Stale", Role = "agency_analyst",
            PasswordHash = "pbkdf2$stale",
        };
        db.AddRange(existingClient, existingUser);
        await db.SaveChangesAsync();

        var artifactClientId = Guid.NewGuid();
        var artifactUserId = Guid.NewGuid();

        var artifact = Artifact(
            clients: [ExportedClient(artifactClientId, "acme", name: "Acme", retentionMonths: 3)],
            domains: [ExportedDomain(Guid.NewGuid(), artifactClientId, "acme.example")],
            sources: [ExportedSource(Guid.NewGuid(), artifactClientId)],
            recipients: [ExportedRecipient(Guid.NewGuid(), artifactClientId, "ops@acme.example")],
            users: [ExportedUser(artifactUserId, "admin@acme.example")],
            grants: [ExportedGrant(Guid.NewGuid(), artifactUserId, artifactClientId)]);

        var result = await Service(db).ImportAsync(artifact, BackupImportModes.Merge, false, default);

        Assert.True(result.IsSuccess);

        // One client, still the original row, carrying the artifact's values.
        var client = await db.Clients.SingleAsync();
        Assert.Equal(existingClient.Id, client.Id);
        Assert.Equal("Acme", client.Name);
        Assert.Equal(3, client.RetentionMonths);
        Assert.Null(await db.Clients.SingleOrDefaultAsync(x => x.Id == artifactClientId));

        // Every child followed the id that won, not the id the file carried.
        Assert.Equal(existingClient.Id, (await db.Domains.SingleAsync()).ClientId);
        Assert.Equal(existingClient.Id, (await db.MailboxSources.SingleAsync()).DefaultClientId);
        Assert.Equal(existingClient.Id, (await db.NotificationRecipients.SingleAsync()).ClientId);

        var grant = await db.UserClientGrants.SingleAsync();
        Assert.Equal(existingClient.Id, grant.ClientId);
        Assert.Equal(existingUser.Id, grant.UserId);

        // And the disagreement is reported rather than resolved quietly.
        var conflict = Assert.Single(Counts(result.Value!, BackupImportEntities.Client).Conflicts);
        Assert.Equal(BackupImportResolutions.KeptExistingId, conflict.Resolution);
        Assert.Equal(artifactClientId, conflict.ArtifactId);
        Assert.Equal(existingClient.Id, conflict.ExistingId);
        Assert.Equal("acme", conflict.NaturalKey);

        Assert.Equal(1, Counts(result.Value!, BackupImportEntities.Client).Updated);
        Assert.Equal(0, Counts(result.Value!, BackupImportEntities.Client).Created);
        Assert.Equal(1, Counts(result.Value!, BackupImportEntities.Domain).Created);
    }

    /// <summary>
    /// On an email collision the imported account wins outright. That is what makes a restore
    /// faithful — the operator signs back in with the credentials their password manager
    /// already holds — and it is why the changed-hash list has to be exact: it is the only
    /// thing that lets the caller end the sessions that just became invalid without signing
    /// out the admin performing the import.
    /// </summary>
    [Fact]
    public async Task ImportedUserWinsAndOnlyChangedHashesAreReported()
    {
        await using var db = NewDb();

        var admin = new AgencyUser
        {
            Email = "admin@acme.example", DisplayName = "Bootstrap", Role = "agency_admin",
            PasswordHash = "pbkdf2$bootstrap", IsActive = true,
        };
        var analyst = new AgencyUser
        {
            Email = "analyst@acme.example", DisplayName = "Analyst", Role = "agency_analyst",
            PasswordHash = "pbkdf2$unchanged",
        };
        db.AddRange(admin, analyst);
        await db.SaveChangesAsync();

        var artifact = Artifact(users:
        [
            ExportedUser(Guid.NewGuid(), "admin@acme.example", hash: "pbkdf2$predisaster",
                display: "Pre-disaster admin", role: "agency_analyst", isActive: false),
            ExportedUser(Guid.NewGuid(), "analyst@acme.example", hash: "pbkdf2$unchanged",
                display: "Analyst", role: "agency_analyst"),
            ExportedUser(Guid.NewGuid(), "new@acme.example"),
        ]);

        var result = await Service(db).ImportAsync(artifact, BackupImportModes.Merge, false, default);

        Assert.True(result.IsSuccess);

        var stored = await db.AgencyUsers.SingleAsync(x => x.Email == "admin@acme.example");
        Assert.Equal(admin.Id, stored.Id);
        Assert.Equal("pbkdf2$predisaster", stored.PasswordHash);
        Assert.Equal("Pre-disaster admin", stored.DisplayName);
        Assert.Equal("agency_analyst", stored.Role);
        Assert.False(stored.IsActive);

        var users = result.Value!.Users;
        Assert.Equal(["new@acme.example"], users.CreatedEmails);
        Assert.Equal(["admin@acme.example", "analyst@acme.example"], users.UpdatedEmails.Order().ToArray());

        // The analyst's hash is byte-identical, so nothing about their session became invalid.
        Assert.Equal(["admin@acme.example"], users.PasswordChangedEmails);
        Assert.Equal(admin.Id, Assert.Single(users.SessionsToInvalidateUserIds));

        // Reported, never acted on: ending an HTTP session is the caller's job.
        Assert.Equal(3, await db.AgencyUsers.CountAsync());
    }

    /// <summary>
    /// <c>enc:v1:</c> carries a format version and no key identity, so an artifact encrypted
    /// under a different key imports cleanly and then fails on the next mailbox sync with an
    /// authentication-tag mismatch, long after the restore was called a success. The
    /// fingerprint turns that into a refusal before anything is written.
    /// </summary>
    [Fact]
    public async Task RefusesCredentialsThisInstallHoldsNoKeyFor()
    {
        await using var db = NewDb();
        var clientId = Guid.NewGuid();

        var artifact = Artifact(
            manifest: Manifest(keyForFingerprint: OtherKey),
            clients: [ExportedClient(clientId, "acme")],
            sources: [ExportedSource(Guid.NewGuid(), clientId)]);

        var refused = await Service(db).ImportAsync(artifact, BackupImportModes.Merge, false, default);

        Assert.False(refused.IsSuccess);
        Assert.Equal(409, refused.StatusCode);
        Assert.Contains("decrypt", refused.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await db.Clients.CountAsync());

        // A preview refuses identically, so the operator meets this at the preview rather than
        // discovering it after pressing the button.
        Assert.Equal(409, (await Service(db).PreviewAsync(artifact, BackupImportModes.Merge, false, default)).StatusCode);

        // "Unknown" is never a match either: an artifact exported with no key at all cannot be
        // assumed to fit whatever key is running now.
        var unprotected = Artifact(
            manifest: Manifest(keyForFingerprint: null, credentialsProtected: false),
            clients: [ExportedClient(clientId, "acme")],
            sources: [ExportedSource(Guid.NewGuid(), clientId)]);

        Assert.Equal(409, (await Service(db).ImportAsync(unprotected, BackupImportModes.Merge, false, default)).StatusCode);
    }

    /// <summary>
    /// The override exists for the operator who wants their configuration back and accepts
    /// re-entering every mailbox password. The result has to say so: the sources import,
    /// and none of them will sync until their passwords are re-entered.
    /// </summary>
    [Fact]
    public async Task OverrideImportsTheConfigAndSaysTheCredentialsWillNotDecrypt()
    {
        await using var db = NewDb();
        var clientId = Guid.NewGuid();

        var artifact = Artifact(
            manifest: Manifest(keyForFingerprint: OtherKey),
            clients: [ExportedClient(clientId, "acme")],
            sources: [ExportedSource(Guid.NewGuid(), clientId)]);

        var result = await Service(db).ImportAsync(
            artifact, BackupImportModes.Merge, allowKeyFingerprintMismatch: true, default);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.MailboxCredentialsWillNotDecrypt);
        Assert.Contains(result.Value!.Warnings, x => x.Contains("re-entered", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, await db.MailboxSources.CountAsync());

        // An artifact with no mailbox sources has no credential that could fail to decrypt, so
        // the key is not a gate on it at all.
        await using var seedOnly = NewDb();
        var clean = await Service(seedOnly).ImportAsync(
            Artifact(manifest: Manifest(keyForFingerprint: OtherKey),
                clients: [ExportedClient(Guid.NewGuid(), "acme")]),
            BackupImportModes.Merge, false, default);

        Assert.True(clean.IsSuccess);
        Assert.False(clean.Value!.MailboxCredentialsWillNotDecrypt);
    }

    /// <summary>
    /// A newer writer may have changed what a field means, so the version is a gate and not a
    /// hint. Guessing produces the worst outcome a backup has: a restore that looks complete
    /// and is subtly wrong.
    /// </summary>
    [Fact]
    public async Task RefusesAFormatVersionItCannotRead()
    {
        await using var db = NewDb();
        var artifact = Artifact(
            manifest: Manifest(formatVersion: BackupJson.FormatVersion + 1),
            clients: [ExportedClient(Guid.NewGuid(), "acme")]);

        var result = await Service(db).ImportAsync(artifact, BackupImportModes.Merge, false, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
        Assert.Contains("formatVersion", result.Error!, StringComparison.Ordinal);
        Assert.Equal(0, await db.Clients.CountAsync());

        // Zero is what a missing manifest deserializes to — "that file was not one of ours"
        // rather than "version 0".
        var malformed = Artifact(
            manifest: Manifest(formatVersion: 0),
            clients: [ExportedClient(Guid.NewGuid(), "acme")]);

        Assert.Equal(400, (await Service(db).ImportAsync(malformed, BackupImportModes.Merge, false, default)).StatusCode);
    }

    /// <summary>
    /// The two modes have different safety properties, so an unrecognised value is a 400 and
    /// never a default — a typo must not silently pick one.
    /// </summary>
    [Fact]
    public async Task RefusesAnUnrecognisedModeInsteadOfDefaulting()
    {
        await using var db = NewDb();
        var artifact = Artifact(clients: [ExportedClient(Guid.NewGuid(), "acme")]);

        foreach (var mode in new[] { "", " ", "restor", "overwrite", "0", "1", "true" })
        {
            var result = await Service(db).ImportAsync(artifact, mode, false, default);

            Assert.False(result.IsSuccess);
            Assert.Equal(400, result.StatusCode);
            Assert.Contains(BackupImportModes.Restore, result.Error!, StringComparison.Ordinal);
            Assert.Contains(BackupImportModes.Merge, result.Error!, StringComparison.Ordinal);

            // Refused before anything was read, let alone written.
            Assert.Equal(0, await db.Clients.CountAsync());
        }

        // The specific trap this avoids: Enum.TryParse accepts numeric text, so "0" would have
        // become Restore — the destructive-looking mode — without anybody asking for it.
        Assert.False(BackupImportModes.TryParse("0", out _));
        Assert.False(BackupImportModes.TryParse(null, out _));

        // Casing and stray whitespace are spellings of a mode, not different modes.
        Assert.True(BackupImportModes.TryParse("MERGE", out var parsed));
        Assert.Equal(BackupImportMode.Merge, parsed);
        Assert.True(BackupImportModes.TryParse(" Restore ", out parsed));
        Assert.Equal(BackupImportMode.Restore, parsed);
    }

    /// <summary>
    /// The preview must be the apply with the save removed, so the counts an operator reads
    /// are the counts they get — and it must leave nothing pending, because the caller writes
    /// an audit event through the same scoped DbContext the moment this returns. If the preview
    /// left tracked inserts behind, that audit write's SaveChanges would commit an import
    /// nobody asked for.
    /// </summary>
    [Fact]
    public async Task PreviewComputesTheSameThingAndWritesNothing()
    {
        await using var db = NewDb();

        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var artifact = Artifact(
            clients: [ExportedClient(clientId, "acme")],
            domains: [ExportedDomain(Guid.NewGuid(), clientId, "acme.example")],
            sources: [ExportedSource(Guid.NewGuid(), clientId)],
            recipients: [ExportedRecipient(Guid.NewGuid(), null, "agency@acme.example")],
            users: [ExportedUser(userId, "admin@acme.example")],
            identities: [ExportedIdentity(Guid.NewGuid(), userId, "sub-1")],
            grants: [ExportedGrant(Guid.NewGuid(), userId, clientId)]);

        var preview = await Service(db).PreviewAsync(artifact, BackupImportModes.Merge, false, default);

        Assert.True(preview.IsSuccess);
        Assert.True(preview.Value!.DryRun);
        Assert.All(preview.Value!.Entities, x => Assert.Equal(1, x.Created));

        // Simulating exactly what the endpoint does next. Nothing must fall out of it.
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.Clients.CountAsync());
        Assert.Equal(0, await db.Domains.CountAsync());
        Assert.Equal(0, await db.MailboxSources.CountAsync());
        Assert.Equal(0, await db.AgencyUsers.CountAsync());
        Assert.Equal(0, await db.UserIdentities.CountAsync());
        Assert.Equal(0, await db.UserClientGrants.CountAsync());
        Assert.Equal(0, await db.NotificationRecipients.CountAsync());

        var applied = await Service(db).ImportAsync(artifact, BackupImportModes.Merge, false, default);

        Assert.True(applied.IsSuccess);
        Assert.False(applied.Value!.DryRun);

        // The promise: what the preview said, per entity, is what the apply did.
        Assert.Equal(
            preview.Value!.Entities.Select(x => (x.Entity, x.Created, x.Updated, x.Skipped)),
            applied.Value!.Entities.Select(x => (x.Entity, x.Created, x.Updated, x.Skipped)));

        Assert.Equal(1, await db.Clients.CountAsync());
        Assert.Equal(1, await db.UserClientGrants.CountAsync());
        Assert.Equal(1, await db.NotificationRecipients.CountAsync());
    }

    /// <summary>
    /// A preview over an install that already has rows must also change nothing — the update
    /// path assigns to tracked entities, and only the missing save keeps those assignments out
    /// of the database.
    /// </summary>
    [Fact]
    public async Task PreviewOfAnUpsertLeavesTheExistingRowUntouched()
    {
        await using var db = NewDb();
        var existing = new Client { Slug = "acme", Name = "Acme (stale)", RetentionMonths = 27, Timezone = "UTC" };
        db.Add(existing);
        await db.SaveChangesAsync();

        var artifact = Artifact(
            clients: [ExportedClient(Guid.NewGuid(), "acme", name: "Acme", retentionMonths: 3)]);

        var preview = await Service(db).PreviewAsync(artifact, BackupImportModes.Merge, false, default);

        Assert.Equal(1, Counts(preview.Value!, BackupImportEntities.Client).Updated);
        await db.SaveChangesAsync();

        // Re-read rather than inspecting the seeded instance: the preview does assign to the
        // tracked entity, and it is the detach-before-save that keeps the store clean.
        var stored = await db.Clients.AsNoTracking().SingleAsync();
        Assert.Equal("Acme (stale)", stored.Name);
        Assert.Equal(27, stored.RetentionMonths);
    }

    /// <summary>
    /// <c>mailbox_source</c> has no unique index beyond its primary key, and that is not an
    /// oversight: two sources may legitimately share host and username — different folders,
    /// different default clients — so an Id is the only thing that can identify one. Deduping
    /// on host+username would silently collapse two real sources into one.
    /// </summary>
    [Fact]
    public async Task MailboxSourcesAreMatchedOnIdBecauseHostAndUsernameAreNotAnIdentity()
    {
        await using var db = NewDb();

        var client = new Client { Slug = "acme", Name = "Acme", Timezone = "UTC" };
        var existing = new MailboxSource
        {
            Name = "Old name", Host = "imap.example", Port = 993, Username = "dmarc@example",
            PasswordEncrypted = "enc:v1:b2xk", DefaultClientId = client.Id, LastProcessedUid = 4711,
        };
        db.AddRange(client, existing);
        await db.SaveChangesAsync();

        var artifact = Artifact(
            sources:
            [
                ExportedSource(existing.Id, client.Id, name: "New name", password: "enc:v1:bmV3"),
                ExportedSource(Guid.NewGuid(), client.Id, name: "Second folder"),
            ]);

        var result = await Service(db).ImportAsync(artifact, BackupImportModes.Merge, false, default);

        Assert.True(result.IsSuccess);

        var counts = Counts(result.Value!, BackupImportEntities.MailboxSource);
        Assert.Equal(1, counts.Created);
        Assert.Equal(1, counts.Updated);
        Assert.Empty(counts.Conflicts);

        // Same host and username as the first, and still its own row.
        Assert.Equal(2, await db.MailboxSources.CountAsync());

        var updated = await db.MailboxSources.SingleAsync(x => x.Id == existing.Id);
        Assert.Equal("New name", updated.Name);
        Assert.Equal("enc:v1:bmV3", updated.PasswordEncrypted);

        // This install's own reading position is untouched: clearing it for a config edit would
        // re-fetch the whole mailbox.
        Assert.Equal(4711L, updated.LastProcessedUid!.Value);
    }

    /// <summary>
    /// Domain names are globally unique and every report derives its tenancy through the
    /// domain, so re-parenting one would move another client's report history with it. The
    /// import reports the clash and leaves the row alone.
    /// </summary>
    [Fact]
    public async Task DomainOwnedByAnotherClientIsReportedNotReParented()
    {
        await using var db = NewDb();

        var beta = new Client { Slug = "beta", Name = "Beta", Timezone = "UTC" };
        var shared = new Domain { ClientId = beta.Id, Name = "shared.example" };
        db.AddRange(beta, shared);
        await db.SaveChangesAsync();

        var acmeId = Guid.NewGuid();
        var artifact = Artifact(
            clients: [ExportedClient(acmeId, "acme")],
            domains: [ExportedDomain(Guid.NewGuid(), acmeId, "shared.example")]);

        var result = await Service(db).ImportAsync(artifact, BackupImportModes.Merge, false, default);

        Assert.True(result.IsSuccess);

        var counts = Counts(result.Value!, BackupImportEntities.Domain);
        Assert.Equal(1, counts.Skipped);
        Assert.Equal(0, counts.Created);
        Assert.Equal(0, counts.Updated);

        var conflict = Assert.Single(counts.Conflicts);
        Assert.Equal(BackupImportResolutions.Skipped, conflict.Resolution);
        Assert.Contains("different client", conflict.Reason, StringComparison.OrdinalIgnoreCase);

        // Left exactly as it was, under beta, and the artifact's client still imported.
        Assert.Equal(beta.Id, (await db.Domains.SingleAsync()).ClientId);
        Assert.Equal(2, await db.Clients.CountAsync());
    }

    /// <summary>
    /// A row whose parent is in neither the artifact nor the install is skipped with a reason,
    /// rather than being inserted to fail the whole import on a foreign key. A partial artifact
    /// is a plausible thing for an operator to hand-edit, and one bad grant should not cost
    /// them the other 200 rows.
    /// </summary>
    [Fact]
    public async Task RowsWithNoResolvableParentAreSkippedWithAReason()
    {
        await using var db = NewDb();

        var clientId = Guid.NewGuid();
        var artifact = Artifact(
            clients: [ExportedClient(clientId, "acme")],
            domains: [ExportedDomain(Guid.NewGuid(), Guid.NewGuid(), "orphan.example")],
            grants: [ExportedGrant(Guid.NewGuid(), Guid.NewGuid(), clientId)]);

        var result = await Service(db).ImportAsync(artifact, BackupImportModes.Merge, false, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, Counts(result.Value!, BackupImportEntities.Domain).Skipped);
        Assert.Equal(1, Counts(result.Value!, BackupImportEntities.UserClientGrant).Skipped);

        // The client it could resolve still landed.
        Assert.Equal(1, await db.Clients.CountAsync());
        Assert.Equal(0, await db.Domains.CountAsync());
        Assert.Equal(0, await db.UserClientGrants.CountAsync());
    }

    /// <summary>
    /// The import's real input is a document — an uploaded file, or an object pulled from the
    /// bucket — so the trip through <see cref="BackupJson"/> is part of the path being tested,
    /// not a detail of the caller. Worth its own test because the artifact's collection
    /// properties are read-only interfaces on a record, and "the export serializes" does not by
    /// itself mean "the importer can read it back".
    /// </summary>
    [Fact]
    public async Task ImportsAnArtifactReadBackFromItsSerializedForm()
    {
        await using var db = NewDb();

        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var artifact = Artifact(
            clients: [ExportedClient(clientId, "acme")],
            domains: [ExportedDomain(Guid.NewGuid(), clientId, "acme.example")],
            sources: [ExportedSource(Guid.NewGuid(), clientId, password: "enc:v1:cm91bmR0cmlw")],
            users: [ExportedUser(userId, "admin@acme.example")],
            grants: [ExportedGrant(Guid.NewGuid(), userId, clientId)]);

        var parsed = JsonSerializer.Deserialize<BackupArtifact>(
            BackupJson.Serialize(artifact), BackupJson.ReadOptions);

        var result = await Service(db).ImportAsync(parsed!, BackupImportModes.Restore, false, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(clientId, (await db.Clients.SingleAsync()).Id);
        Assert.Equal("acme.example", (await db.Domains.SingleAsync()).Name);

        // The one field a restore cannot recreate by hand, still intact after the round trip.
        Assert.Equal("enc:v1:cm91bmR0cmlw", (await db.MailboxSources.SingleAsync()).PasswordEncrypted);
        Assert.Equal(userId, (await db.UserClientGrants.SingleAsync()).UserId);
    }

    /// <summary>
    /// A null client is the agency-wide scope, which the <c>(clientId, email)</c> unique index
    /// treats as its own row. Conflating it with the same address scoped to one client would
    /// silently narrow who gets alerted.
    /// </summary>
    [Fact]
    public async Task AgencyWideRecipientIsADifferentRowFromTheSameAddressUnderAClient()
    {
        await using var db = NewDb();

        var client = new Client { Slug = "acme", Name = "Acme", Timezone = "UTC" };
        var agencyWide = new NotificationRecipient { ClientId = null, Email = "ops@acme.example", Kind = "alert" };
        db.AddRange(client, agencyWide);
        await db.SaveChangesAsync();

        var artifact = Artifact(recipients:
        [
            ExportedRecipient(Guid.NewGuid(), null, "ops@acme.example"),
            ExportedRecipient(Guid.NewGuid(), client.Id, "ops@acme.example"),
        ]);

        var result = await Service(db).ImportAsync(artifact, BackupImportModes.Merge, false, default);

        Assert.True(result.IsSuccess);

        var counts = Counts(result.Value!, BackupImportEntities.NotificationRecipient);
        Assert.Equal(1, counts.Updated);
        Assert.Equal(1, counts.Created);

        Assert.Equal(2, await db.NotificationRecipients.CountAsync());
        Assert.Equal("both", (await db.NotificationRecipients.SingleAsync(x => x.ClientId == null)).Kind);
        Assert.Equal(agencyWide.Id, (await db.NotificationRecipients.SingleAsync(x => x.ClientId == null)).Id);
    }
}
