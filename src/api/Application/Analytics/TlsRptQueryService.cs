using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Application.Reports;
using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Analytics;

public interface ITlsRptQueryService
{
    /// <summary>Per-domain TLS-RPT summary. Null for unknown or cross-tenant ids (→ 404).</summary>
    Task<TlsRptDomainSummaryDto?> GetDomainSummaryAsync(Guid domainId, int days, CancellationToken ct);

    /// <summary>The gate's evidence since <paramref name="sinceUtc"/> — wall-clock, not data-anchored.</summary>
    Task<TlsRptGateSample> GetGateSampleAsync(Guid domainId, DateTime sinceUtc, CancellationToken ct);
}

/// <summary>
/// TLS-RPT analytics — its own service rather than more weight on
/// AnalyticsQueryService (which carries ~40 DmarcReport references), and its
/// own window anchor: TLS windows anchor to the newest **TLS** data the caller
/// can see, because anchoring to DMARC's anchor would blank this panel
/// whenever TLS reporting lags DMARC (it usually does — far fewer reporters).
/// </summary>
public sealed class TlsRptQueryService(
    DmarcAnalyzerDbContext db,
    ICurrentUserContext currentUser) : ITlsRptQueryService
{
    public async Task<TlsRptDomainSummaryDto?> GetDomainSummaryAsync(
        Guid domainId, int days, CancellationToken ct)
    {
        var domain = await db.Domains
            .AsNoTracking()
            .Where(x => x.Id == domainId)
            .Select(x => new { x.Id, x.ClientId })
            .SingleOrDefaultAsync(ct);

        // Cross-tenant ids read as not-found to avoid an existence oracle.
        if (domain is null || !currentUser.CanAccessClient(domain.ClientId))
        {
            return null;
        }

        days = ClampDays(days);
        var window = await ResolveWindowAsync(days, ct);

        var policies = await db.SmtpTlsReportPolicies
            .AsNoTracking()
            .Where(p => p.DomainId == domainId
                && p.ReportRangeBeginUtc >= window.BeginUtc
                && p.ReportRangeBeginUtc <= window.EndUtc)
            .Select(p => new
            {
                p.Id,
                p.SmtpTlsReportId,
                p.PolicyType,
                p.SuccessfulSessionCount,
                p.FailureSessionCount,
                Reporter = p.Report!.OrganizationName,
            })
            .ToListAsync(ct);

        var policyIds = policies.Select(p => p.Id).ToList();
        var details = await db.SmtpTlsFailureDetails
            .AsNoTracking()
            .Where(d => policyIds.Contains(d.SmtpTlsReportPolicyId))
            .Select(d => new { d.ResultType, d.FailureCategory, d.ReceivingMxHostname, d.FailedSessionCount })
            .ToListAsync(ct);

        var successful = policies.Sum(p => p.SuccessfulSessionCount);
        var failed = policies.Sum(p => p.FailureSessionCount);

        return new TlsRptDomainSummaryDto(
            window,
            successful + failed,
            successful,
            failed,
            Rate(successful, successful + failed),
            policies.Select(p => p.SmtpTlsReportId).Distinct().Count(),
            policies.Select(p => p.Reporter).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            policies
                .GroupBy(p => p.PolicyType, StringComparer.Ordinal)
                .Select(g => new TlsRptPolicyTypeStatDto(
                    g.Key, g.Sum(x => x.SuccessfulSessionCount), g.Sum(x => x.FailureSessionCount)))
                .OrderByDescending(x => x.SuccessfulSessions + x.FailedSessions)
                .ToList(),
            details
                .GroupBy(d => d.FailureCategory, StringComparer.Ordinal)
                .Select(g => new TlsRptCategoryStatDto(g.Key, g.Sum(x => x.FailedSessionCount)))
                .OrderByDescending(x => x.FailedSessions)
                .ToList(),
            details
                .GroupBy(d => new { d.ResultType, d.FailureCategory })
                .Select(g => new TlsRptFailureTypeStatDto(
                    g.Key.ResultType, g.Key.FailureCategory, g.Sum(x => x.FailedSessionCount)))
                .OrderByDescending(x => x.FailedSessions)
                .ToList(),
            details
                .GroupBy(d => d.ReceivingMxHostname ?? "unknown", StringComparer.OrdinalIgnoreCase)
                .Select(g => new TlsRptMxHostStatDto(
                    g.Key,
                    g.Sum(x => x.FailedSessionCount),
                    g.Select(x => x.ResultType).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList()))
                .OrderByDescending(x => x.FailedSessions)
                .Take(10)
                .ToList());
    }

    public async Task<TlsRptGateSample> GetGateSampleAsync(
        Guid domainId, DateTime sinceUtc, CancellationToken ct)
    {
        var policies = await db.SmtpTlsReportPolicies
            .AsNoTracking()
            .Where(p => p.DomainId == domainId && p.ReportRangeEndUtc >= sinceUtc)
            .Select(p => new { p.Id, p.SmtpTlsReportId, p.SuccessfulSessionCount, p.FailureSessionCount })
            .ToListAsync(ct);

        if (policies.Count == 0)
        {
            return new TlsRptGateSample(0, 0, 0);
        }

        var policyIds = policies.Select(p => p.Id).ToList();
        var stsFailures = await db.SmtpTlsFailureDetails
            .AsNoTracking()
            .Where(d => policyIds.Contains(d.SmtpTlsReportPolicyId)
                && d.FailureCategory == TlsRptFailureClassifier.Sts)
            .SumAsync(d => (long?)d.FailedSessionCount, ct) ?? 0;

        return new TlsRptGateSample(
            policies.Sum(p => p.SuccessfulSessionCount + p.FailureSessionCount),
            stsFailures,
            policies.Select(p => p.SmtpTlsReportId).Distinct().Count());
    }

    /// <summary>Anchored to the newest TLS policy row the caller can see, DMARC-window doctrine applied to TLS's own data.</summary>
    private async Task<AnalyticsWindowDto> ResolveWindowAsync(int days, CancellationToken ct)
    {
        var scoped = db.SmtpTlsReportPolicies.AsNoTracking().AsQueryable();
        if (!currentUser.IsAgencyStaff)
        {
            var allowed = currentUser.AllowedClientIds;
            scoped = scoped.Where(p => allowed.Contains(p.Domain!.ClientId));
        }

        var latestEnd = await scoped.MaxAsync(p => (DateTime?)p.ReportRangeEndUtc, ct);
        var endUtc = latestEnd ?? DateTime.UtcNow;
        return new AnalyticsWindowDto(days, endUtc.AddDays(-days), endUtc, latestEnd.HasValue);
    }

    private static int ClampDays(int days) => days switch
    {
        <= 0 => 30,
        > 365 => 365,
        _ => days,
    };

    private static double Rate(long part, long total)
        => total == 0 ? 0 : Math.Round((double)part / total, 4);
}
