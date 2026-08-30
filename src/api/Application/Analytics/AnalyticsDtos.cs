namespace DmarcAnalyzer.Api.Application.Analytics;

/// <summary>
/// The window every analytics number is computed over. Relative windows anchor
/// to the newest report end in scope rather than to the wall clock, because
/// mailbox data is often backfilled and "last 30 days from today" would read
/// as empty on a tenant whose reports stop last month.
/// </summary>
/// <param name="AnchoredToLatestData">
/// False only when the tenant has no reports at all and the window fell back
/// to ending at the current time.
/// </param>
public sealed record AnalyticsWindowDto(
    int Days,
    DateTime BeginUtc,
    DateTime EndUtc,
    bool AnchoredToLatestData);

/// <summary>
/// Tenant-wide totals for the dashboard header. Compliant means DMARC pass —
/// at least one of DKIM/SPF passed with alignment; FailingSources counts
/// distinct source IPs whose mail passed neither.
/// </summary>
public sealed record AnalyticsTotalsDto(
    int Domains,
    int ActiveDomains,
    int Reports,
    long Messages,
    long CompliantMessages,
    double ComplianceRate,
    double DkimPassRate,
    double SpfPassRate,
    int FailingSources);

/// <summary>One day of the compliance trend chart. Date is yyyy-MM-dd.</summary>
public sealed record AnalyticsTrendPointDto(
    string Date,
    long Messages,
    long Compliant,
    long Failed);

/// <summary>A "top failing domains" dashboard row — where non-compliant volume concentrates.</summary>
public sealed record AnalyticsFailingDomainDto(
    Guid DomainId,
    string Domain,
    long Messages,
    long FailedMessages,
    double ComplianceRate);

/// <summary>A reporting organisation (Google, Microsoft, …) and how much it reported.</summary>
public sealed record AnalyticsReporterDto(
    string OrganizationName,
    int Reports,
    long Messages);

/// <summary>
/// The four RFC 9990 action dispositions. <c>Pass</c> is the one the published-policy
/// <c>DispositionType</c> does not have — "no action, passing DMARC w/enforcing policy",
/// a different statement from <c>None</c>'s "no action taken", so it must not be folded
/// into it. Reported dispositions outside these four land in no bucket at all, which is
/// why the parser repairs unrecognised values rather than storing them.
/// </summary>
public sealed record AnalyticsDispositionsDto(
    long None,
    long Pass,
    long Quarantine,
    long Reject);

/// <summary>
/// Ingestion-mailbox health for the dashboard. Null on the summary when the
/// viewer is a client_viewer — mailbox operations are agency-internal.
/// </summary>
public sealed record AnalyticsMailboxesDto(
    int Total,
    int Healthy,
    int Failing);

/// <summary>The dashboard payload: totals, trend, top lists, and mailbox health in one response.</summary>
public sealed record AnalyticsSummaryDto(
    AnalyticsWindowDto Window,
    AnalyticsTotalsDto Totals,
    IReadOnlyList<AnalyticsTrendPointDto> Trend,
    IReadOnlyList<AnalyticsFailingDomainDto> TopFailingDomains,
    IReadOnlyList<AnalyticsReporterDto> TopReporters,
    AnalyticsDispositionsDto Dispositions,
    AnalyticsMailboxesDto? Mailboxes);

/// <summary>The domain header of the drill-down page: identity plus the published policy tags.</summary>
/// <param name="PolicyInheritedFrom">
/// Set when <paramref name="PublishedPolicy"/> came from an ancestor because this domain
/// publishes no record of its own. Without it the header would claim this domain publishes
/// a policy it does not, which is the confusion the whole inheritance change exists to remove.
/// </param>
public sealed record DomainDrilldownDomainDto(
    Guid DomainId,
    string Name,
    bool IsActive,
    Guid ClientId,
    string ClientName,
    string ClientSlug,
    string? PublishedPolicy,
    string? SubdomainPolicy,
    int? PublishedPct,
    string? DkimAlignment,
    string? SpfAlignment,
    string? PolicyInheritedFrom);

/// <summary>Policy-aware enforcement status derived from published policy + compliance.</summary>
public static class EnforcementStatus
{
    /// <summary>No messages in the window — nothing can be said either way.</summary>
    public const string NoData = "no_data";

    /// <summary>p=reject — spoofed mail is being blocked.</summary>
    public const string Enforced = "enforced";

    /// <summary>p=quarantine — partial enforcement on the way to reject.</summary>
    public const string Ramping = "ramping";

    /// <summary>Unprotected (p=none or no policy) with failing volume — spoofing goes undisturbed.</summary>
    public const string Spoofing = "spoofing";

    /// <summary>p=none but compliant or low volume — observing, nothing alarming yet.</summary>
    public const string Monitoring = "monitoring";

    /// <summary>
    /// Derives the status from the *effective* published policy plus observed
    /// compliance. The policy decides between enforced/ramping; only an
    /// unenforcing policy falls through to the compliance split, where under
    /// 98% reads as spoofing.
    /// </summary>
    public static string Resolve(long messages, double complianceRate, string? publishedPolicy)
    {
        if (messages == 0)
        {
            return NoData;
        }

        return publishedPolicy switch
        {
            "reject" => Enforced,
            "quarantine" => Ramping,
            // p=none (or unknown): failing mail is not being blocked.
            _ => complianceRate < 0.98 ? Spoofing : Monitoring,
        };
    }
}

/// <summary>
/// One domain's totals over the window. Status is the compliance-only rating
/// (aligned / issues / failing / no_data), distinct from the policy-aware
/// <see cref="EnforcementStatus"/>.
/// </summary>
public sealed record DomainDrilldownTotalsDto(
    long Messages,
    long CompliantMessages,
    double ComplianceRate,
    double DkimPassRate,
    double SpfPassRate,
    int Reports,
    int Sources,
    int Reporters,
    long Quarantined,
    long Rejected,
    string Status);

/// <summary>The domain drill-down page: header, window, totals, and daily trend.</summary>
public sealed record DomainDrilldownDto(
    DomainDrilldownDomainDto Domain,
    AnalyticsWindowDto Window,
    DomainDrilldownTotalsDto Totals,
    IReadOnlyList<AnalyticsTrendPointDto> Trend);

/// <summary>
/// One sending source (IP) of a domain: volume, pass rates, and dispositions
/// over the window. HeaderFroms counts distinct From-header domains this IP
/// sent as — a high count on a failing source is a spoofing tell.
/// </summary>
public sealed record DomainSourceDto(
    string SourceIp,
    long Messages,
    long CompliantMessages,
    long FailedMessages,
    double ComplianceRate,
    double DkimPassRate,
    double SpfPassRate,
    long Quarantined,
    long Rejected,
    int Reporters,
    int HeaderFroms,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc);

/// <summary>Message volume per evaluated (DKIM, SPF) result pair for one source.</summary>
public sealed record SourceEvaluatedComboDto(string Dkim, string Spf, long Messages);

/// <summary>A distinct value (header-from, envelope-from, …) and its message volume.</summary>
public sealed record SourceValueCountDto(string Value, long Messages);

/// <summary>A raw DKIM auth result (signing domain + selector) observed for one source.</summary>
public sealed record SourceDkimAuthDto(string Domain, string Selector, string Result, long Messages);

/// <summary>A raw SPF auth result (checked domain + helo/mfrom scope) observed for one source.</summary>
public sealed record SourceSpfAuthDto(string Domain, string Scope, string Result, long Messages);

/// <summary>Which reporting organisations observed this source, and how much of it.</summary>
public sealed record SourceReporterDto(string OrganizationName, int Reports, long Messages);

/// <summary>
/// The source drill-down: everything the reports say about one IP sending for
/// one domain — result combos, identities used, raw auth results, reporters,
/// and the daily trend.
/// </summary>
public sealed record SourceDetailDto(
    string SourceIp,
    long Messages,
    long CompliantMessages,
    double ComplianceRate,
    AnalyticsDispositionsDto Dispositions,
    IReadOnlyList<SourceEvaluatedComboDto> Evaluated,
    IReadOnlyList<SourceValueCountDto> HeaderFroms,
    IReadOnlyList<SourceValueCountDto> EnvelopeFroms,
    IReadOnlyList<SourceDkimAuthDto> DkimAuth,
    IReadOnlyList<SourceSpfAuthDto> SpfAuth,
    IReadOnlyList<SourceReporterDto> Reporters,
    IReadOnlyList<AnalyticsTrendPointDto> Trend);

/// <summary>One row of the Domains table: compliance over the window plus the cached DNS policy.</summary>
/// <param name="DnsLookupStatus">found / inherited / missing / lookup_failed, or null if never checked.</param>
/// <param name="DnsPolicyInheritedFrom">
/// The ancestor the policy came from when <paramref name="DnsLookupStatus"/> is inherited — this
/// domain publishes no record of its own and receivers apply the organisational domain's.
/// Null otherwise.
/// </param>
/// <param name="DnsCheckedAtUtc">When the cached policy above was last refreshed from DNS.</param>
public sealed record DomainAnalyticsDto(
    Guid DomainId,
    string Name,
    bool IsActive,
    Guid ClientId,
    string ClientName,
    string ClientSlug,
    long Messages,
    long CompliantMessages,
    double ComplianceRate,
    double DkimPassRate,
    double SpfPassRate,
    int Reports,
    int Sources,
    int Reporters,
    long Quarantined,
    long Rejected,
    DateTime? LastReportEndUtc,
    string Status,
    string? PublishedPolicy,
    string? SubdomainPolicy,
    int? PublishedPct,
    string? DkimAlignment,
    string? SpfAlignment,
    string? DnsLookupStatus,
    string? DnsPolicyInheritedFrom,
    DateTime? DnsCheckedAtUtc,
    string EnforcementStatus);

/// <summary>A sending source still emitting unaligned mail — what blocks tightening the policy.</summary>
public sealed record EnforcementBlockingSourceDto(
    string SourceIp,
    long Messages,
    long FailedMessages,
    double ComplianceRate,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc);

/// <summary>Guided path-to-enforcement recommendation for a single domain.</summary>
public sealed record EnforcementGuidanceDto(
    Guid DomainId,
    string Name,
    AnalyticsWindowDto Window,
    string? CurrentPolicy,
    int? CurrentPct,
    string EnforcementStatus,
    long Messages,
    long CompliantMessages,
    double ComplianceRate,
    long FailedMessages,
    int BlockingSourceCount,
    string RecommendedPolicy,
    string RecommendedAction,
    string Rationale,
    bool ReadyToAdvance,
    IReadOnlyList<EnforcementBlockingSourceDto> BlockingSources);

/// <summary>One unauthenticated/failing sending source for a domain — a spoofing candidate.</summary>
public sealed record ThreatSourceDto(
    string SourceIp,
    Guid DomainId,
    string Domain,
    Guid ClientId,
    string ClientName,
    long Messages,
    long FailedMessages,
    double ComplianceRate,
    string? PublishedPolicy,
    long Quarantined,
    long Rejected,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc);

/// <summary>The threat feed: failing sources across all domains, worst first.</summary>
public sealed record ThreatFeedDto(
    AnalyticsWindowDto Window,
    long TotalFailedMessages,
    int TotalSources,
    IReadOnlyList<ThreatSourceDto> Sources);
