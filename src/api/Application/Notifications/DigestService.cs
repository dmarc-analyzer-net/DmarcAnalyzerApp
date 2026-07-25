using System.Text;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Notifications;

public sealed record DigestDomainLine(string Domain, long Messages, double ComplianceRate, string Policy);

public sealed record DigestSummary(
    Guid ClientId,
    string ClientName,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    int Domains,
    long Messages,
    long CompliantMessages,
    double ComplianceRate,
    double PreviousComplianceRate,
    int FailingSources,
    int DomainsEnforcing,
    int AlertsRaised,
    IReadOnlyList<DigestDomainLine> WorstDomains);

public sealed record DigestSendResult(int ClientsConsidered, int Sent, int Skipped, IReadOnlyList<string> SentTo);

public interface IDigestService
{
    /// <summary>Builds a client's summary for a period without sending anything.</summary>
    Task<DigestSummary?> BuildAsync(Guid clientId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct);

    /// <summary>Renders a summary as the plain-text email body.</summary>
    string Render(DigestSummary summary);

    /// <summary>
    /// Sends last month's digest to every client that hasn't already had one for
    /// that period. Safe to call repeatedly.
    /// </summary>
    Task<DigestSendResult> SendDueAsync(CancellationToken ct);
}

/// <summary>
/// Monthly per-client summary. Reuses the recipient model and SMTP relay built for
/// alerting; the only additions are the content and the once-a-month schedule.
///
/// Queries the report tables directly rather than going through
/// <c>AnalyticsQueryService</c>, because that service scopes every read to the
/// signed-in user and there is no user in a worker pass.
/// </summary>
public sealed class DigestService(
    DmarcAnalyzerDbContext db,
    IEmailSender email,
    IOptions<DigestOptions> digestOptions,
    IOptions<EmailOptions> emailOptions,
    ILogger<DigestService> logger) : IDigestService
{
    private readonly DigestOptions _options = digestOptions.Value;
    private readonly EmailOptions _email = emailOptions.Value;

    public async Task<DigestSummary?> BuildAsync(
        Guid clientId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
    {
        var client = await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId)
            .Select(c => new { c.Id, c.Name })
            .SingleOrDefaultAsync(ct);
        if (client is null)
        {
            return null;
        }

        var domainIds = await db.Domains.AsNoTracking()
            .Where(d => d.ClientId == clientId)
            .Select(d => d.Id)
            .ToListAsync(ct);

        var perDomain = await db.DmarcReportRecords.AsNoTracking()
            .Where(r => domainIds.Contains(r.DmarcReport!.DomainId) &&
                        r.DmarcReport.RangeBeginUtc >= periodStartUtc &&
                        r.DmarcReport.RangeBeginUtc < periodEndUtc)
            .Select(r => new
            {
                r.DmarcReport!.DomainId,
                DomainName = r.DmarcReport.Domain!.Name,
                r.MessageCount,
                r.DkimResult,
                r.SpfResult,
                r.SourceIp,
            })
            .GroupBy(r => new { r.DomainId, r.DomainName })
            .Select(g => new
            {
                g.Key.DomainName,
                Messages = g.Sum(x => (long)x.MessageCount),
                Compliant = g.Sum(x => x.DkimResult == "pass" || x.SpfResult == "pass" ? (long)x.MessageCount : 0L),
                FailingSources = g.Where(x => x.DkimResult != "pass" && x.SpfResult != "pass")
                    .Select(x => x.SourceIp).Distinct().Count(),
            })
            .ToListAsync(ct);

        var messages = perDomain.Sum(x => x.Messages);
        var compliant = perDomain.Sum(x => x.Compliant);

        // Same window length immediately before, so the digest can say whether
        // things got better or worse rather than just stating a number.
        var previousStart = periodStartUtc.AddMonths(-1);
        var previousRow = await db.DmarcReportRecords.AsNoTracking()
            .Where(r => domainIds.Contains(r.DmarcReport!.DomainId) &&
                        r.DmarcReport.RangeBeginUtc >= previousStart &&
                        r.DmarcReport.RangeBeginUtc < periodStartUtc)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Messages = g.Sum(x => (long)x.MessageCount),
                Compliant = g.Sum(x => x.DkimResult == "pass" || x.SpfResult == "pass" ? (long)x.MessageCount : 0L),
            })
            .FirstOrDefaultAsync(ct);

        // Latest published policy per domain, to count how many are enforcing.
        var policies = await db.DmarcReports.AsNoTracking()
            .Where(r => domainIds.Contains(r.DomainId) && r.RangeBeginUtc < periodEndUtc)
            .GroupBy(r => r.DomainId)
            .Select(g => g.OrderByDescending(r => r.RangeEndUtc).Select(r => r.PublishedPolicy).First())
            .ToListAsync(ct);

        var alerts = await db.AlertEvents.AsNoTracking()
            .CountAsync(a => a.ClientId == clientId &&
                             a.DetectedAtUtc >= periodStartUtc && a.DetectedAtUtc < periodEndUtc, ct);

        var policyByDomain = await LatestPolicyByDomainNameAsync(domainIds, periodEndUtc, ct);

        var worst = perDomain
            .Where(x => x.Messages > 0)
            .OrderBy(x => (double)x.Compliant / x.Messages)
            .ThenByDescending(x => x.Messages)
            .Take(5)
            .Select(x => new DigestDomainLine(
                x.DomainName, x.Messages, Rate(x.Compliant, x.Messages),
                policyByDomain.GetValueOrDefault(x.DomainName, "unknown")))
            .ToArray();

        return new DigestSummary(
            client.Id, client.Name, periodStartUtc, periodEndUtc,
            domainIds.Count, messages, compliant,
            Rate(compliant, messages),
            Rate(previousRow?.Compliant ?? 0, previousRow?.Messages ?? 0),
            perDomain.Sum(x => x.FailingSources),
            policies.Count(p => string.Equals(p, "reject", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(p, "quarantine", StringComparison.OrdinalIgnoreCase)),
            alerts,
            worst);
    }

    private async Task<Dictionary<string, string>> LatestPolicyByDomainNameAsync(
        List<Guid> domainIds, DateTime before, CancellationToken ct)
    {
        var rows = await db.DmarcReports.AsNoTracking()
            .Where(r => domainIds.Contains(r.DomainId) && r.RangeBeginUtc < before)
            .Select(r => new { r.Domain!.Name, r.PublishedPolicy, r.RangeEndUtc })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.Name)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.RangeEndUtc).First().PublishedPolicy);
    }

    public string Render(DigestSummary s)
    {
        var body = new StringBuilder();
        var period = $"{s.PeriodStartUtc:d MMMM yyyy} – {s.PeriodEndUtc.AddDays(-1):d MMMM yyyy}";

        body.AppendLine($"DMARC summary for {s.ClientName}");
        body.AppendLine(period);
        body.AppendLine();

        if (s.Messages == 0)
        {
            body.AppendLine("No DMARC reports covered this period.");
            body.AppendLine();
            body.AppendLine($"Domains monitored: {s.Domains}");
            return body.ToString();
        }

        var change = (s.ComplianceRate - s.PreviousComplianceRate) * 100;
        var direction = s.PreviousComplianceRate == 0
            ? "no comparable data for the previous period"
            : change >= 0.05
                ? $"up {change:F1} points on the previous period"
                : change <= -0.05
                    ? $"down {Math.Abs(change):F1} points on the previous period"
                    : "level with the previous period";

        body.AppendLine($"DMARC compliance: {s.ComplianceRate * 100:F1}% ({direction})");
        body.AppendLine($"Messages seen:    {s.Messages:N0}");
        body.AppendLine($"Failing messages: {s.Messages - s.CompliantMessages:N0}");
        body.AppendLine($"Domains:          {s.Domains} monitored, {s.DomainsEnforcing} at quarantine or reject");
        body.AppendLine($"Unauthenticated sending sources: {s.FailingSources}");
        body.AppendLine($"Alerts raised:    {s.AlertsRaised}");
        body.AppendLine();

        if (s.WorstDomains.Count > 0)
        {
            body.AppendLine("Domains needing attention:");
            foreach (var d in s.WorstDomains)
            {
                body.AppendLine($"  {d.Domain} — {d.ComplianceRate * 100:F1}% compliant, " +
                                $"{d.Messages:N0} messages, p={d.Policy}");
            }
            body.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(_email.BaseUrl))
        {
            body.AppendLine($"Full detail: {_email.BaseUrl.TrimEnd('/')}/domains");
        }

        return body.ToString();
    }

    public async Task<DigestSendResult> SendDueAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return new DigestSendResult(0, 0, 0, []);
        }

        var now = DateTime.UtcNow;

        // Only start sending once the configured day of the month has arrived, so
        // a digest covers a complete month rather than landing on the 1st at 00:05
        // of a month that has barely begun.
        if (now.Day < Math.Clamp(_options.DayOfMonth, 1, 28))
        {
            return new DigestSendResult(0, 0, 0, []);
        }

        // The period is always the previous whole calendar month.
        var periodStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
        var periodEnd = periodStart.AddMonths(1);

        var clients = await db.Clients.AsNoTracking()
            .Where(c => c.IsActive)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);

        var recipients = await db.NotificationRecipients.AsNoTracking()
            .Where(r => r.IsActive && (r.Kind == "digest" || r.Kind == "both"))
            .Select(r => new { r.ClientId, r.Email })
            .ToListAsync(ct);
        var agencyWide = recipients.Where(r => r.ClientId is null).Select(r => r.Email).ToList();

        var sent = 0;
        var skipped = 0;
        var sentTo = new List<string>();

        foreach (var client in clients)
        {
            ct.ThrowIfCancellationRequested();

            var already = await db.DigestDeliveries
                .AnyAsync(d => d.ClientId == client.Id && d.PeriodStartUtc == periodStart, ct);
            if (already)
            {
                skipped++;
                continue;
            }

            var to = recipients.Where(r => r.ClientId == client.Id).Select(r => r.Email)
                .Concat(agencyWide).Distinct().ToList();
            if (to.Count == 0)
            {
                skipped++;
                continue;
            }

            var summary = await BuildAsync(client.Id, periodStart, periodEnd, ct);
            if (summary is null)
            {
                skipped++;
                continue;
            }

            var subject = $"[DMARC] {client.Name} — {periodStart:MMMM yyyy} summary";
            var delivered = await email.SendAsync(to, subject, Render(summary), ct);

            // Recorded either way: without this a broken relay would retry the same
            // month on every pass. RecipientCount 0 marks a period as attempted.
            db.DigestDeliveries.Add(new DigestDelivery
            {
                ClientId = client.Id,
                PeriodStartUtc = periodStart,
                PeriodEndUtc = periodEnd,
                SentAtUtc = DateTime.UtcNow,
                RecipientCount = delivered ? to.Count : 0,
            });
            await db.SaveChangesAsync(ct);

            if (delivered)
            {
                sent++;
                sentTo.Add(client.Name);
            }
        }

        if (sent > 0 || skipped > 0)
        {
            logger.LogInformation(
                "Digest for {Period:yyyy-MM}: sent {Sent}, skipped {Skipped}",
                periodStart, sent, skipped);
        }

        return new DigestSendResult(clients.Count, sent, skipped, sentTo);
    }

    private static double Rate(long part, long total) => total == 0 ? 0 : Math.Round((double)part / total, 4);
}
