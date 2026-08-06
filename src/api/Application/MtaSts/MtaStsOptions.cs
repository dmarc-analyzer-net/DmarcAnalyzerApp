namespace DmarcAnalyzer.Api.Application.MtaSts;

/// <summary>Controls the worker pass that checks each domain's MTA-STS posture.</summary>
public sealed class MtaStsOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Floor freshness for every active domain, like the DNS refresh. Domains
    /// without an MTA-STS record cost one TXT query per interval; only domains
    /// that publish one get the policy fetch and MX lookup on top.
    /// </summary>
    public int CheckIntervalHours { get; set; } = 6;

    /// <summary>Total budget for one policy-file fetch, connect included.</summary>
    public int FetchTimeoutSeconds { get; set; } = 10;

    /// <summary>How many domains are checked concurrently during a pass.</summary>
    public int MaxConcurrentChecks { get; set; } = 4;

    /// <summary>
    /// Whether the policy fetch may connect to loopback/private/link-local
    /// addresses. Off by default: mta-sts hostnames derive from operator-entered
    /// domains, and an internet-facing instance should not be steerable into its
    /// own network. Turn on for instances that monitor intranet mail domains.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }
}
