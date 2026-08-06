using System.Text;
using DmarcAnalyzer.Api.Application.Reports;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The RFC 8460 parser against on-disk fixtures, asserting every mapped field.
/// The leniency table is the point: reporters disagree with the RFC's own
/// example (mx-host vs mx-host-pattern), send counts as strings, and invent
/// result types — none of which may cost a report.
/// </summary>
public sealed class TlsRptReportParserTests
{
    private static Stream OpenFixture(string name)
        => File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static Stream FromText(string json)
        => new MemoryStream(Encoding.UTF8.GetBytes(json));

    private static readonly TlsRptReportParser Parser = new();

    [Fact]
    public void Rfc8460Example_ParsesEveryField()
    {
        using var stream = OpenFixture("sample-rfc8460-tls.json");
        var result = Parser.Parse(stream);

        Assert.Equal("Company-X", result.OrganizationName);
        Assert.Equal("5065427c-23d3-47ca-b6e0-946ea0e8c4be", result.ReportId);
        Assert.Equal("sts-reporting@company-x.example", result.ContactInfo);
        Assert.Equal(new DateTime(2016, 4, 1, 0, 0, 0, DateTimeKind.Utc), result.RangeBeginUtc);
        Assert.Equal(new DateTime(2016, 4, 1, 23, 59, 59, DateTimeKind.Utc), result.RangeEndUtc);
        Assert.Empty(result.ValidationMessages);

        var policy = Assert.Single(result.Policies);
        Assert.Equal("sts", policy.PolicyType);
        Assert.Equal("company-y.example", policy.PolicyDomain);
        Assert.Equal("version: STSv1\nmode: testing\nmx: *.mail.company-y.example\nmax_age: 86400", policy.PolicyString);
        Assert.Equal("*.mail.company-y.example", policy.MxHostPatterns); // the RFC example's bare mx-host key
        Assert.Equal(5326, policy.SuccessfulSessionCount);
        Assert.Equal(303, policy.FailureSessionCount);

        Assert.Equal(3, policy.FailureDetails.Count);
        var expired = policy.FailureDetails[0];
        Assert.Equal("certificate-expired", expired.ResultType);
        Assert.Equal("2001:db8:abcd:0012::1", expired.SendingMtaIp);
        Assert.Equal("mx1.mail.company-y.example", expired.ReceivingMxHostname);
        Assert.Equal(100, expired.FailedSessionCount);

        var starttls = policy.FailureDetails[1];
        Assert.Equal("starttls-not-supported", starttls.ResultType);
        Assert.Equal("203.0.113.56", starttls.ReceivingIp);
        Assert.Equal(200, starttls.FailedSessionCount);
        Assert.Contains("report_info", starttls.AdditionalInformation);

        var validation = policy.FailureDetails[2];
        Assert.Equal("validation-failure", validation.ResultType);
        Assert.Equal("X509_V_ERR_PROXY_PATH_LENGTH_EXCEEDED", validation.FailureReasonCode);
        Assert.Equal(3, validation.FailedSessionCount);
    }

    [Fact]
    public void MultiPolicyReport_KeepsEveryDomain_InOrder()
    {
        using var stream = OpenFixture("sample-google-tls.json");
        var result = Parser.Parse(stream);

        Assert.Equal(3, result.Policies.Count);
        Assert.Equal(["acme.example", "beta.example", "acme.example"],
            result.Policies.Select(p => p.PolicyDomain).ToArray());
        Assert.Equal(["sts", "no-policy-found", "tlsa"],
            result.Policies.Select(p => p.PolicyType).ToArray());

        // mx-host-pattern as an array joins with newlines.
        Assert.Equal("mx1.acme.example\n*.mail.acme.example", result.Policies[0].MxHostPatterns);
        // A success-only policy simply has no details — not a warning.
        Assert.Empty(result.Policies[1].FailureDetails);
        Assert.Empty(result.ValidationMessages);
    }

    [Fact]
    public void QuirkyReporter_IsToleratedWithMessages()
    {
        using var stream = OpenFixture("sample-lenient-tls.json");
        var result = Parser.Parse(stream);

        Assert.Null(result.ContactInfo);

        // The domainless second policy is dropped with a message; the first survives.
        var policy = Assert.Single(result.Policies);
        Assert.Contains(result.ValidationMessages, m => m.Contains("policy-domain"));

        Assert.Equal("sts", policy.PolicyType);                 // lowercased
        Assert.Equal("acme.example", policy.PolicyDomain);      // case + trailing dot normalized
        Assert.Equal(42, policy.SuccessfulSessionCount);        // string counts parsed
        Assert.Equal(7, policy.FailureSessionCount);

        Assert.Equal(2, policy.FailureDetails.Count);
        Assert.Equal("quantum-handshake-flux", policy.FailureDetails[0].ResultType); // unknown kept raw
        Assert.Equal(5, policy.FailureDetails[0].FailedSessionCount);
        // A present detail row asserts at least one failure; missing count reads as 1.
        Assert.Equal(1, policy.FailureDetails[1].FailedSessionCount);
        Assert.Equal("mx1.acme.example", policy.FailureDetails[1].ReceivingMxHostname);
        Assert.Contains(result.ValidationMessages, m => m.Contains("failed-session-count"));
    }

    [Fact]
    public void SuccessOnlyReport_HasNoDetailsAndNoWarnings()
    {
        using var stream = OpenFixture("sample-successonly-tls.json");
        var result = Parser.Parse(stream);

        var policy = Assert.Single(result.Policies);
        Assert.Equal(5000, policy.SuccessfulSessionCount);
        Assert.Equal(0, policy.FailureSessionCount);
        Assert.Empty(policy.FailureDetails);
        Assert.Empty(result.ValidationMessages);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"organization-name\": \"x\", \"date-range\": {\"start-datetime\": \"2026-08-01T00:00:00Z\", \"end-datetime\": \"2026-08-01T23:59:59Z\"}}")]  // no report-id
    [InlineData("{\"report-id\": \"x\"}")]                                                       // no date-range
    [InlineData("{\"report-id\": \"x\", \"date-range\": {\"start-datetime\": \"whenever\", \"end-datetime\": \"later\"}}")] // unparseable dates
    public void UnusableReports_Throw(string json)
    {
        using var stream = FromText(json);
        Assert.Throws<FormatException>(() => Parser.Parse(stream));
    }

    [Fact]
    public void MissingPoliciesArray_StoresTheReportWithAMessage()
    {
        using var stream = FromText(
            "{\"organization-name\": \"x\", \"report-id\": \"r1\", " +
            "\"date-range\": {\"start-datetime\": \"2026-08-01T00:00:00Z\", \"end-datetime\": \"2026-08-01T23:59:59Z\"}}");
        var result = Parser.Parse(stream);

        Assert.Empty(result.Policies);
        Assert.Contains(result.ValidationMessages, m => m.Contains("policies"));
    }

    [Fact]
    public void OffsetTimestamps_NormalizeToUtc()
    {
        using var stream = FromText(
            "{\"organization-name\": \"x\", \"report-id\": \"r1\", " +
            "\"date-range\": {\"start-datetime\": \"2026-08-01T02:00:00+02:00\", \"end-datetime\": \"2026-08-02T01:59:59+02:00\"}, " +
            "\"policies\": []}");
        var result = Parser.Parse(stream);

        Assert.Equal(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), result.RangeBeginUtc);
        Assert.Equal(new DateTime(2026, 8, 1, 23, 59, 59, DateTimeKind.Utc), result.RangeEndUtc);
    }
}
