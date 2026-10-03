using System.Globalization;
using DmarcAnalyzer.Api.Application.Analytics;
using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Notifications;

/// <summary>The monthly digest — see <see cref="DigestService"/>.</summary>
public interface IDigestService
{
    /// <summary>Builds a client's summary for a period without sending anything. Null for an unknown client.</summary>
    Task<DigestSummary?> BuildAsync(Guid clientId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct);

    /// <summary>The single-client digest for a client, as any recipient of it would get it.</summary>
    Task<DigestMail?> PreviewClientAsync(Guid clientId, DateTime periodStartUtc, CancellationToken ct);

    /// <summary>Every digest mail one recipient row would get for a period, whether or not it was already sent. Null for an unknown recipient.</summary>
    Task<IReadOnlyList<DigestMail>?> PreviewRecipientAsync(Guid recipientId, DateTime periodStartUtc, CancellationToken ct);

    /// <summary>
    /// Sends last month's digests that have not gone out yet: per address, one roll-up
    /// for its roll-up clients and one mail per separate client. Safe to call repeatedly.
    /// </summary>
    Task<DigestSendResult> SendDueAsync(CancellationToken ct);
}

/// <summary>
/// Monthly digest. Builds a per-client summary — the month's numbers, the findings that
/// need a reader's attention, the domain table and the worst failing sources — and sends
/// it per address according to <see cref="NotificationRouting"/>: each covered client
/// either in the address's one roll-up or in a mail of its own.
///
/// Queries the report tables directly rather than going through
/// <c>AnalyticsQueryService</c>, because that service scopes every read to the
/// signed-in user and there is no user in a worker pass. Every window filters on the
/// denormalised <c>ReportRangeBeginUtc</c>, which is what keeps it an index range scan.
/// </summary>
public sealed class DigestService(
    DmarcAnalyzerDbContext db,
    IEmailSender email,
    IHostnameResolver hostnames,
    DigestRenderer renderer,
    IOptions<DigestOptions> digestOptions,
    ILogger<DigestService> logger) : IDigestService
{
    /// <summary>How many failing sources a digest lists per client.</summary>
    public const int TopSources = 10;

    /// <summary>How far back a failing source must be unseen to be called new.</summary>
    private const int NewSourceLookbackDays = 90;

    private readonly DigestOptions _options = digestOptions.Value;

    /// <inheritdoc />
    public async Task<DigestSummary?> BuildAsync(
        Guid clientId, DateTime periodStartUtc, DateTime periodEndUtc, CancellationToken ct)
    {
        var client = await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId)
            .Select(c => new { c.Id, c.Name, c.DigestThresholds })
            .SingleOrDefaultAsync(ct);
        if (client is null)
        {
            return null;
        }

        var t = Effective(client.DigestThresholds);

        var domains = await db.Domains.AsNoTracking()
            .Where(d => d.ClientId == clientId && d.IsActive)
            .OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name, d.DnsPolicy, d.DnsLookupStatus, d.CreatedAtUtc })
            .ToListAsync(ct);
        var domainIds = domains.Select(d => d.Id).ToList();

        var current = await PassRatesAsync(domainIds, periodStartUtc, periodEndUtc, ct);
        var previous = await PassRatesAsync(domainIds, periodStartUtc.AddMonths(-1), periodStartUtc, ct);

        var reportPolicies = await db.DmarcReports.AsNoTracking()
            .Where(r => domainIds.Contains(r.DomainId) && r.RangeBeginUtc < periodEndUtc)
            .Select(r => new { r.DomainId, r.PublishedPolicy, r.RangeEndUtc })
            .ToListAsync(ct);
        var reportPolicyByDomain = reportPolicies
            .GroupBy(r => r.DomainId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.RangeEndUtc).First().PublishedPolicy);

        string PolicyOf(Guid id, string? dnsPolicy, string? dnsStatus) =>
            !string.IsNullOrWhiteSpace(dnsPolicy) ? dnsPolicy.ToLowerInvariant()
            : dnsStatus == "missing" ? "missing"
            : reportPolicyByDomain.TryGetValue(id, out var p) && !string.IsNullOrWhiteSpace(p) ? p.ToLowerInvariant()
            : "unknown";

        // Ready-to-tighten needs a longer window than the month, and proof the domain
        // has been reporting for at least that long.
        var tighten = new Dictionary<Guid, (long Messages, long Compliant)>();
        var firstReport = new Dictionary<Guid, DateTime>();
        if (t.TightenAfterDays > 0)
        {
            tighten = await PassRatesAsync(domainIds, periodEndUtc.AddDays(-t.TightenAfterDays), periodEndUtc, ct);
            firstReport = await db.DmarcReports.AsNoTracking()
                .Where(r => domainIds.Contains(r.DomainId))
                .GroupBy(r => r.DomainId)
                .Select(g => new { DomainId = g.Key, First = g.Min(r => r.RangeBeginUtc) })
                .ToDictionaryAsync(x => x.DomainId, x => x.First, ct);
        }

        var month = periodStartUtc.ToString("MMMM", CultureInfo.InvariantCulture);
        var previousMonth = periodStartUtc.AddMonths(-1).ToString("MMMM", CultureInfo.InvariantCulture);
        var findings = new List<DigestFinding>();
        var lines = new List<DigestDomain>();
        foreach (var d in domains)
        {
            var policy = PolicyOf(d.Id, d.DnsPolicy, d.DnsLookupStatus);
            var (messages, compliant) = current.GetValueOrDefault(d.Id);
            var (prevMessages, prevCompliant) = previous.GetValueOrDefault(d.Id);
            var rate = Rate(compliant, messages);
            var prevRate = prevMessages > 0 ? Rate(prevCompliant, prevMessages) : (double?)null;
            var before = findings.Count;

            if (messages == 0)
            {
                // Reports stopping is the signal, not their absence: plenty of monitored
                // domains send a handful of messages a year (seasonal campaigns, parked
                // brands), and flagging every quiet one buried the real findings.
                if (t.NoReports && prevMessages > 0 && prevMessages >= t.MinMessages)
                {
                    findings.Add(new(DigestFindingKind.NoReports, d.Id, d.Name,
                        $"Reports stopped: {DigestFormat.Count(prevMessages)} messages were reported in {previousMonth}, none in {month}.",
                        "Check that the rua= address in its DMARC record still reaches this instance, or whether the domain has stopped sending."));
                }
            }
            else if (messages >= t.MinMessages)
            {
                if (t.LowCompliancePercent > 0 && rate * 100 < t.LowCompliancePercent)
                {
                    findings.Add(new(DigestFindingKind.LowCompliance, d.Id, d.Name,
                        $"{DigestFormat.Percent(rate)} of {DigestFormat.Count(messages)} messages passed " +
                        $"(threshold {DigestFormat.Number(t.LowCompliancePercent)}%).",
                        policy is "reject" or "quarantine"
                            ? "Receivers are blocking or junking the failing mail. Check the failing sources below for a legitimate sender that lost its SPF or DKIM."
                            : "The failing mail is still delivered. Authorise any legitimate sender below in SPF or DKIM before tightening the policy."));
                }

                if (t.ComplianceDropPoints > 0 && prevRate is { } p && prevMessages >= t.MinMessages &&
                    (p - rate) * 100 >= t.ComplianceDropPoints)
                {
                    findings.Add(new(DigestFindingKind.ComplianceDrop, d.Id, d.Name,
                        $"Pass rate fell {DigestFormat.Number((p - rate) * 100)} points, from {DigestFormat.Percent(p)} to {DigestFormat.Percent(rate)}.",
                        "Something that used to authenticate has stopped: look for a new or changed sending service among the failing sources."));
                }
            }

            var hasProblem = findings.Count > before;

            if (!hasProblem && t.TightenAfterDays > 0 && policy is "none" or "quarantine" &&
                tighten.TryGetValue(d.Id, out var window) && window.Messages >= t.MinMessages &&
                Rate(window.Compliant, window.Messages) * 100 >= t.TightenCompliancePercent &&
                firstReport.TryGetValue(d.Id, out var first) && first <= periodEndUtc.AddDays(-t.TightenAfterDays))
            {
                var next = policy == "none" ? "quarantine" : "reject";
                findings.Add(new(DigestFindingKind.ReadyToTighten, d.Id, d.Name,
                    $"Held {DigestFormat.Percent(Rate(window.Compliant, window.Messages))} for the last {t.TightenAfterDays} days at p={policy}.",
                    $"Ready to move to p={next}."));
            }

            lines.Add(new DigestDomain(d.Id, d.Name, policy, messages, compliant, rate, prevRate, hasProblem));
        }

        var sources = await FailingSourcesAsync(domainIds, periodStartUtc, periodEndUtc, ct);

        var alerts = await db.AlertEvents.AsNoTracking()
            .Where(a => a.ClientId == clientId &&
                        a.DetectedAtUtc >= periodStartUtc && a.DetectedAtUtc < periodEndUtc)
            .OrderByDescending(a => a.DetectedAtUtc)
            .Select(a => new DigestAlert(a.Severity, a.Title, a.DetectedAtUtc))
            .ToListAsync(ct);

        var totalMessages = lines.Sum(x => x.Messages);
        var totalCompliant = lines.Sum(x => x.CompliantMessages);
        var prevMessagesTotal = previous.Values.Sum(x => x.Messages);
        var prevCompliantTotal = previous.Values.Sum(x => x.Compliant);

        return new DigestSummary(
            client.Id,
            client.Name,
            periodStartUtc,
            periodEndUtc,
            domains.Count,
            lines.Count(x => x.Policy is "reject" or "quarantine"),
            totalMessages,
            totalCompliant,
            Rate(totalCompliant, totalMessages),
            prevMessagesTotal > 0 ? Rate(prevCompliantTotal, prevMessagesTotal) : null,
            sources.DistinctIps,
            lines
                .OrderByDescending(x => x.HasProblem)
                .ThenByDescending(x => x.Messages)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .ToList(),
            findings,
            sources.Top,
            alerts);
    }

    /// <summary>Messages and aligned passes per domain in a window.</summary>
    private async Task<Dictionary<Guid, (long Messages, long Compliant)>> PassRatesAsync(
        List<Guid> domainIds, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var rows = await db.DmarcReportRecords.AsNoTracking()
            .Where(r => r.ReportRangeBeginUtc >= fromUtc && r.ReportRangeBeginUtc < toUtc &&
                        domainIds.Contains(r.DmarcReport!.DomainId))
            .GroupBy(r => r.DmarcReport!.DomainId)
            .Select(g => new
            {
                DomainId = g.Key,
                Messages = g.Sum(x => (long)x.MessageCount),
                Compliant = g.Sum(x => x.DkimResult == "pass" || x.SpfResult == "pass" ? (long)x.MessageCount : 0L),
            })
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.DomainId, x => (x.Messages, x.Compliant));
    }

    /// <summary>
    /// The sources that failed DMARC (neither aligned DKIM nor aligned SPF passed), one
    /// entry per IP however many of the client's domains it failed for, the worst
    /// <see cref="TopSources"/> by failing volume with their hostname and whether they are new.
    /// </summary>
    private async Task<(int DistinctIps, IReadOnlyList<DigestSource> Top)> FailingSourcesAsync(
        List<Guid> domainIds, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var failing = await db.DmarcReportRecords.AsNoTracking()
            .Where(r => r.ReportRangeBeginUtc >= fromUtc && r.ReportRangeBeginUtc < toUtc &&
                        domainIds.Contains(r.DmarcReport!.DomainId) &&
                        r.DkimResult != "pass" && r.SpfResult != "pass")
            .GroupBy(r => new { r.SourceIp, DomainName = r.DmarcReport!.Domain!.Name })
            .Select(g => new
            {
                g.Key.SourceIp,
                g.Key.DomainName,
                Messages = g.Sum(x => (long)x.MessageCount),
            })
            .ToListAsync(ct);

        var byIp = failing
            .Where(x => x.Messages > 0)
            .GroupBy(x => x.SourceIp, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Ip = g.First().SourceIp,
                Messages = g.Sum(x => x.Messages),
                Domains = g.OrderByDescending(x => x.Messages).ThenBy(x => x.DomainName, StringComparer.Ordinal)
                    .Select(x => x.DomainName).ToList(),
            })
            .ToList();
        var top = byIp
            .OrderByDescending(x => x.Messages)
            .ThenBy(x => x.Ip, StringComparer.Ordinal)
            .Take(TopSources)
            .ToList();
        if (top.Count == 0)
        {
            return (byIp.Count, []);
        }

        var ips = top.Select(x => x.Ip).ToList();
        var lookbackFrom = fromUtc.AddDays(-NewSourceLookbackDays);
        var seenBefore = (await db.DmarcReportRecords.AsNoTracking()
                .Where(r => r.ReportRangeBeginUtc >= lookbackFrom && r.ReportRangeBeginUtc < fromUtc &&
                            ips.Contains(r.SourceIp) && domainIds.Contains(r.DmarcReport!.DomainId))
                .Select(r => r.SourceIp)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, string?> names;
        try
        {
            names = await hostnames.ResolveAsync(ips, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A digest without hostnames is still a digest.
            logger.LogWarning(ex, "Reverse DNS for the digest's failing sources failed");
            names = new Dictionary<string, string?>();
        }

        return (byIp.Count, top
            .Select(x => new DigestSource(
                x.Ip,
                names.GetValueOrDefault(x.Ip),
                x.Domains,
                x.Messages,
                !seenBefore.Contains(x.Ip)))
            .ToList());
    }

    /// <inheritdoc />
    public async Task<DigestMail?> PreviewClientAsync(Guid clientId, DateTime periodStartUtc, CancellationToken ct)
    {
        var summary = await BuildAsync(clientId, periodStartUtc, periodStartUtc.AddMonths(1), ct);
        return summary is null ? null : renderer.RenderClient(summary);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DigestMail>?> PreviewRecipientAsync(
        Guid recipientId, DateTime periodStartUtc, CancellationToken ct)
    {
        var recipient = await db.NotificationRecipients.AsNoTracking()
            .Where(r => r.Id == recipientId)
            .Select(r => new { r.Email })
            .SingleOrDefaultAsync(ct);
        if (recipient is null)
        {
            return null;
        }

        // Preview what this one row asks for, not the merge with other rows sharing the
        // address — the operator is looking at this row's settings.
        var clients = await DigestClientsAsync(ct);
        var coverage = (await NotificationRouting.LoadAsync(db, "digest", clients.Keys.ToList(), ct, recipientId))
            .FirstOrDefault();
        if (coverage is null)
        {
            return [];
        }

        var end = periodStartUtc.AddMonths(1);
        var cache = new Dictionary<Guid, DigestSummary>();
        var mails = new List<DigestMail>();
        foreach (var plan in Plan(coverage, clients))
        {
            mails.Add(await RenderAsync(plan, periodStartUtc, end, cache, ct));
        }
        return mails;
    }

    /// <inheritdoc />
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

        var clients = await DigestClientsAsync(ct);
        var coverage = await NotificationRouting.LoadAsync(db, "digest", clients.Keys.ToList(), ct);

        var delivered = await db.DigestDeliveries.AsNoTracking()
            .Where(d => d.PeriodStartUtc == periodStart)
            .Select(d => new { d.RecipientEmail, d.ClientId, d.CoveredClientIds })
            .ToListAsync(ct);
        // Rows from before per-recipient digests: that client's month went to everyone.
        var sentToEveryone = delivered
            .Where(d => d.RecipientEmail is null && d.ClientId is not null)
            .Select(d => d.ClientId!.Value)
            .ToHashSet();
        var sentTo = delivered
            .Where(d => d.RecipientEmail is not null)
            .Select(d => (d.RecipientEmail!.ToLowerInvariant(), d.ClientId))
            .ToHashSet();
        // Per address, every client it has already had this month in any mail — so a
        // client moved between roll-up and separate after sending is not mailed twice.
        var coveredFor = delivered
            .Where(d => d.RecipientEmail is not null)
            .GroupBy(d => d.RecipientEmail!.ToLowerInvariant())
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(d => d.ClientId is { } id ? d.CoveredClientIds.Append(id) : d.CoveredClientIds)
                    .ToHashSet());

        var cache = new Dictionary<Guid, DigestSummary>();
        var sent = 0;
        var skipped = 0;
        var sentLog = new List<string>();

        foreach (var recipient in coverage)
        {
            var alreadySent = coveredFor.TryGetValue(recipient.Email, out var covered)
                ? covered.Union(sentToEveryone).ToHashSet()
                : sentToEveryone;
            foreach (var plan in Plan(recipient, clients, alreadySent))
            {
                ct.ThrowIfCancellationRequested();

                var key = plan.IsRollup ? (Guid?)null : plan.ClientIds[0];
                if (sentTo.Contains((recipient.Email, key)))
                {
                    skipped++;
                    continue;
                }

                var mail = await RenderAsync(plan, periodStart, periodEnd, cache, ct);

                // Claimed before the send, so a second pass that races this one fails on
                // the unique index instead of mailing the same month twice.
                var row = new DigestDelivery
                {
                    ClientId = key,
                    RecipientEmail = recipient.Email,
                    ClientCount = plan.ClientIds.Count,
                    CoveredClientIds = [.. plan.ClientIds],
                    PeriodStartUtc = periodStart,
                    PeriodEndUtc = periodEnd,
                    SentAtUtc = DateTime.UtcNow,
                    RecipientCount = 0,
                };
                db.DigestDeliveries.Add(row);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    db.Entry(row).State = EntityState.Detached;
                    skipped++;
                    continue;
                }

                // A failed send still leaves the row with RecipientCount 0: without it a
                // broken relay would retry the same month on every pass.
                if (await email.SendAsync([recipient.Email], mail.Subject, mail.Text, mail.Html, ct))
                {
                    row.RecipientCount = 1;
                    row.SentAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    sent++;
                    sentLog.Add($"{recipient.Email}: {mail.Subject}");
                }
            }
        }

        if (sent > 0 || skipped > 0)
        {
            logger.LogInformation(
                "Digest for {Period:yyyy-MM}: sent {Sent}, skipped {Skipped}",
                periodStart, sent, skipped);
        }

        return new DigestSendResult(clients.Count, sent, skipped, sentLog);
    }

    /// <summary>Active clients with at least one active domain, by id. A client with nothing to monitor gets no digest.</summary>
    private async Task<Dictionary<Guid, string>> DigestClientsAsync(CancellationToken ct)
        => await db.Clients.AsNoTracking()
            .Where(c => c.IsActive && db.Domains.Any(d => d.ClientId == c.Id && d.IsActive))
            .OrderBy(c => c.Name)
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

    /// <summary>One planned mail: the clients it covers, in display order.</summary>
    private sealed record MailPlan(IReadOnlyList<Guid> ClientIds, bool IsRollup);

    /// <summary>
    /// The mails one address gets: a roll-up of its roll-up clients and one mail per
    /// separate client. A roll-up of a single client is sent as that client's own mail
    /// — a roll-up's overview table earns nothing for one row. Clients in
    /// <paramref name="alreadySent"/> are left out.
    /// </summary>
    private static IEnumerable<MailPlan> Plan(
        RecipientCoverage coverage, Dictionary<Guid, string> clients, IReadOnlySet<Guid>? alreadySent = null)
    {
        IEnumerable<Guid> Ordered(string mode) => coverage.Clients(mode)
            .Where(id => clients.ContainsKey(id) && alreadySent?.Contains(id) != true)
            .OrderBy(id => clients[id], StringComparer.OrdinalIgnoreCase);

        var rollup = Ordered(DigestModes.Rollup).ToList();
        var separate = Ordered(DigestModes.Separate).ToList();
        if (rollup.Count == 1)
        {
            separate.Add(rollup[0]);
            rollup.Clear();
        }

        if (rollup.Count > 1)
        {
            yield return new MailPlan(rollup, true);
        }

        foreach (var id in separate.OrderBy(id => clients[id], StringComparer.OrdinalIgnoreCase))
        {
            yield return new MailPlan([id], false);
        }
    }

    private async Task<DigestMail> RenderAsync(
        MailPlan plan, DateTime start, DateTime end, Dictionary<Guid, DigestSummary> cache, CancellationToken ct)
    {
        var summaries = new List<DigestSummary>();
        foreach (var id in plan.ClientIds)
        {
            if (!cache.TryGetValue(id, out var summary))
            {
                summary = await BuildAsync(id, start, end, ct)
                    ?? throw new InvalidOperationException($"Client {id} disappeared while the digest was being built.");
                cache[id] = summary;
            }
            summaries.Add(summary);
        }

        return plan.IsRollup ? renderer.RenderRollup(start, summaries) : renderer.RenderClient(summaries[0]);
    }

    /// <summary>The instance defaults with a client's overrides laid over them.</summary>
    private EffectiveThresholds Effective(DigestThresholds? o) => new(
        o?.LowCompliancePercent ?? _options.LowCompliancePercent,
        o?.ComplianceDropPoints ?? _options.ComplianceDropPoints,
        Math.Max(0, o?.MinMessages ?? _options.MinMessages),
        o?.NoReports ?? _options.NoReports,
        Math.Max(0, o?.TightenAfterDays ?? _options.TightenAfterDays),
        o?.TightenCompliancePercent ?? _options.TightenCompliancePercent);

    private sealed record EffectiveThresholds(
        double LowCompliancePercent,
        double ComplianceDropPoints,
        int MinMessages,
        bool NoReports,
        int TightenAfterDays,
        double TightenCompliancePercent);

    private static double Rate(long part, long total) => total == 0 ? 0 : (double)part / total;
}
