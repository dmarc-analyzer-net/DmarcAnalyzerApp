namespace DmarcAnalyzer.Api.Application.Analytics;

public sealed record AnalyticsWindowDto(
    int Days,
    DateTime BeginUtc,
    DateTime EndUtc,
    bool AnchoredToLatestData);

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

public sealed record AnalyticsTrendPointDto(
    string Date,
    long Messages,
    long Compliant,
    long Failed);

public sealed record AnalyticsFailingDomainDto(
    Guid DomainId,
    string Domain,
    long Messages,
    long FailedMessages,
    double ComplianceRate);

public sealed record AnalyticsReporterDto(
    string OrganizationName,
    int Reports,
    long Messages);

public sealed record AnalyticsDispositionsDto(
    long None,
    long Quarantine,
    long Reject);

public sealed record AnalyticsMailboxesDto(
    int Total,
    int Healthy,
    int Failing);

public sealed record AnalyticsSummaryDto(
    AnalyticsWindowDto Window,
    AnalyticsTotalsDto Totals,
    IReadOnlyList<AnalyticsTrendPointDto> Trend,
    IReadOnlyList<AnalyticsFailingDomainDto> TopFailingDomains,
    IReadOnlyList<AnalyticsReporterDto> TopReporters,
    AnalyticsDispositionsDto Dispositions,
    AnalyticsMailboxesDto? Mailboxes);

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
    string? SpfAlignment);

/// <summary>Policy-aware enforcement status derived from published policy + compliance.</summary>
public static class EnforcementStatus
{
    public const string NoData = "no_data";
    public const string Enforced = "enforced";   // p=reject
    public const string Ramping = "ramping";     // p=quarantine
    public const string Spoofing = "spoofing";   // unprotected (p=none) with failing volume
    public const string Monitoring = "monitoring"; // p=none but compliant / low volume

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

public sealed record DomainDrilldownDto(
    DomainDrilldownDomainDto Domain,
    AnalyticsWindowDto Window,
    DomainDrilldownTotalsDto Totals,
    IReadOnlyList<AnalyticsTrendPointDto> Trend);

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

public sealed record SourceEvaluatedComboDto(string Dkim, string Spf, long Messages);

public sealed record SourceValueCountDto(string Value, long Messages);

public sealed record SourceDkimAuthDto(string Domain, string Selector, string Result, long Messages);

public sealed record SourceSpfAuthDto(string Domain, string Scope, string Result, long Messages);

public sealed record SourceReporterDto(string OrganizationName, int Reports, long Messages);

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

public sealed record ThreatFeedDto(
    AnalyticsWindowDto Window,
    long TotalFailedMessages,
    int TotalSources,
    IReadOnlyList<ThreatSourceDto> Sources);
