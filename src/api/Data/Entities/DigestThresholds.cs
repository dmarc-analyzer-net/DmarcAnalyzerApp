using System.Text.Json.Serialization;

namespace DmarcAnalyzer.Api.Data.Entities;

/// <summary>
/// A client's overrides for what the monthly digest calls "needs attention". Every
/// property is nullable: null inherits the instance default (<c>Digest:*</c>), and for
/// the numeric triggers 0 switches the trigger off for this client. Stored as one JSON
/// column on <see cref="Client"/>, so a new trigger is a property here rather than a
/// migration.
/// </summary>
public sealed record DigestThresholds
{
    /// <summary>Flag a domain whose aligned pass rate is below this percentage. 0 turns the trigger off.</summary>
    public double? LowCompliancePercent { get; init; }

    /// <summary>Flag a domain whose pass rate fell by at least this many points on the previous month. 0 turns it off.</summary>
    public double? ComplianceDropPoints { get; init; }

    /// <summary>Domains with fewer messages in the month are not judged on their rate at all.</summary>
    public int? MinMessages { get; init; }

    /// <summary>Flag a domain whose reports stopped: traffic the month before, none this month.</summary>
    public bool? NoReports { get; init; }

    /// <summary>Suggest a stricter policy after this many days at or above <see cref="TightenCompliancePercent"/>. 0 turns it off.</summary>
    public int? TightenAfterDays { get; init; }

    /// <summary>The pass rate a domain must hold for <see cref="TightenAfterDays"/> before a stricter policy is suggested.</summary>
    public double? TightenCompliancePercent { get; init; }

    /// <summary>True when nothing is overridden, so the column can be stored as null.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        LowCompliancePercent is null && ComplianceDropPoints is null && MinMessages is null &&
        NoReports is null && TightenAfterDays is null && TightenCompliancePercent is null;
}
