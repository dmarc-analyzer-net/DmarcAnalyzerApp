namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// The current MTA-STS posture of a domain, as last observed by the worker's
/// check pass (or an on-demand recheck): the <c>_mta-sts</c> TXT record, the
/// fetched policy file, and how the policy's mx patterns line up with live MX.
///
/// One row per domain, current state only — no history. Policy-id changes are
/// tracked with a single previous value, which is what the alert evaluator
/// needs; anything longer-lived belongs in alert_event.
///
/// Like the domain row's Dns* columns, a failed lookup keeps the last known
/// values rather than blanking them: a transient SERVFAIL must not make an
/// enforce-mode domain read as unprotected.
/// </summary>
public sealed class MtaStsState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }

    /// <summary>
    /// Outcome of the `_mta-sts.{domain}` TXT lookup: found, missing,
    /// lookup_failed, or invalid (two or more STSv1 records, or a record whose
    /// syntax senders reject — both mean "no available policy" per RFC 8461).
    /// </summary>
    public string DnsRecordStatus { get; set; } = string.Empty;

    /// <summary>The STSv1 TXT record as published, when one was found.</summary>
    public string? RawRecord { get; set; }

    /// <summary>Current id= from the TXT record. Senders refetch the policy when it changes.</summary>
    public string? PolicyId { get; set; }

    /// <summary>The id before the last observed change; null until a change has been seen.</summary>
    public string? PreviousPolicyId { get; set; }

    public DateTime? PolicyIdChangedAtUtc { get; set; }

    /// <summary>
    /// Outcome of fetching https://mta-sts.{domain}/.well-known/mta-sts.txt:
    /// ok, redirected, http_error, tls_failed, connect_failed, timeout or
    /// too_large. Null until the TXT record has been seen once (no record, no fetch).
    /// </summary>
    public string? FetchStatus { get; set; }

    /// <summary>Human-readable reason when the fetch was not ok (HTTP status, cert failure, …).</summary>
    public string? FetchDetail { get; set; }

    /// <summary>
    /// When the policy file was last fetched successfully. Never cleared — this is
    /// how "broken now" is told apart from "never reachable yet", which matters
    /// once hosted policies exist and a domain is mid-setup.
    /// </summary>
    public DateTime? LastFetchOkAtUtc { get; set; }

    /// <summary>Whether the last fetched body parsed as a valid policy; null when never fetched.</summary>
    public bool? PolicyValid { get; set; }

    /// <summary>enforce, testing or none — from the last successfully fetched policy.</summary>
    public string? Mode { get; set; }

    public long? MaxAgeSeconds { get; set; }

    /// <summary>The last successfully fetched policy body (capped at 64 KB by the fetcher).</summary>
    public string? PolicyBody { get; set; }

    /// <summary>Outcome of the live MX lookup: found, missing or lookup_failed.</summary>
    public string? MxLookupStatus { get; set; }

    /// <summary>JSON `[{"host","preference","matched"}]` — live MX at check time, each tested against the policy's mx patterns.</summary>
    public string? MxHostsJson { get; set; }

    /// <summary>JSON string array of live MX hosts no mx pattern covers; "[]" when all are covered; null when not evaluable.</summary>
    public string? UnmatchedMxHostsJson { get; set; }

    /// <summary>JSON string array of findings from the last check, ready to render.</summary>
    public string? IssuesJson { get; set; }

    /// <summary>Always advanced by a check, even when nothing moved — "we verified this" is what it is for.</summary>
    public DateTime LastCheckedAtUtc { get; set; }

    /// <summary>When any material field last changed; drives "last verified" copy.</summary>
    public DateTime? LastChangedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Domain? Domain { get; set; }
}
