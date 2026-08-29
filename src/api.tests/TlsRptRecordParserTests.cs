using DmarcAnalyzer.Api.Application.Analytics;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The `_smtp._tls` TXT parser (RFC 8460 §3). The rule worth pinning down is
/// the not-exactly-one one: after discarding records that don't start with
/// v=TLSRPTv1, anything but a single usable record means reporters treat the
/// domain as not implementing TLS-RPT, so it must not read as found.
/// </summary>
public sealed class TlsRptRecordParserTests
{
    [Theory]
    [InlineData("v=TLSRPTv1;rua=mailto:reports@example.com", "mailto:reports@example.com")]
    [InlineData("v=TLSRPTv1; rua=mailto:reports@example.com", "mailto:reports@example.com")]
    [InlineData("v=TLSRPTv1; rua=https://reporting.example.com/v1/tlsrpt", "https://reporting.example.com/v1/tlsrpt")]
    public void Found_ForASingleValidRecord(string txt, string expectedRua)
    {
        var record = TlsRptRecordChecker.Parse([txt]);

        Assert.Equal(TlsRptRecordStatus.Found, record.Status);
        Assert.Equal(txt, record.Raw);
        Assert.Equal([expectedRua], record.Rua);
        Assert.Empty(record.Issues);
    }

    /// <summary>RFC 8460 §3: the record may list a comma-separated set of destinations.</summary>
    [Fact]
    public void Found_KeepsEveryRuaDestination()
    {
        var record = TlsRptRecordChecker.Parse(
            ["v=TLSRPTv1; rua=mailto:a@example.com, https://r.example.com/tls"]);

        Assert.Equal(TlsRptRecordStatus.Found, record.Status);
        Assert.Equal(["mailto:a@example.com", "https://r.example.com/tls"], record.Rua);
    }

    /// <summary>Extension fields are legal and ignored; the record is still usable.</summary>
    [Fact]
    public void Found_IgnoresUnknownFields()
    {
        var record = TlsRptRecordChecker.Parse(
            ["v=TLSRPTv1; rua=mailto:a@example.com; ext-thing=whatever"]);

        Assert.Equal(TlsRptRecordStatus.Found, record.Status);
        Assert.Equal(["mailto:a@example.com"], record.Rua);
    }

    [Fact]
    public void Missing_WhenNothingIsPublished()
    {
        var record = TlsRptRecordChecker.Parse([]);

        Assert.Equal(TlsRptRecordStatus.Missing, record.Status);
        Assert.Null(record.Raw);
        // Publishing TLS-RPT is optional — not publishing it is not a finding.
        Assert.Empty(record.Issues);
    }

    /// <summary>A TXT set at the name that holds something else entirely (SPF, verification tokens).</summary>
    [Fact]
    public void Missing_WhenNoRecordCarriesTheVersion()
    {
        var record = TlsRptRecordChecker.Parse(["some-verification=abc123", "v=spf1 -all"]);

        Assert.Equal(TlsRptRecordStatus.Missing, record.Status);
    }

    [Fact]
    public void LookupFailed_IsNotMissing()
    {
        var record = TlsRptRecordChecker.Parse(null);

        Assert.Equal(TlsRptRecordStatus.LookupFailed, record.Status);
        Assert.Single(record.Issues);
    }

    [Fact]
    public void Invalid_WhenTwoRecordsArePublished()
    {
        var record = TlsRptRecordChecker.Parse([
            "v=TLSRPTv1;rua=mailto:a@example.com",
            "v=TLSRPTv1;rua=mailto:b@example.com",
        ]);

        Assert.Equal(TlsRptRecordStatus.Invalid, record.Status);
        Assert.Empty(record.Rua);
        Assert.Contains("2 TLS-RPT records", Assert.Single(record.Issues));
    }

    /// <summary>The ABNF makes rua required — without it there is nowhere to report.</summary>
    [Fact]
    public void Invalid_WhenRuaIsMissing()
    {
        var record = TlsRptRecordChecker.Parse(["v=TLSRPTv1; ext-thing=whatever"]);

        Assert.Equal(TlsRptRecordStatus.Invalid, record.Status);
        Assert.Equal("v=TLSRPTv1; ext-thing=whatever", record.Raw);
        Assert.Contains("no rua=", Assert.Single(record.Issues));
    }

    /// <summary>
    /// Only mailto: and https: are defined, and the value must be a URI that
    /// actually reaches something — a scheme prefix is not on its own a
    /// destination, and saying "found" for one would invite exactly the false
    /// confidence this card exists to remove.
    /// </summary>
    [Theory]
    [InlineData("http://reports.example.com/tls")] // wrong scheme
    [InlineData("https:relative")]                 // no host
    [InlineData("mailto:nobody")]                  // no domain
    [InlineData("mailto:@example.com")]            // no mailbox
    [InlineData("reports@example.com")]            // no scheme at all
    public void Invalid_WhenNoRuaDestinationIsDeliverable(string rua)
    {
        var record = TlsRptRecordChecker.Parse([$"v=TLSRPTv1; rua={rua}"]);

        Assert.Equal(TlsRptRecordStatus.Invalid, record.Status);
        Assert.Empty(record.Rua);
        Assert.Contains("not one reporters can use", Assert.Single(record.Issues));
    }

    /// <summary>
    /// RFC 8460's ABNF writes the version %s"v=TLSRPTv1", and RFC 7405's %s means
    /// case-sensitive — reporters discard the rest. Graded as invalid rather than
    /// missing: the domain published something, and the spelling is the fix.
    /// </summary>
    [Fact]
    public void Invalid_WhenTheVersionTagIsMiscased()
    {
        var record = TlsRptRecordChecker.Parse(["v=tlsrptv1;rua=mailto:reports@example.com"]);

        Assert.Equal(TlsRptRecordStatus.Invalid, record.Status);
        Assert.Empty(record.Rua);
        Assert.Contains("case-sensitive", Assert.Single(record.Issues));
    }

    /// <summary>
    /// The version has to be the whole first tag. Without this the record below
    /// reaches found, because the tag parser reads "v" and never checks what
    /// followed it on the same field.
    /// </summary>
    [Fact]
    public void Invalid_WhenTheVersionTagCarriesTrailingJunk()
    {
        var record = TlsRptRecordChecker.Parse(["v=TLSRPTv1 junk; rua=mailto:a@example.com"]);

        Assert.Equal(TlsRptRecordStatus.Invalid, record.Status);
        Assert.Contains("requires exactly", Assert.Single(record.Issues));
    }

    /// <summary>
    /// A version and nothing else is a published record missing its required
    /// field, not an absent one — same grading the MTA-STS parser gives a bare
    /// v=STSv1.
    /// </summary>
    [Fact]
    public void Invalid_WhenOnlyTheVersionIsPublished()
    {
        var record = TlsRptRecordChecker.Parse(["v=TLSRPTv1"]);

        Assert.Equal(TlsRptRecordStatus.Invalid, record.Status);
        Assert.Contains("no rua=", Assert.Single(record.Issues));
    }

    /// <summary>One good destination and one bad: usable, but the bad one is still worth saying.</summary>
    [Fact]
    public void Found_WithAnIssue_WhenOnlySomeDestinationsAreUsable()
    {
        var record = TlsRptRecordChecker.Parse(
            ["v=TLSRPTv1; rua=mailto:a@example.com,ftp://example.com/drop"]);

        Assert.Equal(TlsRptRecordStatus.Found, record.Status);
        Assert.Equal(["mailto:a@example.com"], record.Rua);
        Assert.Single(record.Issues);
    }

    /// <summary>
    /// Nothing TLS-RPT-shaped at the name at all. "v=TLSRPTv12" is a different
    /// version of some other thing, and a record that merely mentions the
    /// version later is not one — claiming either would be reporting on a record
    /// we do not understand.
    /// </summary>
    [Theory]
    [InlineData("v=TLSRPTv12;rua=mailto:a@example.com")]
    [InlineData("rua=mailto:a@example.com;v=TLSRPTv1")]
    public void Missing_WhenTheVersionTokenIsNotOurs(string txt)
    {
        Assert.Equal(TlsRptRecordStatus.Missing, TlsRptRecordChecker.Parse([txt]).Status);
    }
}
