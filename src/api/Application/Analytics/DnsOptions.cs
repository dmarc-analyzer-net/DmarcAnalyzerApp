namespace DmarcAnalyzer.Api.Application.Analytics;

/// <summary>Controls the worker pass that refreshes each domain's cached DMARC policy.</summary>
public sealed class DnsOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Floor freshness for every active domain. Detail-page views correct individual
    /// domains sooner, but this is what keeps domains nobody opens from going stale —
    /// which is exactly the case the cache exists to get right.
    /// </summary>
    public int RefreshIntervalHours { get; set; } = 6;
}
