using DmarcAnalyzer.Api.Application.MtaSts;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The RFC 8461 parsers: the `_mta-sts` TXT record, the policy file, and the mx
/// pattern matcher. Pure statics, so the tables here are the spec's edge cases
/// verbatim — including the trailing semicolon gmail publishes in production.
/// </summary>
public sealed class MtaStsParserTests
{
    // --- ParseStsRecord ---

    [Fact]
    public void StsRecord_LookupFailure_IsNotMissing()
    {
        var result = MtaStsCheckService.ParseStsRecord(null);
        Assert.Equal(MtaStsRecordStatus.LookupFailed, result.Status);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void StsRecord_NoRecords_IsMissing_AndQuiet()
    {
        var result = MtaStsCheckService.ParseStsRecord([]);
        Assert.Equal(MtaStsRecordStatus.Missing, result.Status);
        // Publishing MTA-STS is optional; the missing state renders quietly.
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void StsRecord_UnrelatedTxtRecords_AreIgnored()
    {
        var result = MtaStsCheckService.ParseStsRecord(
            ["google-site-verification=abc", "v=spf1 -all"]);
        Assert.Equal(MtaStsRecordStatus.Missing, result.Status);
    }

    [Theory]
    [InlineData("v=STSv1; id=abc123", "abc123")]
    [InlineData("v=STSv1; id=20190429T010101;", "20190429T010101")] // gmail's literal record, trailing semicolon included
    [InlineData("v=STSv1;id=x", "x")]
    [InlineData("  v=STSv1; id=x  ", "x")]
    [InlineData("v=STSv1; id=x; extension=ignored", "x")]
    public void StsRecord_ValidForms_ParseTheId(string record, string expectedId)
    {
        var result = MtaStsCheckService.ParseStsRecord([record]);
        Assert.Equal(MtaStsRecordStatus.Found, result.Status);
        Assert.Equal(expectedId, result.Id);
        Assert.Equal(record, result.Raw);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void StsRecord_MixedWithUnrelatedTxt_IsStillFound()
    {
        var result = MtaStsCheckService.ParseStsRecord(
            ["google-site-verification=abc", "v=STSv1; id=x"]);
        Assert.Equal(MtaStsRecordStatus.Found, result.Status);
        Assert.Equal("x", result.Id);
    }

    [Theory]
    [InlineData("v=STSv1")]                    // no id at all
    [InlineData("v=STSv1; id=")]               // empty id
    [InlineData("v=STSv1; id=has-dash")]       // RFC 8461: letters and digits only
    [InlineData("v=STSv1; id=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // 33 chars
    public void StsRecord_BadId_IsInvalid(string record)
    {
        var result = MtaStsCheckService.ParseStsRecord([record]);
        Assert.Equal(MtaStsRecordStatus.Invalid, result.Status);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void StsRecord_TwoStsRecords_IsInvalid_NotFound()
    {
        // RFC 8461 §3.1: senders treat this as "no available policy" — reporting
        // it as found would tell the operator the opposite of what senders see.
        var result = MtaStsCheckService.ParseStsRecord(
            ["v=STSv1; id=a1", "v=STSv1; id=b2"]);
        Assert.Equal(MtaStsRecordStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, i => i.Contains("2 MTA-STS records"));
    }

    [Fact]
    public void StsRecord_SimilarPrefix_IsNotAnStsRecord()
    {
        var result = MtaStsCheckService.ParseStsRecord(["v=STSv12; id=x"]);
        Assert.Equal(MtaStsRecordStatus.Missing, result.Status);
    }

    // --- ParsePolicy ---

    private const string GmailPolicy =
        "version: STSv1\nmode: enforce\nmx: smtp.google.com\nmx: gmail-smtp-in.l.google.com\nmx: *.gmail-smtp-in.l.google.com\nmax_age: 86400\n";

    [Fact]
    public void Policy_GmailBody_ParsesCompletely()
    {
        var policy = MtaStsCheckService.ParsePolicy(GmailPolicy);

        Assert.True(policy.Valid);
        Assert.Equal("enforce", policy.Mode);
        Assert.Equal(86400, policy.MaxAgeSeconds);
        Assert.Equal(
            ["smtp.google.com", "gmail-smtp-in.l.google.com", "*.gmail-smtp-in.l.google.com"],
            policy.MxPatterns);
        Assert.Empty(policy.Issues);
    }

    [Fact]
    public void Policy_CrlfLineEndings_ParseTheSame()
    {
        var policy = MtaStsCheckService.ParsePolicy(GmailPolicy.Replace("\n", "\r\n"));
        Assert.True(policy.Valid);
        Assert.Equal(3, policy.MxPatterns.Count);
    }

    [Theory]
    [InlineData("mode: enforce\nmx: a.example\nmax_age: 86400\n")]      // no version
    [InlineData("version: STSv2\nmode: enforce\nmx: a.example\nmax_age: 86400\n")] // wrong version
    [InlineData("version: STSv1\nmx: a.example\nmax_age: 86400\n")]      // no mode
    [InlineData("version: STSv1\nmode: enforced\nmx: a.example\nmax_age: 86400\n")] // bad mode
    [InlineData("version: STSv1\nmode: enforce\nmx: a.example\n")]       // no max_age
    [InlineData("version: STSv1\nmode: enforce\nmx: a.example\nmax_age: soon\n")] // non-numeric max_age
    [InlineData("version: STSv1\nmode: enforce\nmax_age: 86400\n")]      // enforce without mx
    public void Policy_MissingOrBadRequiredFields_AreInvalid(string body)
    {
        var policy = MtaStsCheckService.ParsePolicy(body);
        Assert.False(policy.Valid);
        Assert.NotEmpty(policy.Issues);
    }

    [Fact]
    public void Policy_ModeNone_NeedsNoMx()
    {
        var policy = MtaStsCheckService.ParsePolicy("version: STSv1\nmode: none\nmax_age: 86400\n");
        Assert.True(policy.Valid);
        Assert.Equal("none", policy.Mode);
        Assert.Empty(policy.MxPatterns);
    }

    [Fact]
    public void Policy_MaxAgeOverRfcCap_IsValidWithIssue()
    {
        var policy = MtaStsCheckService.ParsePolicy(
            "version: STSv1\nmode: testing\nmx: a.example\nmax_age: 31557601\n");
        Assert.True(policy.Valid);
        Assert.Contains(policy.Issues, i => i.Contains("31557600"));
    }

    [Fact]
    public void Policy_VersionNotFirst_IsValidWithIssue()
    {
        var policy = MtaStsCheckService.ParsePolicy(
            "mode: testing\nversion: STSv1\nmx: a.example\nmax_age: 86400\n");
        Assert.True(policy.Valid);
        Assert.Contains(policy.Issues, i => i.Contains("first"));
    }

    [Fact]
    public void Policy_DuplicateScalarField_FirstWins()
    {
        var policy = MtaStsCheckService.ParsePolicy(
            "version: STSv1\nmode: testing\nmode: enforce\nmx: a.example\nmax_age: 86400\n");
        Assert.True(policy.Valid);
        Assert.Equal("testing", policy.Mode);
        Assert.Contains(policy.Issues, i => i.Contains("Duplicate mode"));
    }

    [Fact]
    public void Policy_UnknownKeysAndBlankLines_AreIgnored()
    {
        var policy = MtaStsCheckService.ParsePolicy(
            "version: STSv1\n\nmode: testing\nfuture_field: whatever\nmx: a.example\nmax_age: 86400\n");
        Assert.True(policy.Valid);
        Assert.Empty(policy.Issues);
    }

    [Fact]
    public void Policy_GarbageLine_IsAnIssueButNotFatal()
    {
        var policy = MtaStsCheckService.ParsePolicy(
            "version: STSv1\nmode: testing\nthis is not a field\nmx: a.example\nmax_age: 86400\n");
        Assert.True(policy.Valid);
        Assert.Contains(policy.Issues, i => i.Contains("Unrecognized line"));
    }

    [Fact]
    public void Policy_WhitespaceAroundColon_IsTolerated()
    {
        var policy = MtaStsCheckService.ParsePolicy(
            "version : STSv1\nmode :testing\nmx:  a.example  \nmax_age: 86400");
        Assert.True(policy.Valid);
        Assert.Equal("testing", policy.Mode);
        Assert.Equal(["a.example"], policy.MxPatterns);
    }

    // --- MatchesMxPattern ---

    [Theory]
    [InlineData("mx1.example.com", "mx1.example.com", true)]
    [InlineData("MX1.Example.COM", "mx1.example.com", true)]              // case-insensitive
    [InlineData("mx1.example.com.", "mx1.example.com", true)]             // trailing dot on pattern
    [InlineData("mx1.example.com", "mx1.example.com.", true)]             // trailing dot on host
    [InlineData("mx1.example.com", "mx2.example.com", false)]
    [InlineData("example.com", "mx1.example.com", false)]                 // non-wildcard never covers a subdomain
    [InlineData("*.gmail-smtp-in.l.google.com", "alt1.gmail-smtp-in.l.google.com", true)]
    [InlineData("*.gmail-smtp-in.l.google.com", "gmail-smtp-in.l.google.com", false)]  // wildcard never covers the apex
    [InlineData("*.gmail-smtp-in.l.google.com", "a.b.gmail-smtp-in.l.google.com", false)] // exactly one label
    [InlineData("*.example.com", "a.example.com", true)]
    [InlineData("*.example.com", ".example.com", false)]                  // empty label is not a label
    [InlineData("*.", "anything", false)]
    [InlineData("", "mx1.example.com", false)]
    [InlineData("mx1.example.com", "", false)]
    public void MxPattern_Table(string pattern, string host, bool expected)
    {
        Assert.Equal(expected, MtaStsCheckService.MatchesMxPattern(pattern, host));
    }
}
