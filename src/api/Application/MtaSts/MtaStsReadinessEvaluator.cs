namespace DmarcAnalyzer.Api.Application.MtaSts;

/// <summary>Everything the gate looks at, flattened so the evaluator stays pure.</summary>
public sealed record MtaStsReadinessInput(
    bool PolicyEnabled,
    string Mode,
    DateTime? ModeChangedAtUtc,
    bool StateChecked,
    bool? TxtOk,
    bool? FetchOk,
    bool? PolicyValid,
    bool? MxMatchOk,
    long TotalSessions,
    long StsFailureSessions,
    int ReportCount,
    DateTime NowUtc);

public sealed record MtaStsReadinessCheckDto(string Name, string Status, string? Detail);

/// <summary>
/// Whether a hosted policy in testing is safe to promote to enforce.
/// EvidenceBasis says what the verdict rests on: tls_rpt (reporters covered the
/// window and saw no STS failures) or time_in_testing (nobody reports on this
/// domain, so clean checks over a longer clock stand in). Null Readiness on the
/// wire means the domain hosts no policy here — nothing to promote.
/// </summary>
public sealed record MtaStsReadinessDto(
    string Status,
    string? BlockedReason,
    string EvidenceBasis,
    int? DaysInTesting,
    int GateWindowDays,
    long TotalSessions,
    long StsFailureSessions,
    int ReportCount,
    IReadOnlyList<MtaStsReadinessCheckDto> Checks);

public static class MtaStsReadinessStatus
{
    public const string Ready = "ready";
    public const string NotReady = "not_ready";
    public const string InsufficientData = "insufficient_data";
    public const string NotApplicable = "not_applicable";
}

public static class MtaStsReadinessEvidence
{
    public const string TlsRpt = "tls_rpt";
    public const string TimeInTesting = "time_in_testing";
    public const string None = "none";
}

/// <summary>
/// The testing→enforce gate, pure so the whole state machine is a theory table.
///
/// Windows here are wall-clock, deliberately unlike the analytics panels'
/// data-anchored windows: promotion is a decision about *now*, and anchoring to
/// stale data would let a domain whose reports stopped months ago look green.
///
/// Thresholds are constants rather than options: they are judgement defaults,
/// not deployment knobs, and every option costs a configuration.md row and a
/// contract-test entry. Promotable later if real installs disagree.
/// </summary>
public static class MtaStsReadinessEvaluator
{
    /// <summary>How far back TLS-RPT evidence counts.</summary>
    public const int GateWindowDays = 14;

    /// <summary>Minimum clean time in testing when reporters cover the domain.</summary>
    public const int MinDaysInTesting = 14;

    /// <summary>
    /// The no-reporter fallback: many domains never attract TLS-RPT reports at
    /// all, and blocking forever on absent evidence would make the gate useless.
    /// Twice the observed floor, and the DTO says this basis was used.
    /// </summary>
    public const int NoDataMinDaysInTesting = 28;

    public static MtaStsReadinessDto Evaluate(MtaStsReadinessInput input)
    {
        var daysInTesting = input.ModeChangedAtUtc is { } since
            ? (int)Math.Floor((input.NowUtc - since).TotalDays)
            : (int?)null;

        var checks = BuildChecks(input);

        if (string.Equals(input.Mode, "enforce", StringComparison.OrdinalIgnoreCase))
        {
            return Result(MtaStsReadinessStatus.NotApplicable, null, MtaStsReadinessEvidence.None,
                daysInTesting, input, checks);
        }

        if (!input.PolicyEnabled || !string.Equals(input.Mode, "testing", StringComparison.OrdinalIgnoreCase))
        {
            return Result(MtaStsReadinessStatus.NotReady,
                input.PolicyEnabled
                    ? "The policy is not in testing mode — start there and let evidence accumulate."
                    : "Hosting is off — senders cannot fetch the policy, so nothing is being tested.",
                MtaStsReadinessEvidence.None, daysInTesting, input, checks);
        }

        var failing = checks.Where(c => c.Status == "fail").Select(c => c.Name).ToArray();
        if (failing.Length > 0)
        {
            return Result(MtaStsReadinessStatus.NotReady,
                $"Monitoring checks failing: {string.Join(", ", failing)}. Enforce would turn these into refused deliveries.",
                MtaStsReadinessEvidence.None, daysInTesting, input, checks);
        }

        if (!input.StateChecked || checks.Any(c => c.Status == "unknown"))
        {
            return Result(MtaStsReadinessStatus.InsufficientData,
                "The monitoring pass has not verified this policy yet.",
                MtaStsReadinessEvidence.None, daysInTesting, input, checks);
        }

        if (input.StsFailureSessions > 0)
        {
            return Result(MtaStsReadinessStatus.NotReady,
                $"{input.StsFailureSessions} STS-category failure session(s) reported in the last " +
                $"{GateWindowDays} days — senders are already tripping over this policy in testing.",
                MtaStsReadinessEvidence.TlsRpt, daysInTesting, input, checks);
        }

        if (input.ReportCount == 0)
        {
            // No reporter covers this domain. Transport-category failures can't
            // block either (there are none to see) — time and green checks are
            // the only evidence available.
            return daysInTesting >= NoDataMinDaysInTesting
                ? Result(MtaStsReadinessStatus.Ready, null,
                    MtaStsReadinessEvidence.TimeInTesting, daysInTesting, input, checks)
                : Result(MtaStsReadinessStatus.InsufficientData,
                    $"No TLS reports received; day {Math.Max(0, daysInTesting ?? 0)} of " +
                    $"{NoDataMinDaysInTesting} in testing without them.",
                    MtaStsReadinessEvidence.TimeInTesting, daysInTesting, input, checks);
        }

        return daysInTesting >= MinDaysInTesting
            ? Result(MtaStsReadinessStatus.Ready, null,
                MtaStsReadinessEvidence.TlsRpt, daysInTesting, input, checks)
            : Result(MtaStsReadinessStatus.InsufficientData,
                $"Reporters see no STS failures, but the policy has only been in testing " +
                $"{Math.Max(0, daysInTesting ?? 0)} of {MinDaysInTesting} days.",
                MtaStsReadinessEvidence.TlsRpt, daysInTesting, input, checks);
    }

    private static IReadOnlyList<MtaStsReadinessCheckDto> BuildChecks(MtaStsReadinessInput input) =>
    [
        Check("TXT record", input.TxtOk, "the _mta-sts record senders discover the policy through"),
        Check("policy fetch", input.FetchOk, "HTTPS reachability and certificate of the policy host"),
        Check("policy syntax", input.PolicyValid, "whether senders can parse what is served"),
        Check("MX coverage", input.MxMatchOk, "every live MX matched by an mx pattern"),
    ];

    private static MtaStsReadinessCheckDto Check(string name, bool? ok, string detail)
        => new(name, ok switch { true => "pass", false => "fail", null => "unknown" }, detail);

    private static MtaStsReadinessDto Result(
        string status, string? blockedReason, string evidence, int? daysInTesting,
        MtaStsReadinessInput input, IReadOnlyList<MtaStsReadinessCheckDto> checks)
        => new(status, blockedReason, evidence, daysInTesting, GateWindowDays,
            input.TotalSessions, input.StsFailureSessions, input.ReportCount, checks);
}
