using DmarcAnalyzer.Api.Application.Auth;
using DmarcAnalyzer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DmarcAnalyzer.Api.Application.Analytics;

public sealed class AnalyticsQueryService(DmarcAnalyzerDbContext db, ICurrentUserContext currentUser) : IAnalyticsQueryService
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

        var domainCount = await ScopedDomains().CountAsync(ct);
        var activeDomainCount = await ScopedDomains().CountAsync(x => x.IsActive, ct);
        var reportCount = await ScopedReports()
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

        // Mailbox operations are agency-internal; viewers get no mailbox block.
        AnalyticsMailboxesDto? mailboxes = null;
        if (currentUser.IsAgencyStaff)
        {
            var mailboxTotal = await db.MailboxSources.CountAsync(ct);
            var latestRunStatuses = await db.MailboxSyncRuns
                .GroupBy(x => x.MailboxSourceId)
                .Select(g => g.OrderByDescending(r => r.StartedAtUtc).First().Status)
                .ToListAsync(ct);
            var failingMailboxes = latestRunStatuses.Count(x => x == "failed");
            mailboxes = new AnalyticsMailboxesDto(mailboxTotal, mailboxTotal - failingMailboxes, failingMailboxes);
        }

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
            mailboxes);
    }

    public async Task<IReadOnlyList<DomainAnalyticsDto>> ListDomainAnalyticsAsync(int days, CancellationToken ct)
    {
        days = ClampDays(days);
        var window = await ResolveWindowAsync(days, ct);

        var records = RecordsInWindow(window);

        // Flattened before grouping for the same reason as ListDomainSourcesAsync:
        // navigations inside grouped aggregates become per-group correlated subqueries.
        var perDomain = await records
            .Select(r => new
            {
                r.DmarcReport!.DomainId,
                r.MessageCount,
                r.DkimResult,
                r.SpfResult,
                r.Disposition,
                r.DmarcReportId,
                r.SourceIp,
                r.DmarcReport.OrganizationName,
            })
            .GroupBy(r => r.DomainId)
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
                Reporters = g.Select(r => r.OrganizationName).Distinct().Count(),
            })
            .ToListAsync(ct);

        var lastReportEnds = await ScopedReports()
            .GroupBy(x => x.DomainId)
            .Select(g => new { DomainId = g.Key, LastEnd = g.Max(x => x.RangeEndUtc) })
            .ToListAsync(ct);

        var domains = await ScopedDomains()
            .OrderBy(x => x.Name)
            .Select(x => new
            {
                x.Id,
                x.Name,
                x.IsActive,
                x.ClientId,
                ClientName = x.Client!.Name,
                ClientSlug = x.Client.Slug,
            })
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
                    d.ClientId,
                    d.ClientName,
                    d.ClientSlug,
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

    public async Task<DomainDrilldownDto?> GetDomainDrilldownAsync(Guid domainId, int days, CancellationToken ct)
    {
        days = ClampDays(days);

        var domain = await db.Domains
            .AsNoTracking()
            .Where(x => x.Id == domainId)
            .Select(x => new DomainDrilldownDomainDto(
                x.Id, x.Name, x.IsActive, x.ClientId, x.Client!.Name, x.Client.Slug))
            .SingleOrDefaultAsync(ct);

        // Cross-tenant ids read as not-found to avoid an existence oracle.
        if (domain is null || !currentUser.CanAccessClient(domain.ClientId))
        {
            return null;
        }

        var window = await ResolveWindowAsync(days, ct);
        var records = DomainRecordsInWindow(domainId, window);

        var totalsRow = await records
            .GroupBy(_ => 1)
            .Select(g => new
            {
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
            .FirstOrDefaultAsync(ct);

        var messages = totalsRow?.Messages ?? 0;
        var compliant = totalsRow?.Compliant ?? 0;
        var rate = Rate(compliant, messages);

        var totals = new DomainDrilldownTotalsDto(
            messages,
            compliant,
            rate,
            Rate(totalsRow?.DkimPass ?? 0, messages),
            Rate(totalsRow?.SpfPass ?? 0, messages),
            totalsRow?.Reports ?? 0,
            totalsRow?.Sources ?? 0,
            totalsRow?.Reporters ?? 0,
            totalsRow?.Quarantined ?? 0,
            totalsRow?.Rejected ?? 0,
            ResolveStatus(messages, rate));

        return new DomainDrilldownDto(domain, window, totals, await TrendAsync(records, ct));
    }

    public async Task<IReadOnlyList<DomainSourceDto>?> ListDomainSourcesAsync(Guid domainId, int days, CancellationToken ct)
    {
        days = ClampDays(days);

        // This access check is the tenant gate for the raw-SQL aggregation
        // below (the SQL itself is keyed by domainId only).
        if (!await CanAccessDomainAsync(domainId, ct))
        {
            return null;
        }

        var window = await ResolveWindowAsync(days, ct);

        // Hand-written SQL: EF translates grouped distinct-counts/min/max over
        // navigations into per-group correlated subqueries (33s for a domain
        // with 1.3k sources); a single-pass GROUP BY does the same in ~75ms.
        var rows = await db.Database
            .SqlQuery<SourceAggregateRow>($@"
                SELECT rec.""SourceIp"",
                       SUM(rec.""MessageCount"")::bigint                                                                        AS ""Messages"",
                       SUM(CASE WHEN rec.""DkimResult"" = 'pass' OR rec.""SpfResult"" = 'pass' THEN rec.""MessageCount"" ELSE 0 END)::bigint AS ""Compliant"",
                       SUM(CASE WHEN rec.""DkimResult"" = 'pass' THEN rec.""MessageCount"" ELSE 0 END)::bigint                  AS ""DkimPass"",
                       SUM(CASE WHEN rec.""SpfResult"" = 'pass' THEN rec.""MessageCount"" ELSE 0 END)::bigint                   AS ""SpfPass"",
                       SUM(CASE WHEN rec.""Disposition"" = 'quarantine' THEN rec.""MessageCount"" ELSE 0 END)::bigint           AS ""Quarantined"",
                       SUM(CASE WHEN rec.""Disposition"" = 'reject' THEN rec.""MessageCount"" ELSE 0 END)::bigint               AS ""Rejected"",
                       COUNT(DISTINCT r.""OrganizationName"")::int                                                              AS ""Reporters"",
                       COUNT(DISTINCT rec.""HeaderFrom"")::int                                                                  AS ""HeaderFroms"",
                       MIN(r.""RangeBeginUtc"")                                                                                 AS ""FirstSeen"",
                       MAX(r.""RangeEndUtc"")                                                                                   AS ""LastSeen""
                FROM dmarc_report_record rec
                JOIN dmarc_report r ON r.""Id"" = rec.""DmarcReportId""
                WHERE r.""DomainId"" = {domainId}
                  AND r.""RangeBeginUtc"" >= {window.BeginUtc}
                  AND r.""RangeBeginUtc"" <= {window.EndUtc}
                GROUP BY rec.""SourceIp""")
            .ToListAsync(ct);

        return rows
            .Select(x => new DomainSourceDto(
                x.SourceIp,
                x.Messages,
                x.Compliant,
                x.Messages - x.Compliant,
                Rate(x.Compliant, x.Messages),
                Rate(x.DkimPass, x.Messages),
                Rate(x.SpfPass, x.Messages),
                x.Quarantined,
                x.Rejected,
                x.Reporters,
                x.HeaderFroms,
                x.FirstSeen,
                x.LastSeen))
            .OrderByDescending(x => x.FailedMessages)
            .ThenByDescending(x => x.Messages)
            .ToArray();
    }

    public async Task<SourceDetailDto?> GetSourceDetailAsync(Guid domainId, string sourceIp, int days, CancellationToken ct)
    {
        days = ClampDays(days);

        if (!await CanAccessDomainAsync(domainId, ct))
        {
            return null;
        }

        var window = await ResolveWindowAsync(days, ct);
        var records = DomainRecordsInWindow(domainId, window).Where(r => r.SourceIp == sourceIp);

        var evaluated = await records
            .GroupBy(r => new { r.DkimResult, r.SpfResult, r.Disposition })
            .Select(g => new
            {
                g.Key.DkimResult,
                g.Key.SpfResult,
                g.Key.Disposition,
                Messages = g.Sum(r => (long)r.MessageCount),
            })
            .ToListAsync(ct);

        var messages = evaluated.Sum(x => x.Messages);
        var compliant = evaluated.Where(x => x.DkimResult == "pass" || x.SpfResult == "pass").Sum(x => x.Messages);

        var dispositions = new AnalyticsDispositionsDto(
            evaluated.Where(x => x.Disposition == "none").Sum(x => x.Messages),
            evaluated.Where(x => x.Disposition == "quarantine").Sum(x => x.Messages),
            evaluated.Where(x => x.Disposition == "reject").Sum(x => x.Messages));

        var combos = evaluated
            .GroupBy(x => new { x.DkimResult, x.SpfResult })
            .Select(g => new SourceEvaluatedComboDto(g.Key.DkimResult, g.Key.SpfResult, g.Sum(x => x.Messages)))
            .OrderByDescending(x => x.Messages)
            .ToArray();

        var headerFroms = await GroupValuesAsync(records.GroupBy(r => r.HeaderFrom), ct);
        var envelopeFroms = await GroupValuesAsync(records.GroupBy(r => r.EnvelopeFrom), ct);

        var dkimAuthRows = await db.DmarcReportRecordDkimAuthResults
            .AsNoTracking()
            .Where(a => a.DmarcReportRecord!.SourceIp == sourceIp &&
                        a.DmarcReportRecord.DmarcReport!.DomainId == domainId &&
                        a.DmarcReportRecord.DmarcReport.RangeBeginUtc >= window.BeginUtc &&
                        a.DmarcReportRecord.DmarcReport.RangeBeginUtc <= window.EndUtc)
            .GroupBy(a => new { a.Domain, a.Selector, a.Result })
            .Select(g => new
            {
                g.Key.Domain,
                g.Key.Selector,
                g.Key.Result,
                Messages = g.Sum(a => (long)a.DmarcReportRecord!.MessageCount),
            })
            .OrderByDescending(x => x.Messages)
            .Take(15)
            .ToListAsync(ct);
        var dkimAuth = dkimAuthRows
            .Select(x => new SourceDkimAuthDto(x.Domain, x.Selector, x.Result, x.Messages))
            .ToArray();

        var spfAuthRows = await db.DmarcReportRecordSpfAuthResults
            .AsNoTracking()
            .Where(a => a.DmarcReportRecord!.SourceIp == sourceIp &&
                        a.DmarcReportRecord.DmarcReport!.DomainId == domainId &&
                        a.DmarcReportRecord.DmarcReport.RangeBeginUtc >= window.BeginUtc &&
                        a.DmarcReportRecord.DmarcReport.RangeBeginUtc <= window.EndUtc)
            .GroupBy(a => new { a.Domain, a.Scope, a.Result })
            .Select(g => new
            {
                g.Key.Domain,
                g.Key.Scope,
                g.Key.Result,
                Messages = g.Sum(a => (long)a.DmarcReportRecord!.MessageCount),
            })
            .OrderByDescending(x => x.Messages)
            .Take(15)
            .ToListAsync(ct);
        var spfAuth = spfAuthRows
            .Select(x => new SourceSpfAuthDto(x.Domain, x.Scope, x.Result, x.Messages))
            .ToArray();

        var reporterRows = await records
            .GroupBy(r => r.DmarcReport!.OrganizationName)
            .Select(g => new
            {
                OrganizationName = g.Key,
                Reports = g.Select(r => r.DmarcReportId).Distinct().Count(),
                Messages = g.Sum(r => (long)r.MessageCount),
            })
            .OrderByDescending(x => x.Messages)
            .Take(10)
            .ToListAsync(ct);
        var reporters = reporterRows
            .Select(x => new SourceReporterDto(x.OrganizationName, x.Reports, x.Messages))
            .ToArray();

        return new SourceDetailDto(
            sourceIp,
            messages,
            compliant,
            Rate(compliant, messages),
            dispositions,
            combos,
            headerFroms,
            envelopeFroms,
            dkimAuth,
            spfAuth,
            reporters,
            await TrendAsync(records, ct));
    }

    private async Task<IReadOnlyList<AnalyticsTrendPointDto>> TrendAsync(
        IQueryable<Data.Entities.DmarcReportRecord> records,
        CancellationToken ct)
    {
        var rows = await records
            .GroupBy(r => r.DmarcReport!.RangeBeginUtc.Date)
            .Select(g => new
            {
                Date = g.Key,
                Messages = g.Sum(r => (long)r.MessageCount),
                Compliant = g.Sum(r => r.DkimResult == "pass" || r.SpfResult == "pass" ? (long)r.MessageCount : 0L),
            })
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        return rows
            .Select(x => new AnalyticsTrendPointDto(
                x.Date.ToString("yyyy-MM-dd"),
                x.Messages,
                x.Compliant,
                x.Messages - x.Compliant))
            .ToArray();
    }

    private static async Task<IReadOnlyList<SourceValueCountDto>> GroupValuesAsync(
        IQueryable<IGrouping<string, Data.Entities.DmarcReportRecord>> grouped,
        CancellationToken ct)
    {
        var rows = await grouped
            .Select(g => new { Value = g.Key, Messages = g.Sum(r => (long)r.MessageCount) })
            .OrderByDescending(x => x.Messages)
            .Take(10)
            .ToListAsync(ct);

        return rows
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => new SourceValueCountDto(x.Value, x.Messages))
            .ToArray();
    }

    private sealed class SourceAggregateRow
    {
        public string SourceIp { get; set; } = string.Empty;
        public long Messages { get; set; }
        public long Compliant { get; set; }
        public long DkimPass { get; set; }
        public long SpfPass { get; set; }
        public long Quarantined { get; set; }
        public long Rejected { get; set; }
        public int Reporters { get; set; }
        public int HeaderFroms { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }

    private async Task<bool> CanAccessDomainAsync(Guid domainId, CancellationToken ct)
    {
        var clientId = await db.Domains
            .AsNoTracking()
            .Where(x => x.Id == domainId)
            .Select(x => (Guid?)x.ClientId)
            .SingleOrDefaultAsync(ct);

        return clientId.HasValue && currentUser.CanAccessClient(clientId.Value);
    }

    private IQueryable<Data.Entities.DmarcReportRecord> DomainRecordsInWindow(Guid domainId, AnalyticsWindowDto window)
        => RecordsInWindow(window).Where(r => r.DmarcReport!.DomainId == domainId);

    private IQueryable<Data.Entities.DmarcReportRecord> RecordsInWindow(AnalyticsWindowDto window)
    {
        var query = db.DmarcReportRecords
            .AsNoTracking()
            .Where(r => r.DmarcReport!.RangeBeginUtc >= window.BeginUtc &&
                        r.DmarcReport.RangeBeginUtc <= window.EndUtc);

        if (!currentUser.IsAgencyStaff)
        {
            var allowed = currentUser.AllowedClientIds;
            query = query.Where(r => allowed.Contains(r.DmarcReport!.Domain!.ClientId));
        }

        return query;
    }

    private IQueryable<Data.Entities.Domain> ScopedDomains()
    {
        var query = db.Domains.AsNoTracking();

        if (!currentUser.IsAgencyStaff)
        {
            var allowed = currentUser.AllowedClientIds;
            query = query.Where(x => allowed.Contains(x.ClientId));
        }

        return query;
    }

    private IQueryable<Data.Entities.DmarcReport> ScopedReports()
    {
        var query = db.DmarcReports.AsNoTracking();

        if (!currentUser.IsAgencyStaff)
        {
            var allowed = currentUser.AllowedClientIds;
            query = query.Where(x => allowed.Contains(x.Domain!.ClientId));
        }

        return query;
    }

    private async Task<AnalyticsWindowDto> ResolveWindowAsync(int days, CancellationToken ct)
    {
        // Report data can lag far behind the wall clock (backfilled mailboxes),
        // so relative windows anchor to the newest report instead of now. The
        // anchor is tenant-scoped so viewers don't learn other tenants' activity.
        var latestEnd = await ScopedReports().MaxAsync(x => (DateTime?)x.RangeEndUtc, ct);
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
