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
    public void NonStandardSpacing_IsNormalizedWithoutLosingTags()
    {
        var change = EnforcementRecordBuilder.Build(
            "acme.example", "v=DMARC1;p=none;rua=mailto:dmarc@acme.example;", "quarantine", testing: false);

        Assert.NotNull(change);
        Assert.Equal("v=DMARC1; p=quarantine; rua=mailto:dmarc@acme.example", change!.Recommended);
    }

    /// <summary>
    /// RFC 6376 §3.2 — the tag-value syntax RFC 9989 §4.7 adopts — has tag names
    /// interpreted case-sensitively, so <c>RUA=</c> is not <c>rua=</c> and this domain
    /// is getting no aggregate reports at all. Rewriting the policy in place would hand
    /// back a record that looks repaired and still sends nothing. The earlier behaviour
    /// did exactly that; record inspection is where the real problem belongs.
    /// </summary>
    [Theory]
    [InlineData("v=DMARC1; P=none; rua=mailto:dmarc@acme.example")]
    [InlineData("v=DMARC1; p=none; RUA=mailto:dmarc@acme.example")]
    [InlineData("v=DMARC1; p=none; rua=mailto:dmarc@acme.example; SP=none")]
    [InlineData("v=DMARC1; p=quarantine; T=y; rua=mailto:dmarc@acme.example")]
    public void AKnownTagPublishedInTheWrongCase_ProducesNoRecord(string live)
    {
        Assert.Null(EnforcementRecordBuilder.Build("acme.example", live, "quarantine", testing: true));
    }

    /// <summary>
    /// "Tags with duplicate names MUST NOT occur within a single tag-list; if a tag name
    /// does occur more than once, the entire tag-list is invalid" (RFC 6376 §3.2).
    /// Editing the first occurrence would leave the second behind to contradict it.
    /// </summary>
    [Theory]
    [InlineData("v=DMARC1; p=none; p=reject; rua=mailto:dmarc@acme.example")]
    [InlineData("v=DMARC1; p=none; rua=mailto:a@acme.example; rua=mailto:b@acme.example")]
    public void ARepeatedTagName_ProducesNoRecord(string live)
    {
        Assert.Null(EnforcementRecordBuilder.Build("acme.example", live, "reject", testing: false));
    }

    /// <summary>
    /// "The tag value is case sensitive, and the only possible value is 'DMARC1'... then
    /// the entire record MUST be ignored" (RFC 9989 §4.7). Our own lookup accepts the
    /// variant case, so this record does reach the builder.
    /// </summary>
    [Theory]
    [InlineData("v=dmarc1; p=none; rua=mailto:dmarc@acme.example")]
    [InlineData("v=DMARC2; p=none; rua=mailto:dmarc@acme.example")]
    public void ARecordReceiversWouldIgnore_ProducesNoRecord(string live)
    {
        Assert.Null(EnforcementRecordBuilder.Build("acme.example", live, "quarantine", testing: true));
    }

    /// <summary>
    /// A policy value in the wrong case is a syntax error a receiver discards in favour
    /// of the default (RFC 9989 §4.8), so <c>p=Reject</c> is not enforcing anything.
    /// Correcting the case is a real change and is reported as one.
    /// </summary>
    [Fact]
    public void APolicyValueInTheWrongCase_IsCorrected()
    {
        var change = EnforcementRecordBuilder.Build(
            "acme.example", "v=DMARC1; p=Reject; rua=mailto:dmarc@acme.example", "reject", testing: false);

        Assert.NotNull(change);
        Assert.Equal("v=DMARC1; p=reject; rua=mailto:dmarc@acme.example", change!.Recommended);
        Assert.Equal(["p"], change.ChangedTags);
    }

    /// <summary>
    /// Unknown tags are a different matter from mis-cased known ones: RFC 9989 §4.8 has
    /// receivers ignore them, so they cost nothing and are carried through untouched,
    /// in place. That includes the tags RFC 9989 marks historic (ri, rf) and the ones it
    /// added (np, psd) — none of which this step has any business moving.
    /// </summary>
    [Fact]
    public void TagsThisCodeDoesNotActOn_SurviveInPlace()
    {
        var change = EnforcementRecordBuilder.Build(
            "acme.example",
            "v=DMARC1; p=none; np=reject; psd=n; ri=86400; rf=afrf; x-vendor=acme-7",
            "quarantine",
            testing: true);

        Assert.NotNull(change);
        Assert.Equal(
            "v=DMARC1; p=quarantine; t=y; np=reject; psd=n; ri=86400; rf=afrf; x-vendor=acme-7",
            change!.Recommended);
    }

    /// <summary>
    /// A value may contain the characters that structure the record around it — an equals
    /// sign inside a reporting URI, a comma between two of them, the <c>!10m</c> size
    /// limit RFC 9989 §4.6 defines. Splitting on the first equals and nothing else is
    /// what keeps them intact.
    /// </summary>
    [Fact]
    public void AReportingUriWithItsOwnPunctuation_IsNotMangled()
    {
        const string live =
            "v=DMARC1; p=none; rua=mailto:dmarc@acme.example!10m,mailto:agency+acme@reports.example?subject=dmarc=rua; fo=0:1:d:s";

        var change = EnforcementRecordBuilder.Build("acme.example", live, "quarantine", testing: false);

        Assert.NotNull(change);
        Assert.Equal(
            "v=DMARC1; p=quarantine; rua=mailto:dmarc@acme.example!10m,mailto:agency+acme@reports.example?subject=dmarc=rua; fo=0:1:d:s",
            change!.Recommended);
    }

    /// <summary>A bare version record is legal, and the step is the policy it lacks.</summary>
    [Fact]
    public void ARecordWithNothingButTheVersion_GainsThePolicy()
    {
        var change = EnforcementRecordBuilder.Build("acme.example", "v=DMARC1", "none", testing: false);

        Assert.NotNull(change);
        Assert.Equal("v=DMARC1; p=none", change!.Recommended);
    }

    /// <summary>
    /// A record already in testing mode, moving up a rung: t=y is already there and stays
    /// there, so the step is the policy alone and the testing tag is not reported as
    /// changed when it did not change.
    /// </summary>
    [Fact]
    public void ARecordAlreadyInTestingMode_KeepsTheTestingTagWhereItIs()
    {
        var change = EnforcementRecordBuilder.Build(
            "acme.example",
            "v=DMARC1; p=none; t=y; rua=mailto:dmarc@acme.example",
            "quarantine",
            testing: true);

        Assert.NotNull(change);
        Assert.Equal("v=DMARC1; p=quarantine; t=y; rua=mailto:dmarc@acme.example", change!.Recommended);
        Assert.Equal(["p"], change.ChangedTags);
    }

    /// <summary>The step a domain is already standing on is not a step.</summary>
    [Fact]
    public void ARecordAlreadyAtTheRecommendedTestingStep_ProducesNoRecord()
    {
        Assert.Null(EnforcementRecordBuilder.Build(
            "acme.example", "v=DMARC1; p=quarantine; t=y; rua=mailto:dmarc@acme.example", "quarantine", testing: true));
    }

    /// <summary>
    /// The whole preserved set at once, asserted as one string rather than tag by tag:
    /// every value identical to the published one and in the published order, with only
    /// p= and t= moved. A Contains assertion per tag would pass on a record whose tags
    /// had been reshuffled, and a reshuffled record is a diff an operator cannot eyeball.
    /// </summary>
    [Fact]
    public void TheFullPublishedTagSet_SurvivesInOrderWithItsValuesUntouched()
    {
        const string live =
            "v=DMARC1; p=none; sp=none; adkim=s; aspf=s; fo=1; " +
            "rua=mailto:dmarc@acme.example,mailto:agency@reports.example; ruf=mailto:forensics@acme.example";

        var change = EnforcementRecordBuilder.Build("acme.example", live, "quarantine", testing: true);

        Assert.NotNull(change);
        Assert.Equal(
            "v=DMARC1; p=quarantine; t=y; sp=none; adkim=s; aspf=s; fo=1; " +
            "rua=mailto:dmarc@acme.example,mailto:agency@reports.example; ruf=mailto:forensics@acme.example",
            change!.Recommended);
        Assert.Equal(["p", "t"], change.ChangedTags);
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
