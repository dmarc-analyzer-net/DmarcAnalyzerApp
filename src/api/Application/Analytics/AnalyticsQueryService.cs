using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Analytics;

public sealed class AnalyticsQueryService(DmarcAnalyzerDbContext db) : IAnalyticsQueryService
{
    private const double AlignedThreshold = 0.98;
    private const double IssuesThreshold = 0.90;

    public async Task<AnalyticsSummaryDto> GetSummaryAsync(int days, CancellationToken ct)
    {
        days = ClampDays(days);
        var window = await ResolveWindowAsync(days, ct);

        var records = RecordsInWindow(window);

        var totalsRow = await records
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Messages = g.Sum(r => (long)r.MessageCount),
                Compliant = g.Sum(r => r.DkimResult == "pass" || r.SpfResult == "pass" ? (long)r.MessageCount : 0L),
                DkimPass = g.Sum(r => r.DkimResult == "pass" ? (long)r.MessageCount : 0L),
                SpfPass = g.Sum(r => r.SpfResult == "pass" ? (long)r.MessageCount : 0L),
            })
            .FirstOrDefaultAsync(ct);

        var failingSources = await records
            .Where(r => r.DkimResult != "pass" && r.SpfResult != "pass")
            .Select(r => r.SourceIp)
            .Distinct()
            .CountAsync(ct);

        var domainCount = await db.Domains.CountAsync(ct);
        var activeDomainCount = await db.Domains.CountAsync(x => x.IsActive, ct);
        var reportCount = await db.DmarcReports
            .CountAsync(x => x.RangeBeginUtc >= window.BeginUtc && x.RangeBeginUtc <= window.EndUtc, ct);

        var trendRows = await records
            .GroupBy(r => r.DmarcReport!.RangeBeginUtc.Date)
            .Select(g => new
            {
                Date = g.Key,
                Messages = g.Sum(r => (long)r.MessageCount),
                Compliant = g.Sum(r => r.DkimResult == "pass" || r.SpfResult == "pass" ? (long)r.MessageCount : 0L),
            })
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        var trend = trendRows
            .Select(x => new AnalyticsTrendPointDto(
                x.Date.ToString("yyyy-MM-dd"),
                x.Messages,
                x.Compliant,
                x.Messages - x.Compliant))
            .ToArray();

        var failingDomains = await records
            .GroupBy(r => new { r.DmarcReport!.Domain!.Id, r.DmarcReport.Domain.Name })
            .Select(g => new
            {
                g.Key.Id,
                g.Key.Name,
                Messages = g.Sum(r => (long)r.MessageCount),
                Compliant = g.Sum(r => r.DkimResult == "pass" || r.SpfResult == "pass" ? (long)r.MessageCount : 0L),
            })
            .Where(x => x.Messages > x.Compliant)
            .OrderByDescending(x => x.Messages - x.Compliant)
            .Take(5)
            .ToListAsync(ct);

        var topFailingDomains = failingDomains
            .Select(x => new AnalyticsFailingDomainDto(
                x.Id,
                x.Name,
                x.Messages,
                x.Messages - x.Compliant,
                Rate(x.Compliant, x.Messages)))
            .ToArray();

        var topReporters = await records
            .GroupBy(r => r.DmarcReport!.OrganizationName)
            .Select(g => new
            {
                OrganizationName = g.Key,
                Reports = g.Select(r => r.DmarcReportId).Distinct().Count(),
                Messages = g.Sum(r => (long)r.MessageCount),
            })
            .OrderByDescending(x => x.Messages)
            .Take(5)
            .ToListAsync(ct);

        var dispositionRows = await records
            .GroupBy(r => r.Disposition)
            .Select(g => new { Disposition = g.Key, Messages = g.Sum(r => (long)r.MessageCount) })
            .ToListAsync(ct);

        var dispositions = new AnalyticsDispositionsDto(
            dispositionRows.Where(x => x.Disposition == "none").Sum(x => x.Messages),
            dispositionRows.Where(x => x.Disposition == "quarantine").Sum(x => x.Messages),
            dispositionRows.Where(x => x.Disposition == "reject").Sum(x => x.Messages));

        var mailboxTotal = await db.MailboxSources.CountAsync(ct);
        var latestRunStatuses = await db.MailboxSyncRuns
            .GroupBy(x => x.MailboxSourceId)
            .Select(g => g.OrderByDescending(r => r.StartedAtUtc).First().Status)
            .ToListAsync(ct);
        var failingMailboxes = latestRunStatuses.Count(x => x == "failed");

        var totals = new AnalyticsTotalsDto(
            domainCount,
            activeDomainCount,
            reportCount,
            totalsRow?.Messages ?? 0,
            totalsRow?.Compliant ?? 0,
            Rate(totalsRow?.Compliant ?? 0, totalsRow?.Messages ?? 0),
            Rate(totalsRow?.DkimPass ?? 0, totalsRow?.Messages ?? 0),
            Rate(totalsRow?.SpfPass ?? 0, totalsRow?.Messages ?? 0),
            failingSources);

        return new AnalyticsSummaryDto(
            window,
            totals,
            trend,
            topFailingDomains,
            topReporters.Select(x => new AnalyticsReporterDto(x.OrganizationName, x.Reports, x.Messages)).ToArray(),
            dispositions,
            new AnalyticsMailboxesDto(mailboxTotal, mailboxTotal - failingMailboxes, failingMailboxes));
    }

    public async Task<IReadOnlyList<DomainAnalyticsDto>> ListDomainAnalyticsAsync(int days, CancellationToken ct)
    {
        days = ClampDays(days);
        var window = await ResolveWindowAsync(days, ct);

        var records = RecordsInWindow(window);

        var perDomain = await records
            .GroupBy(r => r.DmarcReport!.DomainId)
            .Select(g => new
            {
                DomainId = g.Key,
                Messages = g.Sum(r => (long)r.MessageCount),
                Compliant = g.Sum(r => r.DkimResult == "pass" || r.SpfResult == "pass" ? (long)r.MessageCount : 0L),
                DkimPass = g.Sum(r => r.DkimResult == "pass" ? (long)r.MessageCount : 0L),
                SpfPass = g.Sum(r => r.SpfResult == "pass" ? (long)r.MessageCount : 0L),
                Quarantined = g.Sum(r => r.Disposition == "quarantine" ? (long)r.MessageCount : 0L),
                Rejected = g.Sum(r => r.Disposition == "reject" ? (long)r.MessageCount : 0L),
                Reports = g.Select(r => r.DmarcReportId).Distinct().Count(),
                Sources = g.Select(r => r.SourceIp).Distinct().Count(),
                Reporters = g.Select(r => r.DmarcReport!.OrganizationName).Distinct().Count(),
            })
            .ToListAsync(ct);

        var lastReportEnds = await db.DmarcReports
            .GroupBy(x => x.DomainId)
            .Select(g => new { DomainId = g.Key, LastEnd = g.Max(x => x.RangeEndUtc) })
            .ToListAsync(ct);

        var domains = await db.Domains
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        var statsByDomain = perDomain.ToDictionary(x => x.DomainId);
        var lastEndByDomain = lastReportEnds.ToDictionary(x => x.DomainId, x => x.LastEnd);

        return domains
            .Select(d =>
            {
                statsByDomain.TryGetValue(d.Id, out var s);
                var messages = s?.Messages ?? 0;
                var compliant = s?.Compliant ?? 0;
                var rate = Rate(compliant, messages);

                return new DomainAnalyticsDto(
                    d.Id,
                    d.Name,
                    d.IsActive,
                    messages,
                    compliant,
                    rate,
                    Rate(s?.DkimPass ?? 0, messages),
                    Rate(s?.SpfPass ?? 0, messages),
                    s?.Reports ?? 0,
                    s?.Sources ?? 0,
                    s?.Reporters ?? 0,
                    s?.Quarantined ?? 0,
                    s?.Rejected ?? 0,
                    lastEndByDomain.TryGetValue(d.Id, out var lastEnd) ? lastEnd : null,
                    ResolveStatus(messages, rate));
            })
            .ToArray();
    }

    private IQueryable<Data.Entities.DmarcReportRecord> RecordsInWindow(AnalyticsWindowDto window)
        => db.DmarcReportRecords
            .AsNoTracking()
            .Where(r => r.DmarcReport!.RangeBeginUtc >= window.BeginUtc &&
                        r.DmarcReport.RangeBeginUtc <= window.EndUtc);

    private async Task<AnalyticsWindowDto> ResolveWindowAsync(int days, CancellationToken ct)
    {
        // Report data can lag far behind the wall clock (backfilled mailboxes),
        // so relative windows anchor to the newest report instead of now.
        var latestEnd = await db.DmarcReports.MaxAsync(x => (DateTime?)x.RangeEndUtc, ct);
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

    private static string ResolveStatus(long messages, double complianceRate)
    {
        if (messages == 0)
        {
            return "no_data";
        }

        return complianceRate switch
        {
            >= AlignedThreshold => "aligned",
            >= IssuesThreshold => "issues",
            _ => "critical",
        };
    }
}
