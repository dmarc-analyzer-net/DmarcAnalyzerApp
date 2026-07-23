namespace DmarcAnalyzer.Api.Application.Analytics;

/// <summary>Outcome of a live DNS check: found, missing, or the lookup itself failed.</summary>
public static class RecordLookupStatus
{
    public const string Found = "found";
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
    IReadOnlyList<string> Issues);

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
    string SubdomainPolicy,
    int Pct,
    string DkimAlignment,
    string SpfAlignment,
    DateTime AsOfUtc,
    string ReportedBy);

/// <summary>One published-vs-observed field comparison.</summary>
public sealed record RecordComparisonDto(string Field, string? Published, string? Observed, bool Match);

public sealed record RecordInspectionDto(
    Guid DomainId,
    string Name,
    DnsDmarcRecordDto Dmarc,
    DnsSpfRecordDto Spf,
    ObservedPolicyDto? Observed,
    IReadOnlyList<RecordComparisonDto> Comparison);
