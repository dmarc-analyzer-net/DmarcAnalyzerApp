namespace DmarcAnalyzer.Api.Application.Notifications;

/// <summary>What a finding is about. The first three are problems; <see cref="ReadyToTighten"/> is a suggestion.</summary>
public enum DigestFindingKind
{
    LowCompliance,
    ComplianceDrop,
    NoReports,
    ReadyToTighten,
}

/// <summary>One thing the digest wants a reader to notice about one domain.</summary>
/// <param name="Summary">What was seen, in a sentence.</param>
/// <param name="Advice">What to do about it, in a sentence.</param>
public sealed record DigestFinding(DigestFindingKind Kind, Guid DomainId, string Domain, string Summary, string Advice)
{
    public bool IsProblem => Kind != DigestFindingKind.ReadyToTighten;
}

/// <summary>One domain's month.</summary>
/// <param name="Policy">The effective policy — the DNS cache when it has one, else the newest report's — or <c>missing</c> / <c>unknown</c>.</param>
/// <param name="PreviousComplianceRate">Null when the previous month had no messages to compare against.</param>
public sealed record DigestDomain(
    Guid DomainId,
    string Name,
    string Policy,
    long Messages,
    long CompliantMessages,
    double ComplianceRate,
    double? PreviousComplianceRate,
    bool HasProblem);

/// <summary>A source IP that failed DMARC for the client's domains.</summary>
/// <param name="Domains">The domains it failed for, most failing messages first.</param>
/// <param name="IsNew">Not seen sending for any of the client's domains in the 90 days before the month.</param>
public sealed record DigestSource(string Ip, string? Hostname, IReadOnlyList<string> Domains, long FailingMessages, bool IsNew)
{
    /// <summary>"shop.example" or "shop.example and 2 more".</summary>
    public string DomainSummary => Domains.Count switch
    {
        0 => string.Empty,
        1 => Domains[0],
        _ => $"{Domains[0]} and {Domains.Count - 1} more",
    };
}

/// <summary>An alert raised during the month.</summary>
public sealed record DigestAlert(string Severity, string Title, DateTime DetectedAtUtc);

/// <summary>Everything one client's digest says, computed before rendering.</summary>
public sealed record DigestSummary(
    Guid ClientId,
    string ClientName,
    DateTime PeriodStartUtc,
    DateTime PeriodEndUtc,
    int Domains,
    int DomainsEnforcing,
    long Messages,
    long CompliantMessages,
    double ComplianceRate,
    double? PreviousComplianceRate,
    int FailingSources,
    IReadOnlyList<DigestDomain> DomainLines,
    IReadOnlyList<DigestFinding> Findings,
    IReadOnlyList<DigestSource> TopFailingSources,
    IReadOnlyList<DigestAlert> Alerts)
{
    public long FailingMessages => Messages - CompliantMessages;
    public int Problems => Findings.Count(f => f.IsProblem);
    public int Suggestions => Findings.Count(f => !f.IsProblem);
}

/// <summary>A rendered digest mail.</summary>
/// <param name="ClientIds">The clients it covers: one for a client digest, several for a roll-up.</param>
public sealed record DigestMail(string Subject, string Text, string Html, IReadOnlyList<Guid> ClientIds)
{
    public bool IsRollup => ClientIds.Count > 1;
}

/// <summary>How a send pass went; Skipped counts mails already sent for the period.</summary>
/// <param name="SentTo">One entry per mail sent, as "address: subject".</param>
public sealed record DigestSendResult(int ClientsConsidered, int Sent, int Skipped, IReadOnlyList<string> SentTo);
