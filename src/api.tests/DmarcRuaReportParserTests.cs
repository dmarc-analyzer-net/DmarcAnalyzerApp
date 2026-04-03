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
        Assert.Equal(new DateTime(2025, 1, 21, 8, 0, 0, DateTimeKind.Utc), result.RangeBeginUtc);
        Assert.Equal(new DateTime(2025, 1, 22, 8, 0, 0, DateTimeKind.Utc), result.RangeEndUtc);
        Assert.False(result.HasValidationErrors);
    }

    [Fact]
    public void Parse_WithUnreadableStream_Throws()
    {
        using var stream = new NonReadableStream();

        var ex = Assert.Throws<ArgumentException>(() => _parser.Parse(stream));

        Assert.Contains("readable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_WithHeloScope_NormalizesAndParses()
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
        Assert.Contains(result.ValidationMessages, x => x.Contains("normalized SPF scope value 'helo'", StringComparison.Ordinal));
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
