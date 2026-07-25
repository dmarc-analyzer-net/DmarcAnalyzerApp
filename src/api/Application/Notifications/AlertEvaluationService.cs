using DmarcAnalyzer.Api.Data;
using DmarcAnalyzer.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DmarcAnalyzer.Api.Application.Notifications;

public static class AlertRuleTypes
{
    public const string FailureSpike = "failure_spike";
    public const string PolicyRegression = "policy_regression";
}

public sealed record AlertEvaluationResult(
    int ClientsEvaluated,
    int AlertsRaised,
    int Suppressed,
    int EmailsSent,
    IReadOnlyList<string> Raised);

public interface IAlertEvaluationService
{
    Task<AlertEvaluationResult> EvaluateAsync(CancellationToken ct);
}

/// <summary>
/// Raises alerts for the two conditions operators actually need to hear about
/// without watching a dashboard:
///
/// <list type="bullet">
/// <item><b>failure spike</b> — the newest day of data is markedly less compliant
/// than the preceding baseline, i.e. something broke or someone started
/// spoofing.</item>
/// <item><b>policy regression</b> — a domain's published DMARC policy got
/// weaker, which silently removes protection and is easy to do by accident when
/// editing DNS.</item>
/// </list>
///
/// Both compare against report data rather than wall-clock time, because reports
/// arrive daily and a backfill can deliver old data at any moment.
/// </summary>
public sealed class AlertEvaluationService(
    DmarcAnalyzerDbContext db,
    IEmailSender email,
    IOptions<AlertOptions> alertOptions,
    IOptions<EmailOptions> emailOptions,
    ILogger<AlertEvaluationService> logger) : IAlertEvaluationService
{
    private readonly AlertOptions _options = alertOptions.Value;
    private readonly EmailOptions _email = emailOptions.Value;

    public async Task<AlertEvaluationResult> EvaluateAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return new AlertEvaluationResult(0, 0, 0, 0, []);
        }

        var clients = await db.Clients
            .AsNoTracking()
            .Where(c => c.IsActive && c.AlertsEnabled)
            .Select(c => new
            {
                c.Id, c.Name,
                DropPercent = c.AlertComplianceDropPercent,
                MinMessages = c.AlertMinMessages,
            })
            .ToListAsync(ct);

        var raised = new List<AlertEvent>();
        var suppressed = 0;

        foreach (var client in clients)
        {
            ct.ThrowIfCancellationRequested();

            var dropThreshold = client.DropPercent ?? _options.ComplianceDropPercent;
            var minMessages = client.MinMessages ?? _options.MinMessages;

            var domains = await db.Domains
                .AsNoTracking()
                .Where(d => d.ClientId == client.Id && d.IsActive)
                .Select(d => new { d.Id, d.Name })
                .ToListAsync(ct);

            foreach (var domain in domains)
            {
                foreach (var candidate in new[]
                         {
                             await EvaluateFailureSpikeAsync(client.Id, client.Name, domain.Id, domain.Name,
                                 dropThreshold, minMessages, ct),
                             await EvaluatePolicyRegressionAsync(client.Id, client.Name, domain.Id, domain.Name, ct),
                         })
                {
                    if (candidate is null)
                    {
                        continue;
                    }

                    if (await IsInCooldownAsync(candidate, ct))
                    {
                        suppressed++;
                        continue;
                    }

                    db.AlertEvents.Add(candidate);
                    raised.Add(candidate);
                }
            }
        }

        if (raised.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        var emailsSent = await NotifyAsync(raised, ct);

        if (raised.Count > 0 || suppressed > 0)
        {
            logger.LogInformation(
                "Alert evaluation: raised {Raised}, suppressed {Suppressed} by cooldown, {Emails} email(s) sent",
                raised.Count, suppressed, emailsSent);
        }

        return new AlertEvaluationResult(
            clients.Count, raised.Count, suppressed, emailsSent,
            raised.Select(r => r.Title).ToArray());
    }

    /// <summary>
    /// Compares the newest day of report data against the mean compliance of the
    /// preceding <c>BaselineDays</c>. Anchored to the newest report rather than
    /// today, so a domain whose reports lag doesn't look like a total outage.
    /// </summary>
    private async Task<AlertEvent?> EvaluateFailureSpikeAsync(
        Guid clientId, string clientName, Guid domainId, string domainName,
        int dropThreshold, int minMessages, CancellationToken ct)
    {
        var daily = await db.DmarcReportRecords
            .AsNoTracking()
            .Where(r => r.DmarcReport!.DomainId == domainId)
            .Select(r => new
            {
                Day = r.DmarcReport!.RangeBeginUtc.Date,
                r.MessageCount,
                r.DkimResult,
                r.SpfResult,
            })
            .GroupBy(r => r.Day)
            .Select(g => new
            {
                Day = g.Key,
                Messages = g.Sum(x => (long)x.MessageCount),
                Compliant = g.Sum(x => x.DkimResult == "pass" || x.SpfResult == "pass" ? (long)x.MessageCount : 0L),
            })
            .OrderByDescending(x => x.Day)
            .Take(_options.BaselineDays + 1)
            .ToListAsync(ct);

        // Need a latest day plus at least one baseline day to say anything.
        if (daily.Count < 2)
        {
            return null;
        }

        var latest = daily[0];
        if (latest.Messages < minMessages)
        {
            return null;
        }

        var baseline = daily.Skip(1).ToList();
        var baselineMessages = baseline.Sum(x => x.Messages);
        if (baselineMessages == 0)
        {
            return null;
        }

        var latestRate = (double)latest.Compliant / latest.Messages;
        var baselineRate = (double)baseline.Sum(x => x.Compliant) / baselineMessages;
        var dropPoints = (baselineRate - latestRate) * 100;

        if (dropPoints < dropThreshold)
        {
            return null;
        }

        var failed = latest.Messages - latest.Compliant;
        return new AlertEvent
        {
            ClientId = clientId,
            DomainId = domainId,
            RuleType = AlertRuleTypes.FailureSpike,
            Severity = dropPoints >= dropThreshold * 2 ? "critical" : "warning",
            Title = $"DMARC compliance dropped {dropPoints:F0} points for {domainName}",
            Details =
                $"On {latest.Day:yyyy-MM-dd}, {latestRate * 100:F1}% of {latest.Messages:N0} messages for " +
                $"{domainName} were DMARC-compliant, against a {baselineRate * 100:F1}% baseline over the " +
                $"previous {baseline.Count} day(s) with data. {failed:N0} message(s) failed. " +
                $"Client: {clientName}.",
            DetectedAtUtc = DateTime.UtcNow,
        };
    }

    private static readonly Dictionary<string, int> PolicyStrength = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = 0,
        ["quarantine"] = 1,
        ["reject"] = 2,
    };

    /// <summary>
    /// Looks at the published policy on the two newest reports that disagree. A
    /// move to a weaker policy means protection was removed — usually an
    /// accidental DNS edit.
    /// </summary>
    private async Task<AlertEvent?> EvaluatePolicyRegressionAsync(
        Guid clientId, string clientName, Guid domainId, string domainName, CancellationToken ct)
    {
        var policies = await db.DmarcReports
            .AsNoTracking()
            .Where(r => r.DomainId == domainId)
            .OrderByDescending(r => r.RangeEndUtc)
            .ThenByDescending(r => r.IngestedAtUtc)
            .Select(r => new { r.PublishedPolicy, r.RangeEndUtc })
            .Take(50)
            .ToListAsync(ct);

        if (policies.Count < 2)
        {
            return null;
        }

        var current = policies[0].PublishedPolicy;
        var previous = policies.FirstOrDefault(p =>
            !string.Equals(p.PublishedPolicy, current, StringComparison.OrdinalIgnoreCase));
        if (previous is null)
        {
            return null;
        }

        if (!PolicyStrength.TryGetValue(current, out var currentStrength) ||
            !PolicyStrength.TryGetValue(previous.PublishedPolicy, out var previousStrength) ||
            currentStrength >= previousStrength)
        {
            return null;
        }

        return new AlertEvent
        {
            ClientId = clientId,
            DomainId = domainId,
            RuleType = AlertRuleTypes.PolicyRegression,
            Severity = currentStrength == 0 ? "critical" : "warning",
            Title = $"DMARC policy weakened for {domainName}: p={previous.PublishedPolicy} → p={current}",
            Details =
                $"Reporters observed p={current} for {domainName} as of " +
                $"{policies[0].RangeEndUtc:yyyy-MM-dd}, down from p={previous.PublishedPolicy} on " +
                $"{previous.RangeEndUtc:yyyy-MM-dd}. Weakening the policy reduces protection against " +
                $"spoofing — check whether the DNS record was changed intentionally. Client: {clientName}.",
            DetectedAtUtc = DateTime.UtcNow,
        };
    }

    /// <summary>True if the same alert was already raised for this subject recently.</summary>
    private async Task<bool> IsInCooldownAsync(AlertEvent candidate, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Max(1, _options.CooldownHours));
        return await db.AlertEvents
            .AsNoTracking()
            .AnyAsync(e =>
                e.ClientId == candidate.ClientId &&
                e.DomainId == candidate.DomainId &&
                e.RuleType == candidate.RuleType &&
                e.DetectedAtUtc >= since, ct);
    }

    /// <summary>
    /// Emails each client's alert recipients (plus any agency-wide ones), grouping
    /// a client's alerts into a single message rather than one per alert.
    /// </summary>
    private async Task<int> NotifyAsync(List<AlertEvent> raised, CancellationToken ct)
    {
        if (raised.Count == 0)
        {
            return 0;
        }

        var recipients = await db.NotificationRecipients
            .AsNoTracking()
            .Where(r => r.IsActive && (r.Kind == "alert" || r.Kind == "both"))
            .Select(r => new { r.ClientId, r.Email })
            .ToListAsync(ct);

        var agencyWide = recipients.Where(r => r.ClientId is null).Select(r => r.Email).ToList();
        var sent = 0;

        foreach (var group in raised.GroupBy(r => r.ClientId))
        {
            var to = recipients
                .Where(r => r.ClientId == group.Key)
                .Select(r => r.Email)
                .Concat(agencyWide)
                .Distinct()
                .ToList();

            if (to.Count == 0)
            {
                continue;
            }

            var alerts = group.ToList();
            var subject = alerts.Count == 1
                ? $"[DMARC] {alerts[0].Title}"
                : $"[DMARC] {alerts.Count} new alerts";

            var body = new System.Text.StringBuilder();
            foreach (var alert in alerts)
            {
                body.AppendLine(alert.Severity.ToUpperInvariant() + ": " + alert.Title);
                body.AppendLine(alert.Details);
                if (!string.IsNullOrWhiteSpace(_email.BaseUrl) && alert.DomainId is { } domainId)
                {
                    body.AppendLine($"{_email.BaseUrl.TrimEnd('/')}/domains/{domainId}");
                }
                body.AppendLine();
            }

            if (await email.SendAsync(to, subject, body.ToString(), ct))
            {
                var now = DateTime.UtcNow;
                foreach (var alert in alerts)
                {
                    alert.NotifiedAtUtc = now;
                }
                sent++;
            }
        }

        if (sent > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return sent;
    }
}
