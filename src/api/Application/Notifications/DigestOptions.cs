namespace DmarcAnalyzer.Api.Application.Notifications;

/// <summary>
/// Monthly digest settings (`Digest:*`). The attention triggers below are instance
/// defaults; a client can override any of them (<c>Client.DigestThresholds</c>).
/// </summary>
public sealed class DigestOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Day of the month (1–28) from which the previous month's digest may be sent.
    /// Waiting until at least the 1st means a digest always covers a whole month.
    /// </summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>How often the worker checks whether a digest is due.</summary>
    public int CheckIntervalHours { get; set; } = 6;

    /// <summary>Flag a domain whose aligned pass rate for the month is below this percentage. 0 turns the trigger off.</summary>
    public double LowCompliancePercent { get; set; } = 98;

    /// <summary>Flag a domain whose pass rate fell by at least this many points on the previous month. 0 turns it off.</summary>
    public double ComplianceDropPoints { get; set; } = 2;

    /// <summary>
    /// Domains with fewer messages than this in the month are not judged on their pass
    /// rate — on a quiet domain one forwarded message is a percentage point.
    /// </summary>
    public int MinMessages { get; set; } = 100;

    /// <summary>
    /// Flag a domain whose reports stopped: at least <see cref="MinMessages"/> reported the
    /// month before, none this month. A domain that is always quiet is not flagged.
    /// </summary>
    public bool NoReports { get; set; } = true;

    /// <summary>
    /// Suggest a stricter policy for a domain at <c>p=none</c> or <c>p=quarantine</c> that
    /// has held <see cref="TightenCompliancePercent"/> for at least this many days. 0 turns it off.
    /// </summary>
    public int TightenAfterDays { get; set; } = 60;

    /// <summary>The pass rate a domain must hold for <see cref="TightenAfterDays"/> before a stricter policy is suggested.</summary>
    public double TightenCompliancePercent { get; set; } = 98;
}
