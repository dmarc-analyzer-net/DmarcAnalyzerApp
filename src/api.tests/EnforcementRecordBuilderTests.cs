using DmarcAnalyzer.Api.Application.Analytics;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The record this builds is pasted into a DNS zone by hand. A tag dropped here is
/// a tag dropped in production, and a dropped `rua` is silent — the reports simply
/// stop. So these tests are mostly about what survives the rewrite, not what changes.
/// </summary>
public sealed class EnforcementRecordBuilderTests
{
    private const string Live =
        "v=DMARC1; p=none; rua=mailto:dmarc@acme.example; ruf=mailto:forensics@acme.example; " +
        "adkim=s; aspf=r; fo=1";

    [Fact]
    public void MovingToQuarantineInTestingMode_ChangesOnlyThePolicyTags()
    {
        var change = EnforcementRecordBuilder.Build("acme.example", Live, "quarantine", testing: true);

        Assert.NotNull(change);
        Assert.Equal("_dmarc.acme.example", change!.Host);
        Assert.Equal(Live, change.Current);
        Assert.Equal(
            "v=DMARC1; p=quarantine; t=y; rua=mailto:dmarc@acme.example; " +
            "ruf=mailto:forensics@acme.example; adkim=s; aspf=r; fo=1",
            change.Recommended);
        Assert.Equal(["p", "t"], change.ChangedTags);
    }

    /// <summary>
    /// The failure the issue was opened about: a composed-from-scratch record loses the
    /// reporting address, and nothing bounces to say the reports stopped.
    /// </summary>
    [Theory]
    [InlineData("rua=mailto:dmarc@acme.example")]
    [InlineData("ruf=mailto:forensics@acme.example")]
    [InlineData("adkim=s")]
    [InlineData("aspf=r")]
    [InlineData("fo=1")]
    public void EveryNonPolicyTag_SurvivesTheRewrite(string tag)
    {
        var change = EnforcementRecordBuilder.Build("acme.example", Live, "reject", testing: false);

        Assert.NotNull(change);
        Assert.Contains(tag, change!.Recommended);
    }

    /// <summary>sp is a policy tag but not one this step owns — the issue asks for it kept.</summary>
    [Fact]
    public void SubdomainPolicy_IsKept_AndTheGapIsCalledOut()
    {
        var change = EnforcementRecordBuilder.Build(
            "acme.example", "v=DMARC1; p=none; sp=none; rua=mailto:dmarc@acme.example", "quarantine", testing: false);

        Assert.NotNull(change);
        Assert.Equal("v=DMARC1; p=quarantine; sp=none; rua=mailto:dmarc@acme.example", change!.Recommended);
        Assert.Contains(change.Notes, n => n.Contains("sp=none is kept as published"));
    }

    [Fact]
    public void AnSpAtLeastAsStrongAsTheNewPolicy_RaisesNoNote()
    {
        var change = EnforcementRecordBuilder.Build(
            "acme.example", "v=DMARC1; p=none; sp=reject; rua=mailto:dmarc@acme.example", "quarantine", testing: false);

        Assert.NotNull(change);
        Assert.DoesNotContain(change!.Notes, n => n.Contains("sp="));
    }

    /// <summary>
    /// RFC 9989 Appendix A.6 removed pct. The record we hand over must never carry one,
    /// including when the live record still does — which is exactly the record an agency
    /// following the old advice is sitting on.
    /// </summary>
    [Fact]
    public void APublishedPct_IsRemoved_AndNeverReappears()
    {
        var change = EnforcementRecordBuilder.Build(
            "acme.example", "v=DMARC1; p=quarantine; pct=25; rua=mailto:dmarc@acme.example", "reject", testing: false);

        Assert.NotNull(change);
        Assert.Equal("v=DMARC1; p=reject; rua=mailto:dmarc@acme.example", change!.Recommended);
        Assert.DoesNotContain("pct", change.Recommended);
        Assert.Contains("pct", change.ChangedTags);
        Assert.Contains(change.Notes, n => n.Contains("RFC 9989 retired the tag"));
    }

    [Fact]
    public void LeavingTestingMode_DropsTheTestingTag()
    {
        var change = EnforcementRecordBuilder.Build(
            "acme.example", "v=DMARC1; p=quarantine; t=y; rua=mailto:dmarc@acme.example", "quarantine", testing: false);

        Assert.NotNull(change);
        Assert.Equal("v=DMARC1; p=quarantine; rua=mailto:dmarc@acme.example", change!.Recommended);
        Assert.Equal(["t"], change.ChangedTags);
    }

    /// <summary>t=n is the default already, so there is nothing to remove and no step to show.</summary>
    [Fact]
    public void AnExplicitTestingNo_IsLeftAlone()
    {
        var change = EnforcementRecordBuilder.Build(
            "acme.example", "v=DMARC1; p=reject; t=n; rua=mailto:dmarc@acme.example", "reject", testing: false);

        Assert.Null(change);
    }

    [Fact]
    public void ARecordWithNoPolicyTag_GainsOneAfterTheVersion()
    {
        var change = EnforcementRecordBuilder.Build(
            "acme.example", "v=DMARC1; rua=mailto:dmarc@acme.example", "none", testing: false);

        Assert.NotNull(change);
        Assert.Equal("v=DMARC1; p=none; rua=mailto:dmarc@acme.example", change!.Recommended);
    }

    [Fact]
    public void NonStandardSpacingAndCasing_AreNormalizedWithoutLosingTags()
    {
        var change = EnforcementRecordBuilder.Build(
            "acme.example", "v=DMARC1;P=none;RUA=mailto:dmarc@acme.example;", "quarantine", testing: false);

        Assert.NotNull(change);
        // The tag names stay as published — rewriting their case would be a second,
        // unexplained change in a record the operator is about to diff by eye.
        Assert.Equal("v=DMARC1; P=quarantine; RUA=mailto:dmarc@acme.example", change!.Recommended);
    }

    [Fact]
    public void AStepThatChangesNothing_ProducesNoRecord()
    {
        Assert.Null(EnforcementRecordBuilder.Build("acme.example", Live, "none", testing: false));
    }

    /// <summary>
    /// A retired pct= is worth removing but is not a *step*. Offering a record on the
    /// strength of it alone puts "publish this" directly under a recommendation that
    /// says to keep the record in place and change nothing. Record inspection is where
    /// a stale pct= is reported.
    /// </summary>
    [Theory]
    [InlineData("v=DMARC1; p=none; pct=100; rua=mailto:dmarc@acme.example", "none")]
    [InlineData("v=DMARC1; p=reject; pct=25; rua=mailto:dmarc@acme.example", "reject")]
    public void APctToDropIsNotOnItsOwnAStep(string live, string policy)
    {
        Assert.Null(EnforcementRecordBuilder.Build("acme.example", live, policy, testing: false));
    }

    /// <summary>
    /// "If the live record could not be read, say so and show nothing" — a guessed
    /// record is the one failure mode worse than no suggestion at all.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoLiveRecord_ProducesNoRecord(string? live)
    {
        Assert.Null(EnforcementRecordBuilder.Build("acme.example", live, "quarantine", testing: true));
    }

    /// <summary>
    /// A segment that is not name=value means the record is malformed, and rewriting it
    /// would mean deciding what the operator meant. Showing nothing is the honest answer.
    /// </summary>
    [Theory]
    [InlineData("v=DMARC1; p=none; garbage")]
    [InlineData("p=none; rua=mailto:dmarc@acme.example")]
    [InlineData("=DMARC1; p=none")]
    public void AnUnrewritableRecord_ProducesNoRecord(string live)
    {
        Assert.Null(EnforcementRecordBuilder.Build("acme.example", live, "quarantine", testing: true));
    }

    [Fact]
    public void TheHost_IsTheUnderscoreDmarcOwnerName()
    {
        var change = EnforcementRecordBuilder.Build("Mail.Acme.Example.", Live, "reject", testing: false);

        Assert.NotNull(change);
        Assert.Equal("_dmarc.Mail.Acme.Example", change!.Host);
    }
}
