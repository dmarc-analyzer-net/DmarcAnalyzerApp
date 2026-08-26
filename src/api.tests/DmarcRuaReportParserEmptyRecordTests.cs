using DmarcAnalyzer.Api.Application.Reports;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// Records that report no sender and no mail. Reported as #190: the domain's sources table
/// showed a blank row with zeroes across it that answered 400 when expanded, because an empty
/// SourceIp is a group of its own in the analytics aggregation and source-detail has no IP to
/// query by.
/// <para>
/// Two origins, one end state, both dropped. The reported one is a reporter sending an empty
/// record on purpose (<see cref="Parse_DropsTheEmptyHeartbeatReportsObservedInIssue190"/>) —
/// nothing failed to parse there. The rest are rows DmarcRua could not fill at all, each
/// confirmed against 2.0.1 to yield <c>SourceIp=""</c> and <c>MessageCount=0</c>.
/// </para>
/// </summary>
public sealed class DmarcRuaReportParserEmptyRecordTests
{
    private readonly DmarcRuaReportParser _parser = new();

    private const string HealthyRecord = """
          <record>
            <row><source_ip>192.0.2.1</source_ip><count>3</count>
              <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
            </row>
            <identifiers><header_from>acme.example</header_from></identifiers>
            <auth_results><spf><domain>acme.example</domain><result>pass</result></spf></auth_results>
          </record>
        """;

    private static Stream ReportWith(params string[] records)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback>
              <report_metadata>
                <org_name>example.net</org_name>
                <report_id>empty-record</report_id>
                <date_range><begin>1737676800</begin><end>1737763199</end></date_range>
              </report_metadata>
              <policy_published>
                <domain>acme.example</domain><adkim>r</adkim><aspf>r</aspf>
                <p>reject</p><pct>100</pct>
              </policy_published>
            {string.Join("\n", records)}
            </feedback>
            """;
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
    }

    /// <summary>source_ip and count as direct children of record, with no row wrapping them.</summary>
    private const string NoRowElement = """
          <record>
            <source_ip>192.0.2.9</source_ip><count>5</count>
            <identifiers><header_from>acme.example</header_from></identifiers>
            <auth_results><spf><domain>acme.example</domain><result>fail</result></spf></auth_results>
          </record>
        """;

    /// <summary>An empty row element.</summary>
    private const string EmptyRow = """
          <record>
            <row/>
            <identifiers><header_from>acme.example</header_from></identifiers>
            <auth_results><spf><domain>acme.example</domain><result>fail</result></spf></auth_results>
          </record>
        """;

    /// <summary>Row spelled with different case. XmlSerializer matches element names case-sensitively.</summary>
    private const string MiscasedRow = """
          <record>
            <Row><Source_Ip>192.0.2.9</Source_Ip><Count>5</Count></Row>
            <identifiers><header_from>acme.example</header_from></identifiers>
            <auth_results><spf><domain>acme.example</domain><result>fail</result></spf></auth_results>
          </record>
        """;

    [Theory]
    [InlineData(NoRowElement)]
    [InlineData(EmptyRow)]
    [InlineData(MiscasedRow)]
    public void Parse_DropsARecordWhoseRowCouldNotBeRead(string unfillableRecord)
    {
        using var stream = ReportWith(HealthyRecord, unfillableRecord);

        var result = _parser.Parse(stream);

        var record = Assert.Single(result.Records);
        Assert.Equal("192.0.2.1", record.SourceIp);
        Assert.DoesNotContain(result.Records, x => x.SourceIp.Length == 0);
    }

    /// <summary>
    /// The reports #190 was actually about, as posted in the thread (policy domain redacted
    /// there too). wp.pl and o2.pl are one operator, which is why the blank row claimed two
    /// reporters. Nothing here failed to parse: the row is present and complete, and the
    /// reporter is stating that it saw no mail for the window. One record in, none stored.
    /// <para>
    /// The disposition and both policy results arrive as `none`/`fail` rather than empty,
    /// because the EnumRepairs pass rewrites the empty elements first — worth knowing if this
    /// test is ever repointed at the record's contents instead of its absence.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("wp.pl", "1787643920.665812765")]
    [InlineData("o2.pl", "1787643921.168147550")]
    public void Parse_DropsTheEmptyHeartbeatReportsObservedInIssue190(string org, string reportId)
    {
        var xml =
            $"<feedback><report_metadata><date_range><begin>1787522400</begin>" +
            $"<end>1787608800</end></date_range><org_name>{org}</org_name>" +
            $"<email>dmarc-support@{org}</email><report_id>{reportId}</report_id></report_metadata>" +
            "<policy_published><domain>acme.example</domain><adkim>r</adkim><aspf>r</aspf>" +
            "<p>none</p><sp>none</sp><pct>100</pct></policy_published>" +
            "<record><row><source_ip></source_ip><count>0</count>" +
            "<policy_evaluated><disposition></disposition><dkim></dkim><spf></spf></policy_evaluated></row>" +
            "<identifiers><header_from></header_from></identifiers>" +
            "<auth_results><spf><domain></domain><result></result></spf></auth_results></record></feedback>";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = _parser.Parse(stream);

        Assert.Equal(org, result.OrganizationName);
        Assert.Equal(reportId, result.ReportId);
        Assert.Empty(result.Records);

        // The report is still a report: it is ingested, and its own record total is kept.
        Assert.Equal(1, result.RecordCount);
        Assert.Contains(
            result.ValidationMessages,
            x => x.Contains("dropped 1 record(s)", StringComparison.Ordinal));
    }

    /// <summary>
    /// The reporter's own record total is kept, so a report this fired on stays traceable
    /// afterwards: RecordCount exceeds the number of rows stored for it.
    /// </summary>
    [Fact]
    public void Parse_KeepsTheReportedRecordCountWhenARecordIsDropped()
    {
        using var stream = ReportWith(HealthyRecord, EmptyRow);

        var result = _parser.Parse(stream);

        Assert.Equal(2, result.RecordCount);
        Assert.Single(result.Records);
    }

    /// <summary>Dropping a record silently would be untraceable. It is named and counted.</summary>
    [Fact]
    public void Parse_RecordsAWarningNamingHowManyWereDropped()
    {
        using var stream = ReportWith(HealthyRecord, EmptyRow, NoRowElement);

        var result = _parser.Parse(stream);

        Assert.True(result.HasValidationWarnings);
        Assert.Contains(
            result.ValidationMessages,
            x => x.Contains("dropped 2 record(s)", StringComparison.Ordinal)
                 && x.Contains("no source IP and no messages", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of the condition. A blank source_ip with a real count is mail that
    /// arrived and was reported: dropping it would under-count the domain's volume and the
    /// compliance denominator, so it stays and the console renders it unattributed.
    /// </summary>
    [Fact]
    public void Parse_KeepsARecordWithNoSourceIpButRealMessages()
    {
        using var stream = ReportWith("""
              <record>
                <row><source_ip></source_ip><count>4</count>
                  <policy_evaluated><disposition>none</disposition><dkim>fail</dkim><spf>fail</spf></policy_evaluated>
                </row>
                <identifiers><header_from>acme.example</header_from></identifiers>
                <auth_results><spf><domain>acme.example</domain><result>fail</result></spf></auth_results>
              </record>
            """);

        var result = _parser.Parse(stream);

        var record = Assert.Single(result.Records);
        Assert.Equal(string.Empty, record.SourceIp);
        Assert.Equal(4, record.MessageCount);
    }

    /// <summary>
    /// A pretty-printing reporter puts the newline and indentation inside the element. Stored
    /// untrimmed, that source could never be looked up again: source-detail trims the ip it is
    /// given before matching this column.
    /// </summary>
    [Fact]
    public void Parse_TrimsWhitespaceAroundASourceIp()
    {
        using var stream = ReportWith("""
              <record>
                <row>
                  <source_ip>
                    192.0.2.7
                  </source_ip>
                  <count>2</count>
                  <policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated>
                </row>
                <identifiers><header_from>acme.example</header_from></identifiers>
                <auth_results><spf><domain>acme.example</domain><result>pass</result></spf></auth_results>
              </record>
            """);

        var result = _parser.Parse(stream);

        Assert.Equal("192.0.2.7", Assert.Single(result.Records).SourceIp);
    }

    /// <summary>A report with nothing wrong must not gain the warning.</summary>
    [Fact]
    public void Parse_WithNoUnreadableRows_RaisesNoDropWarning()
    {
        using var stream = ReportWith(HealthyRecord);

        var result = _parser.Parse(stream);

        Assert.Single(result.Records);
        Assert.DoesNotContain(result.ValidationMessages, x => x.Contains("dropped", StringComparison.Ordinal));
    }
}
