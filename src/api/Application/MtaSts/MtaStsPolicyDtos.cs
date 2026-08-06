namespace DmarcAnalyzer.Api.Application.MtaSts;

/// <summary>
/// A hosted policy plus everything the operator must publish: the TXT record
/// (with the current id) and the policy URL. The CNAME half lives on the
/// wrapper, because it applies whether or not a policy exists yet.
/// </summary>
public sealed record MtaStsPolicyDto(
    Guid Id,
    Guid DomainId,
    bool Enabled,
    string Mode,
    int MaxAgeSeconds,
    IReadOnlyList<string> MxPatterns,
    string PolicyId,
    string TxtRecordName,
    string TxtRecordValue,
    string PolicyUrl,
    DateTime ModeChangedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// One response for the whole card: Policy is null when the domain hosts no
/// policy here (a 200 — the domain exists; hosting is simply not set up).
/// CnameTarget is MtaSts:PolicyHost, null when unconfigured so the console can
/// hint at setting it instead of rendering an empty target.
/// </summary>
public sealed record MtaStsPolicyResponse(
    Guid DomainId,
    string DomainName,
    Guid ClientId,
    MtaStsPolicyDto? Policy,
    string CnameRecordName,
    string? CnameTarget);

/// <summary>What an upsert did, for auditing and for the bulk response.</summary>
public static class MtaStsPolicyOutcome
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Unchanged = "unchanged";
}

public sealed record MtaStsPolicyUpsertResult(
    MtaStsPolicyResponse Response,
    string Outcome,
    string? PreviousPolicyId);

/// <summary>
/// Per-domain bulk outcome. Carries the TXT value because every domain whose
/// content changed has its own new id — and therefore its own record to update.
/// </summary>
public sealed record MtaStsPolicyApplyOutcomeDto(
    Guid DomainId,
    string DomainName,
    string Outcome,
    string PolicyId,
    string TxtRecordName,
    string TxtRecordValue);

public sealed record MtaStsPolicyBulkApplyResponse(
    IReadOnlyList<MtaStsPolicyApplyOutcomeDto> Results);
