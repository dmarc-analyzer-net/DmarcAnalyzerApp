namespace DmarcAnalyzer.Api.Application.Backup;

/// <summary>
/// The two jobs an import can be doing. They are not variations of one behaviour: one
/// reproduces a dead install, the other folds an artifact into a live one, and picking
/// the wrong one is the difference between a copy and a union.
/// </summary>
public enum BackupImportMode
{
    /// <summary>
    /// Disaster recovery. Allowed only into an empty install, so the Ids in the artifact
    /// land verbatim and the result is a copy rather than a merge into state nobody has
    /// looked at.
    /// </summary>
    Restore,

    /// <summary>
    /// Clone and seed. Upserts by natural key into whatever is already here, keeping the
    /// existing row's Id when one is found, and reporting the disagreements instead of
    /// resolving them quietly.
    /// </summary>
    Merge,
}

/// <summary>
/// The mode travels as a string because it is a query/body value on an admin endpoint, and
/// this is the one place that turns it back into a mode.
/// </summary>
public static class BackupImportModes
{
    public const string Restore = "restore";
    public const string Merge = "merge";

    /// <summary>
    /// Matched against the two known names and nothing else. Deliberately not
    /// <c>Enum.TryParse</c>: that accepts any numeric string, so a client sending
    /// <c>mode=0</c> — or a serializer writing the enum as a number — would silently
    /// select <see cref="BackupImportMode.Restore"/>, the destructive-looking one, without
    /// anybody having asked for it. An unrecognised value has to become a 400; there is no
    /// safe default between "reproduce a dead install" and "fold into a live one".
    /// </summary>
    public static bool TryParse(string? value, out BackupImportMode mode)
    {
        var normalized = value?.Trim();

        if (string.Equals(normalized, Restore, StringComparison.OrdinalIgnoreCase))
        {
            mode = BackupImportMode.Restore;
            return true;
        }

        if (string.Equals(normalized, Merge, StringComparison.OrdinalIgnoreCase))
        {
            mode = BackupImportMode.Merge;
            return true;
        }

        mode = default;
        return false;
    }

    public static string ToWireValue(BackupImportMode mode)
        => mode == BackupImportMode.Restore ? Restore : Merge;
}

/// <summary>
/// Table names, used as the keys of the per-entity report. The same names the manifest's
/// <c>excluded</c> map uses, so an operator reading a preview beside an artifact is
/// reading one vocabulary.
/// </summary>
public static class BackupImportEntities
{
    public const string Client = "client";
    public const string Domain = "domain";
    public const string MailboxSource = "mailbox_source";
    public const string AgencyUser = "agency_user";
    public const string UserIdentity = "user_identity";
    public const string UserClientGrant = "user_client_grant";
    public const string NotificationRecipient = "notification_recipient";
}

/// <summary>What the import did about a conflict, rather than leaving the reader to infer it.</summary>
public static class BackupImportResolutions
{
    /// <summary>
    /// The natural key matched a row whose Id differs from the artifact's. The existing Id
    /// wins — a primary key cannot be rewritten — and everything in the artifact that
    /// pointed at the old Id is repointed at it.
    /// </summary>
    public const string KeptExistingId = "kept-existing-id";

    /// <summary>
    /// The row was left alone. Always paired with a reason, because a silently dropped row
    /// in a recovery is exactly the failure this whole feature exists to avoid.
    /// </summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// One disagreement between the artifact and the install, reported rather than resolved
/// quietly.
/// </summary>
/// <param name="NaturalKey">
/// The value the row was matched on — a slug, a domain name, an email — so the operator can
/// find it without a Guid lookup. <c>id:&lt;guid&gt;</c> for <c>mailbox_source</c>, which has
/// no natural key.
/// </param>
/// <param name="ExistingId">
/// The Id already in this install. Equal to <paramref name="ArtifactId"/> when the clash is an
/// Id collision rather than a natural-key one, and <see cref="Guid.Empty"/> when the row was
/// skipped because its parent is missing — there is no existing row involved in that case.
/// </param>
public sealed record BackupImportConflict(
    string Entity,
    string NaturalKey,
    Guid ArtifactId,
    Guid ExistingId,
    string Resolution,
    string Reason);

/// <summary>
/// One table's share of the pass.
/// <para>
/// A preview and an apply produce this same record from the same computation — the preview
/// only skips the final save — so in a preview these counts read as would-create and
/// would-update. If the two ever disagreed, the preview would be a guess, which is the
/// thing that makes an operator distrust it.
/// </para>
/// </summary>
/// <param name="Skipped">
/// Rows the import declined to touch. Every one of them has an entry in
/// <paramref name="Conflicts"/> saying why.
/// </param>
public sealed record BackupImportEntityCounts(
    string Entity,
    int Created,
    int Updated,
    int Skipped,
    IReadOnlyList<BackupImportConflict> Conflicts);

/// <summary>
/// Accounts get their own report because the operator is signed in as one of them and needs
/// to know which credentials to use next.
/// </summary>
/// <param name="CreatedEmails">Accounts that did not exist here and now do.</param>
/// <param name="UpdatedEmails">
/// Accounts whose email collided. The imported row wins on all of it — hash, display name,
/// role, active flag — because signing back in with pre-disaster credentials is what makes a
/// restore faithful.
/// </param>
/// <param name="PasswordChangedEmails">
/// Whose password just changed under them. The operator-facing half of
/// <paramref name="SessionsToInvalidateUserIds"/>.
/// </param>
/// <param name="SessionsToInvalidateUserIds">
/// Exactly the users whose stored hash changed, for the caller to expire sessions on. The
/// import does not touch <c>user_session</c> itself: it is the wrong layer to be ending an
/// HTTP session, and invalidating everybody would sign out the bootstrap admin who is
/// running the import even when their own password never changed.
/// </param>
public sealed record BackupImportUserReport(
    IReadOnlyList<string> CreatedEmails,
    IReadOnlyList<string> UpdatedEmails,
    IReadOnlyList<string> PasswordChangedEmails,
    IReadOnlyList<Guid> SessionsToInvalidateUserIds);

/// <summary>
/// The outcome of a preview or an import.
/// </summary>
/// <param name="DryRun">
/// True for a preview: everything below was computed, nothing was written.
/// </param>
/// <param name="MailboxCredentialsWillNotDecrypt">
/// Set when the operator overrode a key-fingerprint mismatch. The configuration imports;
/// the mailbox passwords in it are ciphertext this install holds no key for, so every
/// affected source needs its password re-entered before it will sync. Said in the result
/// because the alternative is finding out at the next sync failure.
/// </param>
/// <param name="Warnings">
/// Things that are true and unwelcome — an unprotected artifact, credentials that will not
/// decrypt. Not errors: the import happened.
/// </param>
public sealed record BackupImportResult(
    bool DryRun,
    string Mode,
    DateTime StartedAtUtc,
    int FormatVersion,
    bool MailboxCredentialsWillNotDecrypt,
    IReadOnlyList<BackupImportEntityCounts> Entities,
    BackupImportUserReport Users,
    IReadOnlyList<string> Warnings);
