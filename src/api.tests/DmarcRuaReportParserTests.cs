using DmarcAnalyzer.Api.Application.Reports;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

public sealed class DmarcRuaReportParserTests
{
    private readonly DmarcRuaReportParser _parser = new();

    [Fact]
    public void Parse_WithYahooFixture_MapsMetadataAndSingleRecord()
    {
        using var stream = OpenFixture("sample-yahoo-aggregate.xml");

        var result = _parser.Parse(stream);

        Assert.Equal("Yahoo", result.OrganizationName);
        Assert.Equal("1737770612.289931", result.ReportId);
        Assert.Equal("007ed325dddd44e3a0f17488f4312e49.com", result.PolicyDomain);
        Assert.Equal(1, result.RecordCount);
        Assert.Single(result.Records);
        Assert.Equal("127.0.0.1", result.Records[0].SourceIp);
        Assert.Equal(2, result.Records[0].DkimAuthResults.Count);
        Assert.Single(result.Records[0].SpfAuthResults);
        Assert.Equal(new DateTime(2025, 1, 24, 0, 0, 0, DateTimeKind.Utc), result.RangeBeginUtc);
        Assert.Equal(new DateTime(2025, 1, 24, 23, 59, 59, DateTimeKind.Utc), result.RangeEndUtc);
        Assert.False(result.HasValidationErrors);
    }

    [Fact]
    public void Parse_WithZohoFixture_MapsMetadataAndMultipleRecords()
    {
        using var stream = OpenFixture("sample-zoho-aggregate.xml");

        var result = _parser.Parse(stream);

        Assert.Equal("zoho.com", result.OrganizationName);
        Assert.Equal("cd2dab45-f745-495c-845e-87a731db3873", result.ReportId);
        Assert.Equal("000fb7a64b524d7bb8fe8fc8831716a2.com", result.PolicyDomain);
        Assert.Equal(3, result.RecordCount);
        Assert.Equal(3, result.Records.Count);
        Assert.Equal(new DateTime(2025, 1, 21, 8, 0, 0, DateTimeKind.Utc), result.RangeBeginUtc);
        Assert.Equal(new DateTime(2025, 1, 22, 8, 0, 0, DateTimeKind.Utc), result.RangeEndUtc);
        Assert.False(result.HasValidationErrors);
    }

    /// <summary>
    /// Absent sp and explicit sp=none deserialize identically (the XSD defaults sp
    /// to "none" and DmarcRua exposes no *Specified flag), so presence is read from
    /// the XML. The two real fixtures happen to cover both: Yahoo omits sp, Zoho
    /// sends sp=none.
    /// </summary>
    [Fact]
    public void Parse_DistinguishesAbsentSpFromExplicitSpNone()
    {
        using var withoutSp = OpenFixture("sample-yahoo-aggregate.xml");
        Assert.Null(_parser.Parse(withoutSp).SubdomainPolicy);

        using var withSp = OpenFixture("sample-zoho-aggregate.xml");
        Assert.Equal("none", _parser.Parse(withSp).SubdomainPolicy);
    }

    [Theory]
    [InlineData("<sp>reject</sp>", "reject")]
    [InlineData("<sp>quarantine</sp>", "quarantine")]
    [InlineData("", null)]
    public void Parse_ReadsSubdomainPolicyPresence(string spElement, string? expected)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>example.net</org_name>
                <report_id>sp-presence</report_id>
                <date_range><begin>1737676800</begin><end>1737763199</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>acme.example</domain><adkim>r</adkim><aspf>r</aspf>
                <p>reject</p>{spElement}<pct>100</pct>
              </policy_published>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>1</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>acme.example</header_from></identifiers>
                <auth_results><spf><domain>acme.example</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal("reject", result.PublishedPolicy);
        Assert.Equal(expected, result.SubdomainPolicy);
    }

    [Fact]
    public void Parse_WithUnreadableStream_Throws()
    {
        using var stream = new NonReadableStream();

        var ex = Assert.Throws<ArgumentException>(() => _parser.Parse(stream));

        Assert.Contains("readable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// helo is a legal SPF scope (RFC 7208) and real reporters send it. DmarcRua 2.0.0
    /// modelled only mfrom, so it was fatal, and this parser rewrote it to mfrom to save the
    /// document — storing a scope the reporter never reported, and surfacing it in the
    /// per-source SPF table. 2.0.1 added Helo, so it is now recorded as sent.
    /// </summary>
    [Fact]
    public void Parse_WithHeloScope_PreservesTheScopeAsSent()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>scope-test</org_name>
                <email>noreply@example.com</email>
                <report_id>scope-helo-1</report_id>
                <date_range>
                  <begin>1737446400</begin>
                  <end>1737532800</end>
                </date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain>
                <adkim>r</adkim>
                <aspf>r</aspf>
                <p>none</p>
                <sp>none</sp>
                <pct>100</pct>
              </policy_published>
              <record>
                <row>
                  <source_ip>127.0.0.1</source_ip>
                  <count>1</count>
                  <policy_evaluated>
                    <disposition>none</disposition>
                    <dkim>pass</dkim>
                    <spf>pass</spf>
                  </policy_evaluated>
                </row>
                <identifiers>
                  <header_from>example.com</header_from>
                </identifiers>
                <auth_results>
                  <spf>
                    <domain>example.com</domain>
                    <result>pass</result>
                    <scope>helo</scope>
                  </spf>
                </auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal("scope-test", result.OrganizationName);
        Assert.Equal(1, result.RecordCount);
        Assert.Equal("helo", Assert.Single(result.Records[0].SpfAuthResults).Scope);

        // No longer a repair, so it must no longer be reported as one.
        Assert.DoesNotContain(result.ValidationMessages, x => x.Contains("SPF scope", StringComparison.Ordinal));
    }

    /// <summary>
    /// A scope the enum has no member for is still fatal to the whole document, so it is
    /// repaired rather than passed through — including the uppercase spellings, which matter
    /// because XmlSerializer matches XmlEnum names case-sensitively. 'HELO' is a real value
    /// we can honour exactly, so it is corrected to 'helo' rather than defaulted to 'mfrom'.
    /// </summary>
    [Theory]
    [InlineData("helo", "helo", false)]
    [InlineData("HELO", "helo", false)]
    [InlineData("mfrom", "mfrom", false)]
    [InlineData("MFrom", "mfrom", false)]
    [InlineData("  helo  ", "helo", false)]
    [InlineData("bogus", "mfrom", true)]
    [InlineData("", "mfrom", true)]
    public void Parse_RepairsUnusableSpfScopeWithoutLosingTheReport(
        string scope, string expectedScope, bool expectsWarning)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>scope-test</org_name>
                <email>noreply@example.com</email>
                <report_id>scope-repair-1</report_id>
                <date_range><begin>1737446400</begin><end>1737532800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain><adkim>r</adkim><aspf>r</aspf><p>none</p><pct>100</pct>
              </policy_published>
              <record>
                <row>
                  <source_ip>127.0.0.1</source_ip>
                  <count>1</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results>
                  <spf><domain>example.com</domain><result>pass</result><scope>{scope}</scope></spf>
                </auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal(1, result.RecordCount);
        Assert.Equal(expectedScope, Assert.Single(result.Records[0].SpfAuthResults).Scope);
        Assert.Equal(
            expectsWarning,
            result.ValidationMessages.Any(x => x.Contains("spf/scope", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The same case-sensitivity trap on a value that decides compliance. A reporter sending
    /// 'PASS' used to be accepted by the repair pass, written back verbatim, and then rejected
    /// by XmlSerializer — losing every record in the document.
    /// </summary>
    [Theory]
    [InlineData("PASS", "pass")]
    [InlineData("Fail", "fail")]
    public void Parse_CorrectsEnumCaseRatherThanLosingTheDocument(string dkim, string expected)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>case-test</org_name>
                <email>noreply@example.com</email>
                <report_id>case-1</report_id>
                <date_range><begin>1737446400</begin><end>1737532800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain><adkim>r</adkim><aspf>r</aspf><p>none</p><pct>100</pct>
              </policy_published>
              <record>
                <row>
                  <source_ip>127.0.0.1</source_ip>
                  <count>4</count>
                  <policy_evaluated><disposition>none</disposition><dkim>{dkim}</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal(1, result.RecordCount);
        Assert.Equal(expected, Assert.Single(result.Records).DkimResult);

        // A case correction changes no meaning, so it must not be reported as a substitution.
        Assert.DoesNotContain(result.ValidationMessages, x => x.Contains("replaced unrecognised", StringComparison.Ordinal));
    }

    /// <summary>
    /// np, testing and discovery_method (RFC 9989/9990/9991) are already modeled by
    /// DmarcRua 2.0.0's PolicyPublishedType — this only guards that the parser
    /// actually reads them through, as informational messages rather than silently
    /// discarding them like it did before.
    /// </summary>
    [Fact]
    public void Parse_SurfacesNpTestingAndDiscoveryMethod()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>bis-tags-test</org_name>
                <report_id>bis-tags-1</report_id>
                <date_range><begin>1737446400</begin><end>1737532800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>acme.example</domain>
                <adkim>r</adkim><aspf>r</aspf>
                <p>reject</p><np>quarantine</np><pct>100</pct>
                <testing>y</testing>
                <discovery_method>treewalk</discovery_method>
              </policy_published>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>1</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>acme.example</header_from></identifiers>
                <auth_results><spf><domain>acme.example</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Contains(result.ValidationMessages, x => x == "info: np=quarantine published for acme.example");
        Assert.Contains(result.ValidationMessages, x => x == "info: t=y published for acme.example");
        Assert.Contains(result.ValidationMessages, x => x == "info: discovery_method=treewalk reported for acme.example");
        Assert.False(result.HasValidationErrors, string.Join(" | ", result.ValidationMessages));
    }

    [Fact]
    public void Parse_WithoutNpTestingOrDiscoveryMethod_AddsNoMessages()
    {
        using var stream = OpenFixture("sample-yahoo-aggregate.xml");
        var result = _parser.Parse(stream);
        Assert.DoesNotContain(result.ValidationMessages, x => x.StartsWith("info:", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_WithDmarcBisNamespace_StripsNamespaceAndParses()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback xmlns="urn:ietf:params:xml:ns:dmarc-2.0">
              <version>2.0</version>
              <report_metadata>
                <org_name>bis-test</org_name>
                <email>noreply@example.com</email>
                <report_id>bis-1</report_id>
                <date_range>
                  <begin>1737446400</begin>
                  <end>1737532800</end>
                </date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain>
                <adkim>r</adkim>
                <aspf>r</aspf>
                <p>none</p>
                <sp>none</sp>
                <pct>100</pct>
              </policy_published>
              <record>
                <row>
                  <source_ip>127.0.0.1</source_ip>
                  <count>2</count>
                  <policy_evaluated>
                    <disposition>none</disposition>
                    <dkim>pass</dkim>
                    <spf>pass</spf>
                  </policy_evaluated>
                </row>
                <identifiers>
                  <header_from>example.com</header_from>
                </identifiers>
                <auth_results>
                  <dkim>
                    <domain>example.com</domain>
                    <selector>s1</selector>
                    <result>pass</result>
                  </dkim>
                  <spf>
                    <domain>example.com</domain>
                    <result>pass</result>
                  </spf>
                </auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal("bis-test", result.OrganizationName);
        Assert.Equal("bis-1", result.ReportId);
        Assert.Equal("example.com", result.PolicyDomain);
        Assert.Equal(1, result.RecordCount);
        Assert.Single(result.Records);
        Assert.Equal(2, result.Records[0].MessageCount);
        Assert.Contains(result.ValidationMessages, x => x.Contains("stripped XML namespace 'urn:ietf:params:xml:ns:dmarc-2.0'", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ExtractsPublishedPolicy()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>policy-test</org_name>
                <email>noreply@example.com</email>
                <report_id>policy-1</report_id>
                <date_range><begin>1737446400</begin><end>1737532800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain>
                <adkim>s</adkim>
                <aspf>r</aspf>
                <p>reject</p>
                <sp>quarantine</sp>
                <pct>100</pct>
              </policy_published>
              <record>
                <row>
                  <source_ip>127.0.0.1</source_ip>
                  <count>1</count>
                  <policy_evaluated><disposition>reject</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal("reject", result.PublishedPolicy);
        Assert.Equal("quarantine", result.SubdomainPolicy);
        Assert.Equal(100, result.PublishedPct);
        Assert.Equal("strict", result.DkimAlignment);
        Assert.Equal("relaxed", result.SpfAlignment);
    }

    /// <summary>
    /// Both alignment tags are optional, and real reporters omit them — Mail.Ru and Fastmail
    /// among them, 1.5% of the reports vendored in DmarcRua 2.0.1's own test resources. On
    /// 2.0.1 that is not merely a default to fill in: reading its computed Adkim/Aspf
    /// properties throws ArgumentNullException on an absent tag, which would have failed
    /// ingestion for every report from those reporters. Every other test here supplies both
    /// tags, so the suite went green on that upgrade while production would have broken.
    /// </summary>
    [Theory]
    [InlineData("", "", "relaxed", "relaxed")]                                  // both omitted
    [InlineData("<adkim>s</adkim>", "", "strict", "relaxed")]                    // aspf omitted
    [InlineData("", "<aspf>s</aspf>", "relaxed", "strict")]                      // adkim omitted
    [InlineData("<adkim></adkim>", "<aspf/>", "relaxed", "relaxed")]             // present but empty
    [InlineData("<adkim> S </adkim>", "<aspf>R</aspf>", "strict", "relaxed")]    // padded, uppercase
    [InlineData("<adkim>strict</adkim>", "<aspf>bogus</aspf>", "relaxed", "relaxed")] // unrecognised
    public void Parse_DefaultsAlignmentWhenTagIsAbsentOrUnusable(
        string adkim, string aspf, string expectedDkimAlignment, string expectedSpfAlignment)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>alignment-test</org_name>
                <email>noreply@example.com</email>
                <report_id>alignment-1</report_id>
                <date_range><begin>1737446400</begin><end>1737532800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain>
                {adkim}
                {aspf}
                <p>reject</p>
                <pct>100</pct>
              </policy_published>
              <record>
                <row>
                  <source_ip>127.0.0.1</source_ip>
                  <count>1</count>
                  <policy_evaluated><disposition>reject</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal(expectedDkimAlignment, result.DkimAlignment);
        Assert.Equal(expectedSpfAlignment, result.SpfAlignment);

        // The record itself must still survive: the throw was at policy-read time, after
        // deserialization, so it discarded a report that had parsed perfectly well.
        Assert.Equal("reject", result.PublishedPolicy);
        Assert.Single(result.Records);
        Assert.False(result.HasValidationErrors);
    }

    private static Stream OpenFixture(string fixtureName)
    {
        var basePath = AppContext.BaseDirectory;
        var path = Path.Combine(basePath, "Fixtures", fixtureName);
        return File.OpenRead(path);
    }

    private sealed class NonReadableStream : MemoryStream
    {
        public override bool CanRead => false;
    }
}
