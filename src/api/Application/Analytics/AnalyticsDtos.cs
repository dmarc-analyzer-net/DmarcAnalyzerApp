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
    AnalyticsMailboxesDto Mailboxes);

public sealed record DomainDrilldownDomainDto(
    Guid DomainId,
    string Name,
    bool IsActive,
    Guid ClientId,
    string ClientName,
    string ClientSlug);

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
    string Status);
