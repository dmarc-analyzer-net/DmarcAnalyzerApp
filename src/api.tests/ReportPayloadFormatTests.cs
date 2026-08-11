using System.Text;
using DmarcAnalyzer.Api.Application.Reports;
using Xunit;

namespace DmarcAnalyzer.Api.Tests;

/// <summary>
/// The regression these guard: TLS reports (RFC 8460) arrive in the same mailbox
/// as DMARC reports, normally gzipped as application/tlsrpt+gzip. Extraction
/// detects gzip by magic bytes and returned whatever was inside without checking
/// it, so the JSON reached the DMARC parser, threw, and incremented the
/// parse-failure counter that marks a report source unhealthy in the console.
/// </summary>
public sealed class ReportPayloadFormatTests
{
    private static ReportPayloadKind Classify(string content, string? file = null, string? mime = null)
        => ReportPayloadFormat.Classify(Encoding.UTF8.GetBytes(content), file, mime);

    [Fact]
    public void RealTlsReportBodyIsRecognisedAsTls()
    {
        // Shape taken from RFC 8460 §4.4.
        const string tls = """
            {
              "organization-name": "Company-X",
              "date-range": { "start-datetime": "2016-04-01T00:00:00Z", "end-datetime": "2016-04-01T23:59:59Z" },
              "contact-info": "sts-reporting@company-x.example",
              "report-id": "5065427c-23d3-47ca-b6e0-946ea0e8c4be",
              "policies": [{ "policy": { "policy-type": "sts" },
                             "summary": { "total-successful-session-count": 5326,
                                          "total-failure-session-count": 303 } }]
            }
            """;
        Assert.Equal(ReportPayloadKind.SmtpTlsReportJson, Classify(tls));
    }

    [Fact]
    public void RealDmarcReportBodyIsStillRecognisedAsDmarc()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feedback><report_metadata><org_name>google.com</org_name></report_metadata></feedback>
            """;
        Assert.Equal(ReportPayloadKind.DmarcAggregateXml, Classify(xml));
    }

    [Theory]
    // Content decides, even when the filename says otherwise — senders misname attachments.
    [InlineData("{\"report-id\":\"x\"}", "report.xml", "application/xml", ReportPayloadKind.SmtpTlsReportJson)]
    [InlineData("<feedback/>", "report.json", "application/tlsrpt+json", ReportPayloadKind.DmarcAggregateXml)]
    // A UTF-8 BOM and leading whitespace must not hide the first real character.
    [InlineData("﻿  \n{\"a\":1}", null, null, ReportPayloadKind.SmtpTlsReportJson)]
    [InlineData("﻿\r\n<feedback/>", null, null, ReportPayloadKind.DmarcAggregateXml)]
    public void ContentWinsOverFilenameAndMimeType(
        string body, string? file, string? mime, ReportPayloadKind expected)
        => Assert.Equal(expected, Classify(body, file, mime));

    [Theory]
    // Only when the bytes are inconclusive do the labels get a vote.
    [InlineData("", "report.json", null, ReportPayloadKind.SmtpTlsReportJson)]
    [InlineData("", null, "application/tlsrpt+gzip", ReportPayloadKind.SmtpTlsReportJson)]
    [InlineData("", "report.xml", null, ReportPayloadKind.DmarcAggregateXml)]
    [InlineData("", null, "text/xml", ReportPayloadKind.DmarcAggregateXml)]
    public void LabelsDecideOnlyWhenTheBytesDoNot(
        string body, string? file, string? mime, ReportPayloadKind expected)
        => Assert.Equal(expected, Classify(body, file, mime));

    [Theory]
    [InlineData("", null, null)]
    [InlineData("   ", null, null)]
    [InlineData("not a report at all", "notes.txt", "text/plain")]
    public void AnythingElseIsUnknownAndGetsIgnored(string body, string? file, string? mime)
        => Assert.Equal(ReportPayloadKind.Unknown, Classify(body, file, mime));
}
