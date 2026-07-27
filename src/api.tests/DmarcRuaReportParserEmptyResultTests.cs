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
}
