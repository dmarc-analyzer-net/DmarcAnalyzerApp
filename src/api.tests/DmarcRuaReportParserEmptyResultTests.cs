using DmarcAnalyzer.Api.Application.Reports;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// A reporter sending an empty policy_evaluated result used to cost the whole report.
/// DMARCResultType is a strict enum, so XmlSerializer rejected '' and the entire
/// &lt;feedback&gt; document failed — taking every valid record in it, 28 on average, and
/// about 1.5% of attachments. These cases pin the repair and, more importantly, pin that
/// the rest of the report still arrives intact.
/// </summary>
public sealed class DmarcRuaReportParserEmptyResultTests
{
    private readonly DmarcRuaReportParser _parser = new();

    /// <summary>Two records: the first carries the supplied policy_evaluated, the second is valid.</summary>
    private static Stream ReportWith(string policyEvaluated)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>example.net</org_name>
                <report_id>empty-result</report_id>
                <date_range><begin>1737676800</begin><end>1737763199</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>acme.example</domain><adkim>r</adkim><aspf>r</aspf>
                <p>reject</p><pct>100</pct>
              </policy_published>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>7</count>
                  <policy_evaluated>{policyEvaluated}</policy_evaluated>
                </row>
                <identifiers><header_from>acme.example</header_from></identifiers>
                <auth_results><spf><domain>acme.example</domain><result>pass</result></spf></auth_results>
              </record>
              <record>
                <row><source_ip>192.0.2.9</source_ip><count>3</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>acme.example</header_from></identifiers>
                <auth_results><spf><domain>acme.example</domain><result>pass</result></spf></auth_results>
              </record>
            </feedback>
            """;
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
    }

    [Theory]
    [InlineData("<disposition>none</disposition><dkim></dkim><spf>fail</spf>")]
    [InlineData("<disposition>none</disposition><dkim/><spf>fail</spf>")]
    [InlineData("<disposition>none</disposition><dkim>  </dkim><spf>fail</spf>")]
    public void Parse_WithEmptyDkim_ReadsItAsFail(string policyEvaluated)
    {
        using var stream = ReportWith(policyEvaluated);

        var result = _parser.Parse(stream);

        var record = Assert.Single(result.Records, x => x.SourceIp == "192.0.2.1");
        Assert.Equal("fail", record.DkimResult);
        Assert.Equal("fail", record.SpfResult);
    }

    [Theory]
    [InlineData("<disposition>none</disposition><dkim>fail</dkim><spf></spf>")]
    [InlineData("<disposition>none</disposition><dkim>fail</dkim><spf/>")]
    public void Parse_WithEmptySpf_ReadsItAsFail(string policyEvaluated)
    {
        using var stream = ReportWith(policyEvaluated);

        var result = _parser.Parse(stream);

        var record = Assert.Single(result.Records, x => x.SourceIp == "192.0.2.1");
        Assert.Equal("fail", record.SpfResult);
        Assert.Equal("fail", record.DkimResult);
    }

    /// <summary>
    /// The point of the fix. Before it, one malformed record discarded the report, so the
    /// valid records alongside it were lost too.
    /// </summary>
    [Fact]
    public void Parse_WithOneEmptyResult_StillReturnsTheOtherRecords()
    {
        using var stream = ReportWith("<disposition>none</disposition><dkim></dkim><spf>fail</spf>");

        var result = _parser.Parse(stream);

        Assert.Equal(2, result.Records.Count);
        Assert.Equal(2, result.RecordCount);

        // The untouched record must be unaffected by the repair.
        var healthy = Assert.Single(result.Records, x => x.SourceIp == "192.0.2.9");
        Assert.Equal("pass", healthy.DkimResult);
        Assert.Equal("pass", healthy.SpfResult);
        Assert.Equal(3, healthy.MessageCount);
    }

    /// <summary>Silently inventing a verdict would be worse than the crash. It is recorded.</summary>
    [Fact]
    public void Parse_WithEmptyResult_RecordsAWarning()
    {
        using var stream = ReportWith("<disposition>none</disposition><dkim></dkim><spf>fail</spf>");

        var result = _parser.Parse(stream);

        Assert.True(result.HasValidationWarnings);
        Assert.Contains(
            result.ValidationMessages,
            x => x.Contains("policy_evaluated", StringComparison.OrdinalIgnoreCase)
                 && x.Contains("fail", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A report with nothing wrong must not be rewritten, and must not gain a warning that
    /// would make every clean report look repaired.
    /// </summary>
    [Fact]
    public void Parse_WithPopulatedResults_IsNotNormalized()
    {
        using var stream = ReportWith("<disposition>none</disposition><dkim>pass</dkim><spf>fail</spf>");

        var result = _parser.Parse(stream);

        var record = Assert.Single(result.Records, x => x.SourceIp == "192.0.2.1");
        Assert.Equal("pass", record.DkimResult);
        Assert.Equal("fail", record.SpfResult);
        Assert.DoesNotContain(
            result.ValidationMessages,
            x => x.Contains("policy_evaluated", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// auth_results has its own dkim and spf elements. They are complex types wrapping a
    /// nested result, so the repair must not touch them — narrowing to direct children of
    /// policy_evaluated is what keeps this true.
    /// </summary>
    [Fact]
    public void Parse_DoesNotRewriteAuthResultsDkim()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>example.net</org_name>
                <report_id>auth-results-untouched</report_id>
                <date_range><begin>1737676800</begin><end>1737763199</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>acme.example</domain><adkim>r</adkim><aspf>r</aspf>
                <p>reject</p><pct>100</pct>
              </policy_published>
              <record>
                <row><source_ip>192.0.2.1</source_ip><count>7</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>acme.example</header_from></identifiers>
                <auth_results>
                  <dkim><domain>acme.example</domain><selector>s1</selector><result>pass</result></dkim>
                  <spf><domain>acme.example</domain><result>pass</result></spf>
                </auth_results>
              </record>
            </feedback>
            """;
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        var record = Assert.Single(result.Records);
        var dkim = Assert.Single(record.DkimAuthResults);
        Assert.Equal("acme.example", dkim.Domain);
        Assert.Equal("s1", dkim.Selector);
        Assert.Equal("pass", dkim.Result);
    }

    /// <summary>
    /// 'unknown' is the RFC 4408 name for what RFC 7208 calls permerror. Seen in a real
    /// mailbox, and fatal before the repair: SpfResultType has no such member.
    /// </summary>
    [Theory]
    [InlineData("unknown", "permerror")]
    [InlineData("error", "temperror")]
    public void Parse_TranslatesLegacySpfResultNames(string reported, string expected)
    {
        var xml = ReportWithSpfAuthResult(reported);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        var spf = Assert.Single(Assert.Single(result.Records).SpfAuthResults);
        Assert.Equal(expected, spf.Result);
    }

    /// <summary>A value no version of the spec ever defined still must not cost the report.</summary>
    [Fact]
    public void Parse_WithNonsenseSpfResult_FallsBackToPermerrorAndKeepsTheReport()
    {
        var xml = ReportWithSpfAuthResult("wat");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        var spf = Assert.Single(Assert.Single(result.Records).SpfAuthResults);
        Assert.Equal("permerror", spf.Result);
        Assert.Contains(result.ValidationMessages, x => x.Contains("'wat'", StringComparison.Ordinal));
    }

    /// <summary>A reporter putting a number in disposition. Observed as '15'.</summary>
    [Fact]
    public void Parse_WithNumericDisposition_FallsBackToNone()
    {
        using var stream = ReportWith("<disposition>15</disposition><dkim>pass</dkim><spf>pass</spf>");

        var result = _parser.Parse(stream);

        Assert.Equal(2, result.Records.Count);
        var record = Assert.Single(result.Records, x => x.SourceIp == "192.0.2.1");
        Assert.Equal("none", record.Disposition);
        Assert.Contains(result.ValidationMessages, x => x.Contains("'15'", StringComparison.Ordinal));
    }

    /// <summary>The substituted value has to be named, or the repair is untraceable.</summary>
    [Fact]
    public void Parse_NamesTheOffendingValueInTheWarning()
    {
        using var stream = ReportWith("<disposition>none</disposition><dkim>bogus</dkim><spf>pass</spf>");

        var result = _parser.Parse(stream);

        Assert.Contains(
            result.ValidationMessages,
            x => x.Contains("policy_evaluated/dkim", StringComparison.Ordinal)
                 && x.Contains("'bogus'", StringComparison.Ordinal)
                 && x.Contains("'fail'", StringComparison.Ordinal));
    }

    /// <summary>Values the enums genuinely accept must survive untouched.</summary>
    [Theory]
    [InlineData("softfail")]
    [InlineData("temperror")]
    [InlineData("neutral")]
    public void Parse_LeavesValidSpfResultsAlone(string reported)
    {
        var xml = ReportWithSpfAuthResult(reported);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        var spf = Assert.Single(Assert.Single(result.Records).SpfAuthResults);
        Assert.Equal(reported, spf.Result);
        Assert.DoesNotContain(result.ValidationMessages, x => x.Contains("unrecognised", StringComparison.Ordinal));
    }

    private static string ReportWithSpfAuthResult(string spfResult) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <feedback>
          <report_metadata>
            <org_name>example.net</org_name>
            <report_id>spf-result</report_id>
            <date_range><begin>1737676800</begin><end>1737763199</end></date_range>
          </report_metadata>
          <policy_published>
            <domain>acme.example</domain><adkim>r</adkim><aspf>r</aspf>
            <p>reject</p><pct>100</pct>
          </policy_published>
          <record>
            <row><source_ip>192.0.2.1</source_ip><count>7</count>
              <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
            </row>
            <identifiers><header_from>acme.example</header_from></identifiers>
            <auth_results>
              <spf><domain>acme.example</domain><result>{spfResult}</result></spf>
            </auth_results>
          </record>
        </feedback>
        """;

    /// <summary>
    /// Documents a lossy mapping inside DmarcRua rather than anything this parser does.
    /// SpfResultType aliases its members — PermError and HardFail are both 6, None and
    /// Default are both 0 — so 'hardfail' round-trips as 'permerror' and an empty result
    /// as 'none'. The repair correctly leaves both alone (no warning is raised); the value
    /// changes underneath us in the serializer. Worth pinning so a future reader does not
    /// go looking for the bug in the normalization.
    /// </summary>
    [Fact]
    public void Parse_HardfailIsAcceptedButSurfacesAsPermerror()
    {
        var xml = ReportWithSpfAuthResult("hardfail");
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        var spf = Assert.Single(Assert.Single(result.Records).SpfAuthResults);
        Assert.Equal("permerror", spf.Result);

        // The giveaway that this is not our substitution: no warning was raised.
        Assert.DoesNotContain(result.ValidationMessages, x => x.Contains("unrecognised", StringComparison.Ordinal));
    }

    // --- Truncated documents ---------------------------------------------------------
    //
    // One real reporter (plesk4.lg.dynavee.net) sends XML ending at "</feedback" — the final
    // '>' never arrives, and every one of its reports was discarded. The records themselves
    // are complete, so the document is completable; the guard is that nothing may be lost.

    private const string TruncatableReport = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feedback>
          <report_metadata>
            <org_name>plesk.example</org_name>
            <report_id>truncated</report_id>
            <date_range><begin>1737676800</begin><end>1737763199</end></date_range>
          </report_metadata>
          <policy_published>
            <domain>acme.example</domain><adkim>r</adkim><aspf>r</aspf>
            <p>reject</p><pct>100</pct>
          </policy_published>
          <record>
            <row><source_ip>192.0.2.1</source_ip><count>7</count>
              <policy_evaluated><disposition>15</disposition><spf>fail</spf><dkim>fail</dkim></policy_evaluated>
            </row>
            <identifiers><header_from>acme.example</header_from></identifiers>
            <auth_results><spf><domain>acme.example</domain><result>pass</result></spf></auth_results>
          </record>
        </feedback>
        """;

    [Fact]
    public void Parse_WithTruncatedRootTag_CompletesTheDocumentAndKeepsTheRecords()
    {
        // Exactly the observed corruption: drop the final '>'.
        var truncated = TruncatableReport.TrimEnd()[..^1];
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(truncated));

        var result = _parser.Parse(stream);

        var record = Assert.Single(result.Records);
        Assert.Equal("192.0.2.1", record.SourceIp);
        Assert.Equal(7, record.MessageCount);
        // The enum repair still runs, which it could not when the load failed.
        Assert.Equal("none", record.Disposition);
        Assert.Contains(result.ValidationMessages, x => x.Contains("truncated document", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_WithTruncationAfterAWholeRecord_Recovers()
    {
        // Cut immediately after </record>: the root is open, no record is.
        var truncated = TruncatableReport[..(TruncatableReport.IndexOf("</record>", StringComparison.Ordinal) + "</record>".Length)];
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(truncated));

        var result = _parser.Parse(stream);

        Assert.Single(result.Records);
        Assert.Contains(result.ValidationMessages, x => x.Contains("truncated document", StringComparison.Ordinal));
    }

    /// <summary>
    /// The guard. A report cut mid-record must still fail: completing it would ingest a
    /// partial report as whole, and the unique index would keep that partial version even if
    /// a complete copy arrived later.
    /// </summary>
    [Fact]
    public void Parse_WithTruncationInsideARecord_StillFails()
    {
        var truncated = TruncatableReport[..(TruncatableReport.IndexOf("<count>7</count>", StringComparison.Ordinal) + "<count>7</count>".Length)];
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(truncated));

        Assert.ThrowsAny<Exception>(() => _parser.Parse(stream));
    }

    /// <summary>A well-formed report must not be reported as truncated.</summary>
    [Fact]
    public void Parse_WithCompleteDocument_RaisesNoTruncationWarning()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(TruncatableReport));

        var result = _parser.Parse(stream);

        Assert.Single(result.Records);
        Assert.DoesNotContain(result.ValidationMessages, x => x.Contains("truncated", StringComparison.Ordinal));
    }
}
