using DmarcAnalyzer.Api.Application.Analytics;

namespace DmarcAnalyzer.Api.Application.MtaSts;

/// <summary>
/// Outcome of the `_mta-sts.{domain}` TXT lookup. Reuses the record-inspection
/// vocabulary where it fits, plus one MTA-STS-specific state: RFC 8461 §3.1 says
/// two or more STSv1 records, or a record senders cannot parse, mean the domain
/// has no available policy — which must not read as "found". There is no
/// `inherited`: MTA-STS has no tree walk.
/// </summary>
public static class MtaStsRecordStatus
{
    public const string Found = RecordLookupStatus.Found;
    public const string Missing = RecordLookupStatus.Missing;
    public const string LookupFailed = RecordLookupStatus.LookupFailed;
    public const string Invalid = "invalid";
}

/// <summary>Outcome of fetching the policy file over HTTPS.</summary>
public static class MtaStsFetchStatus
{
    public const string Ok = "ok";

    /// <summary>RFC 8461 §3.3: senders must not follow redirects, so a 3xx is a broken policy host.</summary>
    public const string Redirected = "redirected";

    public const string HttpError = "http_error";
    public const string TlsFailed = "tls_failed";
    public const string ConnectFailed = "connect_failed";
    public const string Timeout = "timeout";
    public const string TooLarge = "too_large";
}

/// <summary>MX lookup outcome for the cross-check: found, missing or lookup_failed.</summary>
public static class MtaStsMxStatus
{
    public const string Found = RecordLookupStatus.Found;
    public const string Missing = RecordLookupStatus.Missing;
    public const string LookupFailed = RecordLookupStatus.LookupFailed;
}

/// <summary>The `_mta-sts` TXT record, parsed. Raw is the STSv1 record when one was seen, even if invalid.</summary>
public sealed record MtaStsRecordParseResult(
    string Status,
    string? Raw,
    string? Id,
    IReadOnlyList<string> Issues);

/// <summary>
/// A parsed policy file. Valid means senders would accept it; the individual
/// fields carry whatever could be read either way, so a broken policy still
/// renders legibly.
/// </summary>
public sealed record MtaStsPolicyParseResult(
    bool Valid,
    string? Mode,
    long? MaxAgeSeconds,
    IReadOnlyList<string> MxPatterns,
    IReadOnlyList<string> Issues);

/// <summary>What came back from https://mta-sts.{domain}/.well-known/mta-sts.txt.</summary>
public sealed record MtaStsPolicyFetchResult(
    string Status,
    string? Body,
    int? HttpStatusCode,
    string? Detail,
    string? ContentType);

/// <summary>
/// One full check of a domain: TXT record, policy fetch + parse, MX cross-check.
/// Stateless — persistence and change detection live in the state cache.
/// Fetch/Policy/Mx fields are null when the TXT record was not found, because
/// nothing further is checked in that case.
/// </summary>
public sealed record MtaStsCheckResult(
    MtaStsRecordParseResult Record,
    MtaStsPolicyFetchResult? Fetch,
    MtaStsPolicyParseResult? Policy,
    string? MxLookupStatus,
    IReadOnlyList<MxHost>? MxHosts,
    IReadOnlyList<string>? UnmatchedMxHosts,
    IReadOnlyList<string> Issues);

/// <summary>
/// A live MX host and whether any policy mx pattern covers it. Matched is null
/// when the cross-check was not evaluable (invalid policy, mode none, null MX).
/// </summary>
public sealed record MtaStsMxHostDto(string Host, int Preference, bool? Matched);

/// <summary>
/// The persisted MTA-STS state of a domain, as the console renders it. Checked
/// is false (and every nullable field null) for a domain the pass has not
/// reached yet — distinct from missing, which is a definitive answer.
/// </summary>
public sealed record MtaStsStateDto(
    Guid DomainId,
    string Name,
    bool Checked,
    string? DnsRecordStatus,
    string? RawRecord,
    string? PolicyId,
    string? PreviousPolicyId,
    DateTime? PolicyIdChangedAtUtc,
    string? FetchStatus,
    string? FetchDetail,
    DateTime? LastFetchOkAtUtc,
    bool? PolicyValid,
    string? Mode,
    long? MaxAgeSeconds,
    string? PolicyBody,
    IReadOnlyList<string> MxPatterns,
    string? MxLookupStatus,
    IReadOnlyList<MtaStsMxHostDto> MxHosts,
    IReadOnlyList<string> Issues,
    DateTime? LastCheckedAtUtc,
    DateTime? LastChangedAtUtc);
