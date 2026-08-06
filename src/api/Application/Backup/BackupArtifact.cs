namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// The configuration export. One JSON document holding everything a fresh install
/// needs to become this one, minus the report data — which arrived over IMAP and can
/// arrive again, and which outweighs the rest by roughly four orders of magnitude.
/// <para>
/// The shape is a published format, not an internal DTO: an artifact written by one
/// version is read by another, possibly years later, so property names are pinned by
/// <see cref="BackupJson"/> and asserted by <c>BackupArtifactFormatTests</c>. Renaming
/// a property here is a breaking change to every stored artifact.
/// </para>
/// </summary>
public sealed record BackupArtifact(
    BackupManifest Manifest,
    IReadOnlyList<BackupClient> Clients,
    IReadOnlyList<BackupDomain> Domains,
    IReadOnlyList<BackupMailboxSource> MailboxSources,
    IReadOnlyList<BackupNotificationRecipient> NotificationRecipients,
    IReadOnlyList<BackupUser> Users,
    IReadOnlyList<BackupUserIdentity> UserIdentities,
    IReadOnlyList<BackupUserClientGrant> Grants,
    // Null-tolerant on read: artifacts written before hosted MTA-STS existed
    // lack the property, and FormatVersion is bumped only for changes an older
    // reader could not handle — an ignorable extra list is not one.
    IReadOnlyList<BackupMtaStsPolicy>? MtaStsPolicies = null);

/// <summary>
/// What this artifact is, and — as importantly — what it is not.
/// </summary>
/// <param name="FormatVersion">
/// Bumped only for a change an older reader could not handle. An importer refuses a
/// version it does not know rather than guessing at the difference.
/// </param>
/// <param name="AppVersion">
/// The API assembly's informational version. Advisory only: this build does not stamp
/// one, so it reads <c>1.0.0</c> until the release pipeline sets it. Use
/// <paramref name="MigrationId"/> for anything that must be exact.
/// </param>
/// <param name="MigrationId">
/// The newest applied migration — the artifact's real schema identity, and the thing
/// to compare when deciding whether a target install can take it.
/// </param>
/// <param name="EncryptionKeyFingerprint">
/// Identifies the key that encrypted the mailbox credentials in this file without
/// being able to decrypt them, so "do I hold the right key?" is answerable *before*
/// the import rather than at the next failed mailbox sync. Null when no key is set.
/// </param>
/// <param name="CredentialsProtected">
/// False means the install stores mailbox passwords in plaintext, so this artifact
/// contains plaintext passwords. Export refuses to produce that without an explicit
/// override.
/// </param>
/// <param name="Excluded">
/// Row counts for the tables deliberately left out, by table name. A file that
/// silently omits five million rows is a trap; one that says so is a backup with a
/// stated scope.
/// </param>
/// <param name="LegalHoldClients">
/// Slugs of clients whose data is under legal hold. For these, "we can re-ingest it
/// from the mailbox" is not a defensible answer, so their presence means this artifact
/// alone is not sufficient coverage.
/// </param>
public sealed record BackupManifest(
    int FormatVersion,
    DateTime ExportedAtUtc,
    string AppVersion,
    string? MigrationId,
    int MigrationCount,
    string? EncryptionKeyFingerprint,
    bool CredentialsProtected,
    BackupScope Scope,
    IReadOnlyDictionary<string, long> Excluded,
    IReadOnlyList<string> LegalHoldClients);

/// <param name="Config">Whether the configuration tables are present.</param>
/// <param name="History">
/// How the append-only history tables are covered: <c>none</c>, or
/// <c>shipped-separately</c> when the offload streams them as their own objects.
/// </param>
/// <param name="Reports">Report-data coverage: <c>none</c> or <c>legal-hold-only</c>.</param>
public sealed record BackupScope(bool Config, string History, string Reports);

public sealed record BackupClient(
    Guid Id,
    string Name,
    string Slug,
    bool IsActive,
    int RetentionMonths,
    bool LegalHold,
    bool AlertsEnabled,
    int? AlertComplianceDropPercent,
    int? AlertMinMessages,
    string Timezone,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// The DNS policy cache (<c>DnsPolicy</c>, <c>DnsLookupStatus</c>, <c>DnsCheckedAtUtc</c>)
/// is deliberately absent — the worker refreshes it from DNS within hours of a restore,
/// and carrying a stale copy would show a policy the domain may no longer publish.
/// </summary>
public sealed record BackupDomain(
    Guid Id,
    Guid ClientId,
    string Name,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// Carries <c>PasswordEncrypted</c> verbatim — the <c>enc:v1:</c> ciphertext, which is
/// useless without the key named by the manifest fingerprint. The IMAP checkpoint
/// (<c>LastProcessedUid</c>, <c>LastProcessedUidValidity</c>) is deliberately absent so a
/// restored source rescans from the beginning: a checkpoint from another install's view
/// of the mailbox would skip mail, and UIDVALIDITY makes a stale value actively
/// misleading.
/// </summary>
public sealed record BackupMailboxSource(
    Guid Id,
    string Name,
    string Protocol,
    string Host,
    int Port,
    bool UseTls,
    string Username,
    string PasswordEncrypted,
    Guid DefaultClientId,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <param name="ClientId">Null is the agency-wide scope — every client.</param>
public sealed record BackupNotificationRecipient(
    Guid Id,
    Guid? ClientId,
    string Email,
    string Kind,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// Carries <c>PasswordHash</c> verbatim, which is what makes a restore faithful: you
/// sign back in with the credentials you had before the disaster. It is also what makes
/// this file a credential file — see the security notes on the export endpoint.
/// <c>LastLoginAtUtc</c> is left out as derived state.
/// </summary>
public sealed record BackupUser(
    Guid Id,
    string Email,
    string PasswordHash,
    string DisplayName,
    string Role,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record BackupUserIdentity(
    Guid Id,
    Guid UserId,
    string Issuer,
    string Subject,
    string? EmailAtLink,
    DateTime CreatedAtUtc);

public sealed record BackupUserClientGrant(
    Guid Id,
    Guid UserId,
    Guid ClientId,
    DateTime CreatedAtUtc,
    Guid? CreatedByUserId);

/// <summary>
/// A hosted MTA-STS policy — DNS-load-bearing configuration, which is exactly
/// what the artifact exists to preserve. <c>PolicyId</c> travels verbatim so a
/// restore serves the same id and forces no TXT record update on any client
/// domain. The monitoring state (<c>mta_sts_state</c>) is deliberately absent:
/// it is a cache the check pass rebuilds within one interval.
/// </summary>
public sealed record BackupMtaStsPolicy(
    Guid Id,
    Guid DomainId,
    bool Enabled,
    string Mode,
    int MaxAgeSeconds,
    string MxPatterns,
    string PolicyId,
    DateTime ModeChangedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);
