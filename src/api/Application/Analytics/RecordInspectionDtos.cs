namespace DmarcAnalyzer.Api.Application.Analytics;

/// <summary>Outcome of a live DNS check: found, missing, or the lookup itself failed.</summary>
public static class RecordLookupStatus
{
    public const string Found = "found";

    /// <summary>
    /// No record here, but an ancestor publishes one, so a receiver applies that. Distinct
    /// from Found because the policy is not this domain's to change, and from Missing because
    /// the domain is not unprotected.
    /// </summary>
    public const string Inherited = "inherited";

    public const string Missing = "missing";
    public const string LookupFailed = "lookup_failed";
}

/// <summary>The live DMARC record at _dmarc.{domain}, parsed tag by tag.</summary>
public sealed record DnsDmarcRecordDto(
    string Status,
    string? Raw,
    string? Policy,
    string? SubdomainPolicy,
    int? Pct,
    string? Rua,
    string? Ruf,
    string? DkimAlignment,
    string? SpfAlignment,
    IReadOnlyList<string> Issues,
    /// <summary>Testing mode (RFC 9989) — y/n, null when not published. Default is n.</summary>
    string? Testing = null,
    /// <summary>Public suffix domain flag (RFC 9989) — y/n/u, null when not published. Default is u.</summary>
    string? PublicSuffixDomain = null,
    /// <summary>Policy for non-existent subdomains (RFC 9989, promoted from experimental RFC 9091).</summary>
    string? NonExistentSubdomainPolicy = null);

/// <summary>
/// The live SPF record(s) at {domain}. LookupMechanisms counts top-level
/// mechanisms that cost a DNS lookup (include/a/mx/ptr/exists/redirect) —
/// RFC 7208 caps the total at 10.
/// </summary>
public sealed record DnsSpfRecordDto(
    string Status,
    string? Raw,
    int RecordCount,
    int LookupMechanisms,
    string? AllQualifier,
    IReadOnlyList<string> Issues);

/// <summary>The DMARC policy reporters most recently observed (policy_published).</summary>
public sealed record ObservedPolicyDto(
    string Policy,
    string? SubdomainPolicy,
    int Pct,
    string DkimAlignment,
    string SpfAlignment,
    DateTime AsOfUtc,
    string ReportedBy);

/// <summary>How a published tag lines up with what the reporter echoed back.</summary>
public static class RecordComparisonStatus
{
    public const string Match = "match";
    public const string Differs = "differs";

    /// <summary>Not published, so RFC 7489 derives it — nothing to disagree with.</summary>
    public const string Inherited = "inherited";

    /// <summary>Published, but the reporter sent no value for it.</summary>
    public const string NotReported = "not_reported";
}

/// <summary>
/// One published-vs-observed field comparison. Only <see cref="RecordComparisonStatus.Differs"/>
/// is a finding; the other three states are informational.
/// </summary>
public sealed record RecordComparisonDto(
    string Field,
    string? Published,
    string? Observed,
    string Status,
    string? Note = null);

/// <summary>Whether a rua/ruf destination outside this domain has authorized receiving its reports.</summary>
public static class ExternalDestinationAuthStatus
{
    public const string Authorized = "authorized";
    public const string NotAuthorized = "not_authorized";
    public const string LookupFailed = "lookup_failed";
}

/// <summary>
/// A rua/ruf address at a domain other than the one publishing the DMARC record only
/// works if that destination opts in, by publishing a DMARC record at
/// {domain}._report._dmarc.{destination} (RFC 9990 §4). Without it, conforming
/// receivers silently drop the reports — nothing bounces to say why.
/// </summary>
public sealed record ExternalDestinationAuthDto(
    string Destination,
    string Status,
    string Detail);

public sealed record RecordInspectionDto(
    Guid DomainId,
    string Name,
    DnsDmarcRecordDto Dmarc,
    DnsSpfRecordDto Spf,
    ObservedPolicyDto? Observed,
    IReadOnlyList<RecordComparisonDto> Comparison,
    IReadOnlyList<ExternalDestinationAuthDto> ExternalDestinations);
