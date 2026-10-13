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
        Assert.DoesNotContain(result.ValidationMessages, x => x.Text.Contains("SPF scope", StringComparison.Ordinal));
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
            result.ValidationMessages.Any(x => x.Text.Contains("spf/scope", StringComparison.Ordinal)));
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
        Assert.DoesNotContain(result.ValidationMessages, x => x.Text.Contains("replaced unrecognised", StringComparison.Ordinal));
    }

    /// <summary>
    /// np, testing and discovery_method are RFC 9990's, but they are read in any format:
    /// a real-mailbox sample found 1,307 RFC 7489 reports from one large reporter sending
    /// np in no namespace. This report has no namespace, so "v2-only" would lose them.
    /// </summary>
    [Fact]
    public void Parse_ReadsNpTestingAndDiscoveryMethodFromAnRfc7489Report()
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

        Assert.Equal(DmarcReportFormats.Rfc7489, result.ReportFormat);
        Assert.Equal("quarantine", result.NonexistentSubdomainPolicy);
        Assert.True(result.Testing);
        Assert.Equal("treewalk", result.DiscoveryMethod);
        Assert.Equal(100, result.PublishedPct);
        Assert.False(result.HasValidationErrors, string.Join(" | ", result.ValidationMessages));
        Assert.Empty(result.ValidationMessages);
    }

    /// <summary>
    /// An RFC 7489 report: its format, the pct it sends, and null — not a default — for
    /// every report-level field it does not send.
    /// </summary>
    [Fact]
    public void Parse_Rfc7489Report_StoresItsPctAndNullForTheFieldsItDoesNotSend()
    {
        using var stream = OpenFixture("sample-yahoo-aggregate.xml");

        var result = _parser.Parse(stream);

        Assert.Equal(DmarcReportFormats.Rfc7489, result.ReportFormat);
        Assert.Equal(100, result.PublishedPct);
        Assert.Null(result.NonexistentSubdomainPolicy);
        Assert.Null(result.Testing);
        Assert.Null(result.DiscoveryMethod);
        Assert.Null(result.Generator);
        Assert.Empty(result.ReportErrors);
        Assert.Empty(result.ValidationMessages);
    }

    /// <summary>
    /// RFC 9990 removed pct. An absent tag used to become 100, so every conformant RFC 9990
    /// report stored a pct it never sent; now it stores nothing.
    /// </summary>
    [Fact]
    public void Parse_Rfc9990ReportWithoutPct_StoresNoPct()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback xmlns="urn:ietf:params:xml:ns:dmarc-2.0">
              <version>1.0</version>
              <report_metadata>
                <org_name>no-pct</org_name>
                <email>noreply@example.com</email>
                <report_id>no-pct-1</report_id>
                <date_range><begin>1737446400</begin><end>1737532800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain>
                <p>none</p>
              </policy_published>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>1</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal(DmarcReportFormats.Rfc9990, result.ReportFormat);
        Assert.Null(result.PublishedPct);
        Assert.False(result.HasValidationErrors, string.Join(" | ", result.ValidationMessages));
    }

    /// <summary>
    /// A value that is not a whole number from 0 to 100 still becomes 100, as before, but
    /// now says so: it is a substitution, unlike an absent tag.
    /// </summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("250")]
    [InlineData("")]
    public void Parse_UnusablePct_Becomes100WithAWarning(string pct)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>bad-pct</org_name>
                <report_id>bad-pct-1</report_id>
                <date_range><begin>1737446400</begin><end>1737532800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain>
                <p>none</p>
                <pct>{pct}</pct>
              </policy_published>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>1</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal(100, result.PublishedPct);
        Assert.Contains(
            DmarcReportParseMessage.Warning(
                $"replaced unusable policy_published/pct value '{pct}' with '100' for compatibility"),
            result.ValidationMessages);
    }

    /// <summary>
    /// generator and error are read whatever the format. RFC 7489 allows several error
    /// elements where RFC 9990 allows one, so they are a list. The generator is trimmed and
    /// capped at the column width, and removed before validation, because DmarcRua's schema
    /// does not know it.
    /// </summary>
    [Fact]
    public void Parse_ReadsGeneratorAndErrorsFromAnRfc7489Report()
    {
        var longGenerator = new string('g', 300);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>errors</org_name>
                <report_id>errors-1</report_id>
                <date_range><begin>1737446400</begin><end>1737532800</end></date_range>
                <error>first error</error>
                <error>
                  second error
                </error>
                <generator>  {longGenerator}  </generator>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain>
                <p>none</p>
              </policy_published>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>1</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal(["first error", "second error"], result.ReportErrors);
        Assert.Equal(new string('g', 256), result.Generator);
        Assert.False(result.HasValidationErrors, string.Join(" | ", result.ValidationMessages));
        Assert.Empty(result.ValidationMessages);
    }

    /// <summary>
    /// DmarcRua's policy_published enums have no catch-all member, so a value outside them
    /// used to throw in the serializer and lose the whole report. For the optional tags no
    /// substitute is honest — sp=none on a p=reject domain reads as a regression — so the
    /// tag is removed and reads as absent, which for sp means "inherits p".
    /// </summary>
    [Theory]
    [InlineData("sp", "bogus")]
    [InlineData("np", "bogus")]
    [InlineData("testing", "maybe")]
    [InlineData("discovery_method", "guess")]
    public void Parse_RemovesAnUnusableOptionalPolicyPublishedTagRatherThanLosingTheReport(string tag, string value)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>bad-tag</org_name>
                <report_id>bad-tag-1</report_id>
                <date_range><begin>1737446400</begin><end>1737532800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain>
                <adkim>r</adkim><aspf>r</aspf>
                <p>reject</p>
                <{tag}>{value}</{tag}>
              </policy_published>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>1</count>
                  <policy_evaluated><disposition>reject</disposition><dkim>fail</dkim><spf>fail</spf></policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>fail</result></spf></auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal("reject", result.PublishedPolicy);
        Assert.Null(result.SubdomainPolicy);
        Assert.Null(result.NonexistentSubdomainPolicy);
        Assert.Null(result.Testing);
        Assert.Null(result.DiscoveryMethod);
        Assert.Single(result.Records);
        Assert.False(result.HasValidationErrors, string.Join(" | ", result.ValidationMessages));
        Assert.Equal(
            [DmarcReportParseMessage.Warning($"removed unusable policy_published/{tag} value '{value}' for compatibility")],
            result.ValidationMessages);
    }

    /// <summary>
    /// p is required, so it cannot be removed; none is what MapDisposition already reads
    /// for anything that is not reject or quarantine.
    /// </summary>
    [Fact]
    public void Parse_ReplacesAnUnusablePublishedPolicyWithNone()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>bad-p</org_name>
                <report_id>bad-p-1</report_id>
                <date_range><begin>1737446400</begin><end>1737532800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain>
                <p>bogus</p>
              </policy_published>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>1</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal("none", result.PublishedPolicy);
        Assert.Single(result.Records);
        Assert.Contains(
            DmarcReportParseMessage.Warning(
                "replaced unrecognised policy_published/p value 'bogus' with 'none' for compatibility"),
            result.ValidationMessages);
    }

    /// <summary>
    /// A root namespace nobody defined is not a format the parser can claim to read. It
    /// used to be stripped and parsed as RFC 7489; now the report is rejected, which the
    /// ingest path counts as a parse failure like any other unreadable document.
    /// </summary>
    [Fact]
    public void Parse_UnknownRootNamespace_IsRejected()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback xmlns="urn:example:not-dmarc">
              <report_metadata>
                <org_name>unknown-ns</org_name>
                <report_id>unknown-ns-1</report_id>
                <date_range><begin>1737446400</begin><end>1737532800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain>
                <p>none</p>
              </policy_published>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>1</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var ex = Assert.ThrowsAny<InvalidOperationException>(() => _parser.Parse(stream));

        Assert.Contains("urn:example:not-dmarc", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// RFC 9990 says a receiver ignores extensions it does not understand. Both places one
    /// can appear — the file-level <c>extension</c> wrapper and bare foreign-namespace
    /// elements after a record's <c>auth_results</c> — are removed before validation, so a
    /// report carrying them has nothing to say about it. In a report with no namespace, an
    /// element in any namespace is foreign.
    /// </summary>
    [Fact]
    public void Parse_ForeignNamespaceExtensions_RaiseNoMessages()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback xmlns:ext="https://ext.example.org/dmarc-extension/1">
              <report_metadata>
                <org_name>extensions</org_name>
                <report_id>extensions-1</report_id>
                <date_range><begin>1737446400</begin><end>1737532800</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain>
                <p>none</p>
              </policy_published>
              <extension>
                <ext:report_note definition="https://ext.example.org/dmarc-extension/1">file-level</ext:report_note>
              </extension>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>7</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>pass</result></spf></auth_results>
                <ext:record_note definition="https://ext.example.org/dmarc-extension/1">record-level</ext:record_note>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal(7, Assert.Single(result.Records).MessageCount);
        Assert.False(result.HasValidationErrors, string.Join(" | ", result.ValidationMessages));
        Assert.False(result.HasValidationWarnings, string.Join(" | ", result.ValidationMessages));
        Assert.Empty(result.ValidationMessages);
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
                    <disposition>pass</disposition>
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
        Assert.Equal("pass", result.Records[0].Disposition);
        Assert.Contains(result.ValidationMessages, x => x.Text.Contains("stripped XML namespace 'urn:ietf:params:xml:ns:dmarc-2.0'", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.ValidationMessages,
            x => x.Text.Contains("policy_evaluated/disposition", StringComparison.Ordinal));
    }

    /// <summary>
    /// The captured action dispositions are matched to records by position, so a one-record
    /// report cannot show they land on the right record. Three records, three different values.
    /// </summary>
    [Fact]
    public void Parse_WithDmarcBisNamespace_KeepsEachRecordsOwnDisposition()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback xmlns="urn:ietf:params:xml:ns:dmarc-2.0">
              <version>1.0</version>
              <report_metadata>
                <org_name>bis-test</org_name>
                <email>noreply@example.com</email>
                <report_id>bis-2</report_id>
                <date_range>
                  <begin>1737446400</begin>
                  <end>1737532800</end>
                </date_range>
              </report_metadata>
              <policy_published>
                <domain>example.com</domain>
                <adkim>r</adkim>
                <aspf>r</aspf>
                <p>reject</p>
                <pct>100</pct>
              </policy_published>
              <record>
                <row>
                  <source_ip>192.0.2.1</source_ip>
                  <count>1</count>
                  <policy_evaluated>
                    <disposition>reject</disposition>
                    <dkim>fail</dkim>
                    <spf>fail</spf>
                  </policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>fail</result></spf></auth_results>
              </record>
              <record>
                <row>
                  <source_ip>192.0.2.2</source_ip>
                  <count>2</count>
                  <policy_evaluated>
                    <disposition>pass</disposition>
                    <dkim>pass</dkim>
                    <spf>pass</spf>
                  </policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>pass</result></spf></auth_results>
              </record>
              <record>
                <row>
                  <source_ip>192.0.2.3</source_ip>
                  <count>3</count>
                  <policy_evaluated>
                    <disposition>quarantine</disposition>
                    <dkim>fail</dkim>
                    <spf>pass</spf>
                  </policy_evaluated>
                </row>
                <identifiers><header_from>example.com</header_from></identifiers>
                <auth_results><spf><domain>example.com</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal(
            ["192.0.2.1=reject", "192.0.2.2=pass", "192.0.2.3=quarantine"],
            result.Records.Select(x => $"{x.SourceIp}={x.Disposition}").ToArray());
        Assert.DoesNotContain(
            result.ValidationMessages,
            x => x.Text.Contains("policy_evaluated/disposition", StringComparison.Ordinal));
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
    /// among them, 1.5% of the reports vendored in DmarcRua's own test resources. Every other
    /// test here supplies both tags, so without these cases the suite goes green on an
    /// upgrade that would break production: that is exactly what 2.0.1 did, where reading the
    /// computed Adkim/Aspf properties threw ArgumentNullException on an absent tag.
    ///
    /// 2.1.0 fixed that (danielsen/DmarcRua#11) and the parser reads those properties again,
    /// so this now guards the library rather than a workaround around it. The cases that
    /// matter most are the ones where DmarcRua returns null for three different reasons —
    /// absent, present-but-empty, and unrecognised — because relaxed is this project's
    /// reading of all three, not something the library decides.
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
