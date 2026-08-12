namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// One issued machine credential. See ADR 0010.
/// <para>
/// A row rather than a column on <see cref="ReportSource"/>, because rotation needs two
/// valid credentials at the same time: a single column forces a flag-day cutover on an
/// unattended pipeline, which is how operators end up never rotating at all.
/// </para>
/// </summary>
public sealed class ApiCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Operator-chosen, so a list of credentials is readable rather than a list of ids.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// What this credential may do — one of <see cref="MachineCredentialKinds"/>. Endpoints
    /// name the kind they require, so a credential authorises an allowlist of endpoints
    /// rather than carrying a role.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// The source this credential ingests for. Set for <c>report_ingest</c>; null for kinds
    /// that are not scoped to one source. It is what makes the credential decide the client
    /// rather than the request body.
    /// </summary>
    public Guid? ReportSourceId { get; set; }

    /// <summary>
    /// The non-secret half of the issued token, used to find the row. Unique and indexed, so
    /// verification is one lookup rather than a scan that hashes every candidate.
    /// </summary>
    public string TokenId { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the secret half, hex encoded. Deliberately not the PBKDF2 used for user
    /// passwords: that is slow on purpose because a password is low-entropy and worth
    /// brute-forcing offline, and neither is true of 256 bits from a CSPRNG.
    /// </summary>
    public string TokenHash { get; set; } = string.Empty;

    public Guid? CreatedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Refreshed when the credential authenticates, throttled so a busy pipeline does not
    /// turn every request into a write. Evidence that a credential is live — or abandoned
    /// and safe to revoke.
    /// </summary>
    public DateTime? LastUsedAtUtc { get; set; }

    /// <summary>Null means no expiry, which is the default. See ADR 0010 for why.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>
    /// Revocation is a timestamp rather than a delete, because "revoked at 09:14" answers an
    /// incident question that a missing row cannot.
    /// </summary>
    public DateTime? RevokedAtUtc { get; set; }

    public ReportSource? ReportSource { get; set; }

    public bool IsUsable(DateTime nowUtc)
        => RevokedAtUtc is null && (ExpiresAtUtc is null || ExpiresAtUtc > nowUtc);
}

/// <summary>
/// A closed set in code rather than a scope grammar, per ADR 0010. Adding a kind means
/// adding the endpoints that accept it, in the same change.
/// </summary>
public static class MachineCredentialKinds
{
    public const string ReportIngest = "report_ingest";

    public static bool IsKnown(string kind) => kind == ReportIngest;
}
