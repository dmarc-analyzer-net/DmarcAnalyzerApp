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

    /// <summary>
    /// The hostname client CNAMEs point at for hosted policies — what the
    /// console shows as the CNAME target in publish instructions. Empty means
    /// the console shows a hint to configure it instead. Serving itself never
    /// reads this; it keys on the request's Host header.
    /// </summary>
    public string PolicyHost { get; set; } = string.Empty;

    /// <summary>
    /// In-memory TTL for served policy bodies, and the Cache-Control max-age on
    /// the public endpoint. Also the propagation bound for a dedicated
    /// APP_MODE=mta-sts container after a console edit — negligible against
    /// max_age values measured in days.
    /// </summary>
    public int ServeCacheSeconds { get; set; } = 60;
}
