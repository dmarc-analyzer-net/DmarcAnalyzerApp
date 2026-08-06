using DmarcAnalyzer.Api.Application.MtaSts;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The testing→enforce state machine, boundary days included. The asymmetry the
/// rules encode: a false not-ready costs patience, a false ready breaks mail.
/// </summary>
public sealed class MtaStsReadinessEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    private static MtaStsReadinessInput Input(
        bool enabled = true,
        string mode = "testing",
        int? daysInTesting = 20,
        bool stateChecked = true,
        bool? txtOk = true,
        bool? fetchOk = true,
        bool? policyValid = true,
        bool? mxMatchOk = true,
        long totalSessions = 500,
        long stsFailures = 0,
        int reportCount = 5)
        => new(enabled, mode,
            daysInTesting is { } d ? Now.AddDays(-d) : null,
            stateChecked, txtOk, fetchOk, policyValid, mxMatchOk,
            totalSessions, stsFailures, reportCount, Now);

    [Fact]
    public void EnforceMode_IsNotApplicable()
        => Assert.Equal(MtaStsReadinessStatus.NotApplicable,
            MtaStsReadinessEvaluator.Evaluate(Input(mode: "enforce")).Status);

    [Theory]
    [InlineData("none")]
    [InlineData("testing")]
    public void DisabledOrNonTestingPolicies_AreNotReady(string mode)
    {
        var disabled = MtaStsReadinessEvaluator.Evaluate(Input(enabled: false, mode: mode));
        Assert.Equal(MtaStsReadinessStatus.NotReady, disabled.Status);
        Assert.Contains("Hosting is off", disabled.BlockedReason);

        if (mode != "testing")
        {
            var wrongMode = MtaStsReadinessEvaluator.Evaluate(Input(mode: mode));
            Assert.Equal(MtaStsReadinessStatus.NotReady, wrongMode.Status);
            Assert.Contains("not in testing mode", wrongMode.BlockedReason);
        }
    }

    [Fact]
    public void FailingChecks_Block_AndAreNamed()
    {
        var result = MtaStsReadinessEvaluator.Evaluate(Input(fetchOk: false, mxMatchOk: false));

        Assert.Equal(MtaStsReadinessStatus.NotReady, result.Status);
        Assert.Contains("policy fetch", result.BlockedReason);
        Assert.Contains("MX coverage", result.BlockedReason);
        Assert.Equal(2, result.Checks.Count(c => c.Status == "fail"));
    }

    [Fact]
    public void NeverChecked_IsInsufficientData_NotFailure()
    {
        var result = MtaStsReadinessEvaluator.Evaluate(
            Input(stateChecked: false, txtOk: null, fetchOk: null, policyValid: null, mxMatchOk: null));

        Assert.Equal(MtaStsReadinessStatus.InsufficientData, result.Status);
        Assert.All(result.Checks, c => Assert.Equal("unknown", c.Status));
    }

    [Fact]
    public void StsFailuresInWindow_Block_EvenWithGreenChecks()
    {
        var result = MtaStsReadinessEvaluator.Evaluate(Input(stsFailures: 4));

        Assert.Equal(MtaStsReadinessStatus.NotReady, result.Status);
        Assert.Contains("4 STS-category", result.BlockedReason);
        Assert.Equal(MtaStsReadinessEvidence.TlsRpt, result.EvidenceBasis);
    }

    [Theory]
    [InlineData(13, MtaStsReadinessStatus.InsufficientData)]
    [InlineData(14, MtaStsReadinessStatus.Ready)]
    public void WithReporters_TheCleanClockIs14Days(int days, string expected)
    {
        var result = MtaStsReadinessEvaluator.Evaluate(Input(daysInTesting: days));

        Assert.Equal(expected, result.Status);
        Assert.Equal(MtaStsReadinessEvidence.TlsRpt, result.EvidenceBasis);
        Assert.Equal(days, result.DaysInTesting);
    }

    [Theory]
    [InlineData(27, MtaStsReadinessStatus.InsufficientData)]
    [InlineData(28, MtaStsReadinessStatus.Ready)]
    public void WithoutReporters_TheFallbackClockIs28Days(int days, string expected)
    {
        var result = MtaStsReadinessEvaluator.Evaluate(
            Input(daysInTesting: days, totalSessions: 0, reportCount: 0));

        Assert.Equal(expected, result.Status);
        // The verdict says out loud that no reporter vouched for it.
        Assert.Equal(MtaStsReadinessEvidence.TimeInTesting, result.EvidenceBasis);
    }

    [Fact]
    public void TransportFailures_NeverBlock()
    {
        // Reporters saw failures, but none in the sts bucket: those describe
        // receiver problems enforce doesn't change for STS-validated senders.
        var result = MtaStsReadinessEvaluator.Evaluate(
            Input(totalSessions: 1000, stsFailures: 0, reportCount: 3));

        Assert.Equal(MtaStsReadinessStatus.Ready, result.Status);
    }

    [Fact]
    public void MissingModeClock_ReadsAsInsufficient_NeverReady()
    {
        var result = MtaStsReadinessEvaluator.Evaluate(Input(daysInTesting: null));

        Assert.Equal(MtaStsReadinessStatus.InsufficientData, result.Status);
        Assert.Null(result.DaysInTesting);
    }
}
