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

public sealed record DomainAnalyticsDto(
    Guid DomainId,
    string Name,
    bool IsActive,
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
