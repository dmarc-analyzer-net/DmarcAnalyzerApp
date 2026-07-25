namespace DmarcAnalyzer.Api.Application.Notifications;

/// <summary>Monthly digest settings (`Digest:*`).</summary>
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
}
