namespace DmarcAnalyzer.Api.Application.Notifications;

/// <summary>Alert evaluation defaults (`Alerts:*`). Clients may override the thresholds.</summary>
public sealed class AlertOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How often the worker evaluates alerts.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Compliance drop, in percentage points, between the baseline and the latest
    /// day of data before a failure spike is raised.
    /// </summary>
    public int ComplianceDropPercent { get; set; } = 15;

    /// <summary>
    /// Days quieter than this are ignored — on a low-volume domain a handful of
    /// failures is noise, not a spike.
    /// </summary>
    public int MinMessages { get; set; } = 100;

    /// <summary>Days of history used as the comparison baseline.</summary>
    public int BaselineDays { get; set; } = 7;

    /// <summary>Suppress repeat alerts of the same type for the same subject this long.</summary>
    public int CooldownHours { get; set; } = 24;
}
